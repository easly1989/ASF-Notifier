using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ASFNotifier;

internal enum SyncOutcome : byte {
	Ok,
	Unauthorized,
	RetryAfter,
	Transient,
	Rejected
}

// One m.room.message of the command room. Age: unsigned.age from the homeserver; OriginServerTs in milliseconds
internal sealed record RoomMessage(string EventId, string Sender, string MsgType, string Body, TimeSpan? Age = null, double? OriginServerTs = null);

internal sealed record SyncResult(SyncOutcome Outcome, string? Error = null, TimeSpan? Delay = null) {
	// Events of the room's timeline after the filter, of any type; zero means nothing new happened in the room
	internal int EventCount { get; init; }

	internal IReadOnlyList<RoomMessage> Messages { get; init; } = [];
	internal string? NextBatch { get; init; }
	internal string? UserId { get; init; }
}

// Spec section 9.1: reads the command room with /sync. Like MatrixChannel, it never puts the token anywhere but the
// Authorization header, and its errors are a status and an M_* code at most
internal sealed class MatrixSyncClient {
	internal const int MaxResponseBytes = 1024 * 1024;
	internal const int TimelineLimit = 20;
	internal static readonly TimeSpan RequestSlack = TimeSpan.FromSeconds(20);

	private readonly string AccessToken;
	private readonly Clock Clock;
	private readonly string HomeserverUrl;
	private readonly HttpClient HttpClient;

	internal MatrixSyncClient(HttpClient httpClient, string homeserverUrl, string accessToken, Clock clock) {
		HttpClient = httpClient;
		HomeserverUrl = homeserverUrl.TrimEnd('/');
		AccessToken = accessToken;
		Clock = clock;
	}

	// The long poll outlasts MatrixChannel's 10 seconds; each request has its own deadline. No logging handler, on purpose
	internal static HttpClient CreateHttpClient(HttpMessageHandler? handler = null) => handler == null ? new HttpClient { Timeout = TimeSpan.FromMinutes(2) } : new HttpClient(handler, false) { Timeout = TimeSpan.FromMinutes(2) };

	// Only the room, only m.room.message, never the bot's own events; nothing else the homeserver would send
	internal static string Filter(string roomId, string botUserId) {
		using MemoryStream stream = new();

		using (Utf8JsonWriter writer = new(stream)) {
			writer.WriteStartObject();
			WriteNothing(writer, "account_data");
			WriteNothing(writer, "presence");
			writer.WritePropertyName("room");
			writer.WriteStartObject();
			writer.WritePropertyName("rooms");
			writer.WriteStartArray();
			writer.WriteStringValue(roomId);
			writer.WriteEndArray();
			WriteNothing(writer, "account_data");
			WriteNothing(writer, "ephemeral");
			WriteNothing(writer, "state");
			writer.WritePropertyName("timeline");
			writer.WriteStartObject();
			writer.WritePropertyName("types");
			writer.WriteStartArray();
			writer.WriteStringValue("m.room.message");
			writer.WriteEndArray();
			writer.WritePropertyName("not_senders");
			writer.WriteStartArray();
			writer.WriteStringValue(botUserId);
			writer.WriteEndArray();
			writer.WritePropertyName("limit");
			writer.WriteNumberValue(TimelineLimit);
			writer.WriteEndObject();
			writer.WriteEndObject();
			writer.WriteEndObject();
		}

		return Encoding.UTF8.GetString(stream.ToArray());
	}

	// since null: an initial sync, whose events the caller skips (commands start from now)
	internal async Task<SyncResult> SyncAsync(string roomId, string botUserId, string? since, TimeSpan timeout, CancellationToken cancellationToken) {
		string query = $"/_matrix/client/v3/sync?filter={Uri.EscapeDataString(Filter(roomId, botUserId))}&timeout={((long) timeout.TotalMilliseconds).ToString(CultureInfo.InvariantCulture)}{(since == null ? "" : $"&since={Uri.EscapeDataString(since)}")}";
		(SyncResult result, JsonElement root) = await GetAsync(query, timeout, cancellationToken).ConfigureAwait(false);

		if (result.Outcome != SyncOutcome.Ok) {
			return result;
		}

		if (ReadString(root, "next_batch") is not { Length: > 0 } nextBatch) {
			return new SyncResult(SyncOutcome.Transient, "invalid sync response");
		}

		List<RoomMessage> messages = [];
		int count = 0;

		// Only this room's timeline counts, whatever else a homeserver that ignores the filter sends
		if (Member(Member(Member(Member(Member(root, "rooms"), "join"), roomId), "timeline"), "events") is { ValueKind: JsonValueKind.Array } events) {
			foreach (JsonElement item in events.EnumerateArray()) {
				count++;

				if ((ReadString(item, "type") != "m.room.message") || (ReadString(item, "event_id") is not { } eventId) || (ReadString(item, "sender") is not { } sender)) {
					continue;
				}

				JsonElement? content = Member(item, "content");

				// A redacted message has no msgtype or body left
				if ((ReadString(content, "msgtype") is not { } msgType) || (ReadString(content, "body") is not { } body)) {
					continue;
				}

				double? age = ReadNumber(Member(item, "unsigned"), "age");

				messages.Add(new RoomMessage(eventId, sender, msgType, body, age is >= 0 and <= 1e12 ? TimeSpan.FromMilliseconds(age.Value) : null, ReadNumber(item, "origin_server_ts")));
			}
		}

		return result with { EventCount = count, Messages = messages, NextBatch = nextBatch };
	}

