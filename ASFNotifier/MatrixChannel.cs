using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ASFNotifier;

internal sealed class MatrixChannel : INotificationChannel {
	internal static readonly TimeSpan DegradedRetryGap = TimeSpan.FromMinutes(10);
	internal static readonly TimeSpan MaxRetryAfter = TimeSpan.FromSeconds(60);
	internal const int MaxResponseBytes = 64 * 1024;
	internal static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);

	private readonly string AccessToken;
	private readonly HttpClient HttpClient;
	private readonly string HomeserverUrl;
	private readonly Lock Lock = new();
	private readonly INotifierLog Log;
	private readonly string RoomId;
	private readonly Clock Clock;

	private bool CommandRoomRefused;
	private bool Degraded;
	private DateTimeOffset LastFailure;

	internal MatrixChannel(HttpClient httpClient, string homeserverUrl, string roomId, string accessToken, Clock clock, INotifierLog log) {
		HttpClient = httpClient;
		HomeserverUrl = homeserverUrl.TrimEnd('/');
		RoomId = roomId;
		AccessToken = accessToken;
		Clock = clock;
		Log = log;
	}

	// Built without any logging handler on purpose: it would see the bearer token
	internal static HttpClient CreateHttpClient(HttpMessageHandler? handler = null) => handler == null ? new HttpClient { Timeout = RequestTimeout } : new HttpClient(handler, false) { Timeout = RequestTimeout };

	public async Task<SendResult> SendAsync(FormattedMessage message, string transactionId, bool bypassDegraded, CancellationToken cancellationToken) {
		ArgumentNullException.ThrowIfNull(message);
		ArgumentException.ThrowIfNullOrEmpty(transactionId);

		if (!bypassDegraded) {
			lock (Lock) {
				if (Degraded && ((Clock.GetUtcNow() - LastFailure) < DegradedRetryGap)) {
					return SendResult.Dropped("degraded");
				}
			}
		}

		// A command reply goes to the command room, which may not be the notification room
		string roomId = message.RoomId ?? RoomId;

		using HttpRequestMessage request = new(HttpMethod.Put, new Uri($"{HomeserverUrl}/_matrix/client/v3/rooms/{Uri.EscapeDataString(roomId)}/send/m.room.message/{Uri.EscapeDataString(transactionId)}", UriKind.Absolute)) {
			Content = CreateBody(message)
		};

		request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", AccessToken);

		// HttpClient.Timeout stops at the headers once they are read separately; this one covers the body too
		using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		timeout.CancelAfter(RequestTimeout);

		try {
			// The body is capped here, not with HttpClient.MaxResponseContentBufferSize: ASF's trimmed Windows builds
			// drop that setter (issue #23). Too large throws HttpRequestException, like the setter did
			using HttpResponseMessage response = await HttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
			await response.Content.LoadIntoBufferAsync(MaxResponseBytes, timeout.Token).ConfigureAwait(false);

			string responseBody = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);

			return Interpret(response, responseBody, roomId);
		} catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) {
			return SendResult.Transient("timeout");
		} catch (HttpRequestException) {
			// The exception text can carry the URL; the short error is all anyone needs
			return SendResult.Transient("network error");
		}
	}

	// Written by hand: ASF's trimmed builds disable reflection-based JSON serialization (issue #23)
	private static ByteArrayContent CreateBody(FormattedMessage message) {
		using MemoryStream stream = new();

		using (Utf8JsonWriter writer = new(stream)) {
			writer.WriteStartObject();
			writer.WriteString("msgtype", "m.notice");
			writer.WriteString("body", message.PlainText);
			writer.WriteString("format", "org.matrix.custom.html");
			writer.WriteString("formatted_body", message.Html);

			if (message.InReplyTo != null) {
				writer.WritePropertyName("m.relates_to");
				writer.WriteStartObject();
				writer.WritePropertyName("m.in_reply_to");
				writer.WriteStartObject();
				writer.WriteString("event_id", message.InReplyTo);
				writer.WriteEndObject();
				writer.WriteEndObject();
			}

			writer.WriteEndObject();
		}

		ByteArrayContent content = new(stream.ToArray());
		content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };

		return content;
	}

	// Only an M_* style code is echoed, so a server that reflects the token in its error body cannot leak it
	internal static string? ReadErrorCode(JsonElement root) {
		if ((root.ValueKind != JsonValueKind.Object) || !root.TryGetMember("errcode", out JsonElement code) || (code.ValueKind != JsonValueKind.String)) {
			return null;
		}

		string? text = code.GetString();

		if (string.IsNullOrEmpty(text) || (text.Length > 64) || !text.StartsWith("M_", StringComparison.Ordinal)) {
			return null;
		}

		foreach (char c in text) {
			if (c is not ((>= 'A' and <= 'Z') or (>= '0' and <= '9') or '_')) {
				return null;
			}
		}

		return text;
	}

	private static TimeSpan? Cap(TimeSpan delay) => delay < TimeSpan.Zero ? TimeSpan.Zero : delay > MaxRetryAfter ? MaxRetryAfter : delay;

	private SendResult Interpret(HttpResponseMessage response, string responseBody, string roomId) {
		int status = (int) response.StatusCode;

		if (response.IsSuccessStatusCode) {
			lock (Lock) {
				if (roomId != RoomId) {
					CommandRoomRefused = false;
				}

				if (Degraded) {
					Degraded = false;
					Log.Info("Matrix delivery works again");
				}
			}

			return SendResult.Sent();
		}

		JsonElement root = default;

		try {
			using JsonDocument document = JsonDocument.Parse(responseBody);
			root = document.RootElement.Clone();
		} catch (JsonException) {
			// Not JSON: errcode and retry_after_ms are simply absent
		}

		string? errorCode = ReadErrorCode(root);
		string error = errorCode == null ? status.ToString(CultureInfo.InvariantCulture) : $"{status} {errorCode}";

		switch (response.StatusCode) {
			case HttpStatusCode.Forbidden when roomId != RoomId:
				// Only the command room refuses: that reply is dropped, notifications go on
				lock (Lock) {
					if (!CommandRoomRefused) {
						CommandRoomRefused = true;
						Log.Error($"Matrix refused a command reply ({error}, room {roomId}); replies there are dropped, notifications are not affected");
					}
				}

				return SendResult.Dropped(error);
			case HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden:
				EnterDegraded(error, response.StatusCode == HttpStatusCode.Forbidden ? roomId : null);

				return SendResult.Dropped(error);
			case HttpStatusCode.TooManyRequests:
				return SendResult.RetryAfter(ReadRetryAfter(response, root, Clock), error);
			case >= HttpStatusCode.InternalServerError:
				return SendResult.Transient(error);
			default:
				Log.Warning($"Matrix rejected a notification ({error}), it was dropped");

				return SendResult.Dropped(error);
		}
	}

	private void EnterDegraded(string error, string? roomId) {
		lock (Lock) {
			LastFailure = Clock.GetUtcNow();

			if (Degraded) {
				return;
			}

			Degraded = true;
			Log.Error($"Matrix rejected the credentials or permissions ({error}{(roomId != null ? $", room {roomId}" : "")}); notifications are dropped and delivery is retried every {DegradedRetryGap.TotalMinutes:0} minutes");
		}
	}

	internal static TimeSpan? ReadRetryAfter(HttpResponseMessage response, JsonElement root, Clock clock) {
		if ((root.ValueKind == JsonValueKind.Object) && root.TryGetMember("retry_after_ms", out JsonElement ms) && (ms.ValueKind == JsonValueKind.Number) && ms.TryGetDouble(out double milliseconds)) {
			return double.IsNaN(milliseconds) ? null : TimeSpan.FromMilliseconds(Math.Min(Math.Max(milliseconds, 0), MaxRetryAfter.TotalMilliseconds));
		}

		// Read by hand: the trimmed ASF has no HttpResponseHeaders.RetryAfter (issue #23). Delay-seconds or an HTTP date
		if (!response.Headers.TryGetValues("Retry-After", out IEnumerable<string>? values)) {
			return null;
		}

		foreach (string value in values) {
			if (int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int seconds)) {
				return Cap(TimeSpan.FromSeconds(seconds));
			}

			return DateTimeOffset.TryParseExact(value, "r", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset date) ? Cap(date - clock.GetUtcNow()) : null;
		}

		return null;
	}
}
