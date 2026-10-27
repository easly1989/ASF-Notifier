using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ASFNotifier;

internal sealed class MatrixChannel : INotificationChannel {
	internal static readonly TimeSpan DegradedRetryGap = TimeSpan.FromMinutes(10);
	internal static readonly TimeSpan MaxRetryAfter = TimeSpan.FromSeconds(60);
	internal static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);

	private readonly string AccessToken;
	private readonly HttpClient HttpClient;
	private readonly string HomeserverUrl;
	private readonly Lock Lock = new();
	private readonly INotifierLog Log;
	private readonly string RoomId;
	private readonly TimeProvider TimeProvider;

	private bool Degraded;
	private DateTimeOffset LastFailure;

	internal MatrixChannel(HttpClient httpClient, string homeserverUrl, string roomId, string accessToken, TimeProvider timeProvider, INotifierLog log) {
		HttpClient = httpClient;
		HomeserverUrl = homeserverUrl.TrimEnd('/');
		RoomId = roomId;
		AccessToken = accessToken;
		TimeProvider = timeProvider;
		Log = log;
	}

	// Built without any logging handler on purpose: it would see the bearer token
	internal static HttpClient CreateHttpClient(HttpMessageHandler? handler = null) => handler == null ? new HttpClient { Timeout = RequestTimeout } : new HttpClient(handler, false) { Timeout = RequestTimeout };

	public async Task<SendResult> SendAsync(FormattedMessage message, string transactionId, bool bypassDegraded, CancellationToken cancellationToken) {
		ArgumentNullException.ThrowIfNull(message);
		ArgumentException.ThrowIfNullOrEmpty(transactionId);

		if (!bypassDegraded) {
			lock (Lock) {
				if (Degraded && ((TimeProvider.GetUtcNow() - LastFailure) < DegradedRetryGap)) {
					return SendResult.Dropped("degraded");
				}
			}
		}

		Dictionary<string, string> body = new() {
			["msgtype"] = "m.notice",
			["body"] = message.PlainText,
			["format"] = "org.matrix.custom.html",
			["formatted_body"] = message.Html
		};

		using HttpRequestMessage request = new(HttpMethod.Put, new Uri($"{HomeserverUrl}/_matrix/client/v3/rooms/{Uri.EscapeDataString(RoomId)}/send/m.room.message/{Uri.EscapeDataString(transactionId)}", UriKind.Absolute)) {
			Content = JsonContent.Create(body)
		};

		request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", AccessToken);

		try {
			using HttpResponseMessage response = await HttpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);

			string responseBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

			return Interpret(response, responseBody);
		} catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) {
			return SendResult.Transient("timeout");
		} catch (HttpRequestException) {
			// The exception text can carry the URL; the short error is all anyone needs
			return SendResult.Transient("network error");
		}
	}

	// Only an M_* style code is echoed, so a server that reflects the token in its error body cannot leak it
	private static string? ReadErrorCode(JsonElement root) {
		if ((root.ValueKind != JsonValueKind.Object) || !root.TryGetProperty("errcode", out JsonElement code) || (code.ValueKind != JsonValueKind.String)) {
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

	private SendResult Interpret(HttpResponseMessage response, string responseBody) {
		int status = (int) response.StatusCode;

		if (response.IsSuccessStatusCode) {
			lock (Lock) {
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
			case HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden:
				EnterDegraded(error, response.StatusCode == HttpStatusCode.Forbidden);

				return SendResult.Dropped(error);
			case HttpStatusCode.TooManyRequests:
				return SendResult.RetryAfter(ReadRetryAfter(response, root), error);
			case >= HttpStatusCode.InternalServerError:
				return SendResult.Transient(error);
			default:
				Log.Warning($"Matrix rejected a notification ({error}), it was dropped");

				return SendResult.Dropped(error);
		}
	}

	private void EnterDegraded(string error, bool includeRoom) {
		lock (Lock) {
			LastFailure = TimeProvider.GetUtcNow();

			if (Degraded) {
				return;
			}

			Degraded = true;
			Log.Error($"Matrix rejected the credentials or permissions ({error}{(includeRoom ? $", room {RoomId}" : "")}); notifications are dropped and delivery is retried every {DegradedRetryGap.TotalMinutes:0} minutes");
		}
	}

	private TimeSpan? ReadRetryAfter(HttpResponseMessage response, JsonElement root) {
		if ((root.ValueKind == JsonValueKind.Object) && root.TryGetProperty("retry_after_ms", out JsonElement ms) && (ms.ValueKind == JsonValueKind.Number) && ms.TryGetDouble(out double milliseconds)) {
			return Cap(TimeSpan.FromMilliseconds(milliseconds));
		}

		RetryConditionHeaderValue? header = response.Headers.RetryAfter;

		if (header?.Delta is { } delta) {
			return Cap(delta);
		}

		return header?.Date is { } date ? Cap(date - TimeProvider.GetUtcNow()) : null;
	}
}