	internal async Task<SyncResult> WhoAmIAsync(CancellationToken cancellationToken) {
		(SyncResult result, JsonElement root) = await GetAsync("/_matrix/client/v3/account/whoami", TimeSpan.Zero, cancellationToken).ConfigureAwait(false);

		if (result.Outcome != SyncOutcome.Ok) {
			return result;
		}

		return ReadString(root, "user_id") is { Length: > 1 } userId && userId.StartsWith('@') ? result with { UserId = userId } : new SyncResult(SyncOutcome.Transient, "invalid whoami response");
	}

	private static JsonElement? Member(JsonElement? element, string name) => element is { ValueKind: JsonValueKind.Object } value && value.TryGetMember(name, out JsonElement member) ? member : null;

	private static double? ReadNumber(JsonElement? element, string name) => Member(element, name) is { ValueKind: JsonValueKind.Number } value && value.TryGetDouble(out double number) && double.IsFinite(number) ? number : null;

	private static string? ReadString(JsonElement? element, string name) => Member(element, name) is { ValueKind: JsonValueKind.String } value ? value.GetString() : null;

	private static void WriteNothing(Utf8JsonWriter writer, string name) {
		writer.WritePropertyName(name);
		writer.WriteStartObject();
		writer.WritePropertyName("not_types");
		writer.WriteStartArray();
		writer.WriteStringValue("*");
		writer.WriteEndArray();
		writer.WriteEndObject();
	}

	private async Task<(SyncResult Result, JsonElement Root)> GetAsync(string pathAndQuery, TimeSpan pollTimeout, CancellationToken cancellationToken) {
		using HttpRequestMessage request = new(HttpMethod.Get, new Uri(HomeserverUrl + pathAndQuery, UriKind.Absolute));
		request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", AccessToken);

		using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		deadline.CancelAfter(pollTimeout + RequestSlack);

		try {
			// Capped by hand, as in MatrixChannel: the trimmed ASF has no MaxResponseContentBufferSize setter (issue #23)
			using HttpResponseMessage response = await HttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
			await response.Content.LoadIntoBufferAsync(MaxResponseBytes, deadline.Token).ConfigureAwait(false);

			string body = await response.Content.ReadAsStringAsync(deadline.Token).ConfigureAwait(false);

			return Interpret(response, body);
		} catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) {
			return (new SyncResult(SyncOutcome.Transient, "timeout"), default);
		} catch (HttpRequestException) {
			// The exception text can carry the URL; the short error is all anyone needs
			return (new SyncResult(SyncOutcome.Transient, "network error"), default);
		}
	}

	private (SyncResult Result, JsonElement Root) Interpret(HttpResponseMessage response, string body) {
		JsonElement root = default;

		try {
			using JsonDocument document = JsonDocument.Parse(body);
			root = document.RootElement.Clone();
		} catch (JsonException) {
			// Not JSON: an error keeps its status, a success is unusable
		}

		if (response.IsSuccessStatusCode) {
			return root.ValueKind == JsonValueKind.Object ? (new SyncResult(SyncOutcome.Ok), root) : (new SyncResult(SyncOutcome.Transient, "invalid response"), default);
		}

		int status = (int) response.StatusCode;
		string? errorCode = MatrixChannel.ReadErrorCode(root);
		string error = errorCode == null ? status.ToString(CultureInfo.InvariantCulture) : $"{status.ToString(CultureInfo.InvariantCulture)} {errorCode}";

		SyncResult result = response.StatusCode switch {
			HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => new SyncResult(SyncOutcome.Unauthorized, error),
			HttpStatusCode.TooManyRequests => new SyncResult(SyncOutcome.RetryAfter, error, MatrixChannel.ReadRetryAfter(response, root, Clock)),
			>= HttpStatusCode.InternalServerError => new SyncResult(SyncOutcome.Transient, error),
			_ => new SyncResult(SyncOutcome.Rejected, error)
		};

		return (result, default);
	}
}
