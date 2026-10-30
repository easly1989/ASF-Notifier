using System;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ASFNotifier.Tests;

#pragma warning disable CA1812 // MSTest uses this class during runtime
[TestClass]
internal sealed class MatrixSyncClientTests {
	private sealed class Rig : IDisposable {
		private readonly HttpClient Http;

		internal MatrixSyncClient Client { get; }
		internal FakeHandler Handler { get; } = new();

		internal Rig() {
			Http = MatrixSyncClient.CreateHttpClient(Handler);
			Client = new MatrixSyncClient(Http, TestData.Homeserver + "/", TestData.Token, new FakeClock());
		}

		public void Dispose() {
			Http.Dispose();
			Handler.Dispose();
		}
	}

	private static Task<SyncResult> Sync(Rig rig, string? since = "s1") => rig.Client.SyncAsync(TestData.RoomId, CommandParserTests.BotUserId, since, TimeSpan.FromSeconds(30), CancellationToken.None);

	[TestMethod]
	internal async Task WhoAmIRequestAndUserId() {
		using Rig rig = new();
		rig.Handler.Enqueue(HttpStatusCode.OK, """{"user_id": "@asf:example.org", "device_id": "D"}""");

		SyncResult result = await rig.Client.WhoAmIAsync(CancellationToken.None).ConfigureAwait(false);

		Assert.AreEqual(SyncOutcome.Ok, result.Outcome);
		Assert.AreEqual("@asf:example.org", result.UserId);
		RecordedRequest request = rig.Handler.Requests.Single();
		Assert.AreEqual(HttpMethod.Get, request.Method);
		Assert.AreEqual("https://matrix.example.org/_matrix/client/v3/account/whoami", request.Uri);
		Assert.AreEqual("Bearer " + TestData.Token, request.Authorization);
	}

	[TestMethod]
	[DataRow("""{"device_id": "D"}""")]
	[DataRow("""{"user_id": "asf"}""")]
	[DataRow("not json")]
	internal async Task AnUnusableWhoAmIIsTransient(string body) {
		using Rig rig = new();
		rig.Handler.Enqueue(HttpStatusCode.OK, body);

		SyncResult result = await rig.Client.WhoAmIAsync(CancellationToken.None).ConfigureAwait(false);

		Assert.AreEqual(SyncOutcome.Transient, result.Outcome);
		Assert.IsNull(result.UserId);
	}

	[TestMethod]
	internal async Task SyncRequestCarriesTheRoomFilterSinceAndTimeout() {
		using Rig rig = new();
		rig.Handler.Enqueue(HttpStatusCode.OK, """{"next_batch": "s2"}""");

		await Sync(rig, "s1/+&").ConfigureAwait(false);

		Uri uri = new(rig.Handler.Requests.Single().Uri);
		Assert.AreEqual("/_matrix/client/v3/sync", uri.AbsolutePath);
		StringAssert.Contains(uri.Query, "&timeout=30000", StringComparison.Ordinal);
		StringAssert.Contains(uri.Query, "&since=s1%2F%2B%26", StringComparison.Ordinal);
		Assert.IsFalse(uri.AbsoluteUri.Contains(TestData.Token, StringComparison.Ordinal));

		string filter = Uri.UnescapeDataString(uri.Query.Split('&').Single(static part => part.StartsWith("?filter=", StringComparison.Ordinal))["?filter=".Length..]);
		using JsonDocument document = JsonDocument.Parse(filter);
		JsonElement room = document.RootElement.GetProperty("room");
		Assert.AreEqual(TestData.RoomId, room.GetProperty("rooms").EnumerateArray().Single().GetString());
		JsonElement timeline = room.GetProperty("timeline");
		Assert.AreEqual("m.room.message", timeline.GetProperty("types").EnumerateArray().Single().GetString());
		Assert.AreEqual(CommandParserTests.BotUserId, timeline.GetProperty("not_senders").EnumerateArray().Single().GetString());
		Assert.AreEqual(MatrixSyncClient.TimelineLimit, timeline.GetProperty("limit").GetInt32());

		foreach (string quiet in new[] { "ephemeral", "state", "account_data" }) {
			Assert.AreEqual("*", room.GetProperty(quiet).GetProperty("not_types").EnumerateArray().Single().GetString(), quiet);
		}

		Assert.AreEqual("*", document.RootElement.GetProperty("presence").GetProperty("not_types").EnumerateArray().Single().GetString());
	}

	[TestMethod]
	internal async Task AnInitialSyncHasNoSince() {
		using Rig rig = new();
		rig.Handler.Enqueue(HttpStatusCode.OK, """{"next_batch": "s1"}""");

		await rig.Client.SyncAsync(TestData.RoomId, CommandParserTests.BotUserId, null, TimeSpan.Zero, CancellationToken.None).ConfigureAwait(false);

		string query = new Uri(rig.Handler.Requests.Single().Uri).Query;
		Assert.IsFalse(query.Contains("since=", StringComparison.Ordinal));
		StringAssert.Contains(query, "&timeout=0", StringComparison.Ordinal);
	}

	[TestMethod]
	internal async Task OnlyTheRoomsTimelineMessagesAreRead() {
		using Rig rig = new();
		string other = SyncJson.Room("!other:example.org", SyncJson.Message("!status", id: "$other"));
		string ours = SyncJson.Room(
			TestData.RoomId,
			SyncJson.Message("!status bot1", age: 1500),
			"""{"type": "m.room.member", "event_id": "$m", "sender": "@alice:example.org", "content": {"membership": "join"}}""",
			"""{"type": "m.room.message", "event_id": "$redacted", "sender": "@alice:example.org", "content": {}, "origin_server_ts": 1}""",
			"""{"type": "m.room.message", "sender": "@alice:example.org", "content": {"msgtype": "m.text", "body": "!no id"}}""",
			"""{"type": "m.room.message", "event_id": "$ts", "sender": "@bob:example.org", "content": {"msgtype": "m.text", "body": "!version"}, "origin_server_ts": 1767225600000}"""
		);

		rig.Handler.Enqueue(HttpStatusCode.OK, SyncJson.Rooms("s2", other, ours));

		SyncResult result = await Sync(rig).ConfigureAwait(false);

		Assert.AreEqual(SyncOutcome.Ok, result.Outcome);
		Assert.AreEqual("s2", result.NextBatch);
		Assert.AreEqual(5, result.EventCount);
		Assert.HasCount(2, result.Messages);

		RoomMessage first = result.Messages[0];
		Assert.AreEqual("$e1", first.EventId);
		Assert.AreEqual(SyncJson.Alice, first.Sender);
		Assert.AreEqual("m.text", first.MsgType);
		Assert.AreEqual("!status bot1", first.Body);
		Assert.AreEqual(TimeSpan.FromMilliseconds(1500), first.Age);

		RoomMessage second = result.Messages[1];
		Assert.AreEqual("$ts", second.EventId);
		Assert.IsNull(second.Age);
		Assert.AreEqual(1767225600000, second.OriginServerTs);
	}

	// An edit repeats an old message and other relations annotate one; only a plain message or a reply is new
	[TestMethod]
	internal async Task EditsAndOtherRelationsAreSkippedButRepliesAreKept() {
		using Rig rig = new();
		rig.Handler.Enqueue(HttpStatusCode.OK, SyncJson.Batch(
			"s2",
			SyncJson.Message("* !status", id: "$edit", extraContent: """, "m.new_content": {"msgtype": "m.text", "body": "!status"}, "m.relates_to": {"rel_type": "m.replace", "event_id": "$e0"}"""),
			SyncJson.Message("!status", id: "$thread", extraContent: """, "m.relates_to": {"rel_type": "m.thread", "event_id": "$root"}"""),
			SyncJson.Message("!status", id: "$odd", extraContent: """, "m.relates_to": {"rel_type": 5}"""),
			SyncJson.Message("!status", id: "$reply", extraContent: """, "m.relates_to": {"m.in_reply_to": {"event_id": "$notice"}}""")
		));

		SyncResult result = await Sync(rig).ConfigureAwait(false);

		Assert.AreEqual(4, result.EventCount);
		Assert.AreEqual("$reply", result.Messages.Single().EventId);
	}

	// A negative age is passed on, not replaced by origin_server_ts, so the listener can refuse a future-dated event
	[TestMethod]
	internal async Task AFutureAgeIsKeptAndTheHtmlBodyIsRead() {
		using Rig rig = new();
		rig.Handler.Enqueue(HttpStatusCode.OK, SyncJson.Batch(
			"s2",
			SyncJson.Message("ASF Bot: status", age: -120_000, id: "$future", originServerTs: 1, formattedBody: "<b>ASF Bot</b>: status"),
			SyncJson.Message("!status", age: 1_000_000_000_000_000, id: "$absurd", extraContent: """, "formatted_body": "<b>no format</b>" """)
		));

		SyncResult result = await Sync(rig).ConfigureAwait(false);

		Assert.AreEqual(TimeSpan.FromMinutes(-2), result.Messages[0].Age);
		Assert.AreEqual("<b>ASF Bot</b>: status", result.Messages[0].FormattedBody);
		Assert.AreEqual(TimeSpan.FromMilliseconds(1e12), result.Messages[1].Age);
		Assert.IsNull(result.Messages[1].FormattedBody);
	}

	[TestMethod]
	internal async Task AnEmptyBatchHasNoEvents() {
		using Rig rig = new();
		rig.Handler.Enqueue(HttpStatusCode.OK, """{"next_batch": "s2", "rooms": {"join": {}}}""");

		SyncResult result = await Sync(rig).ConfigureAwait(false);

		Assert.AreEqual(0, result.EventCount);
		Assert.IsEmpty(result.Messages);
	}

	[TestMethod]
	[DataRow("""{"rooms": {}}""")]
	[DataRow("""{"next_batch": 5}""")]
	[DataRow("""[]""")]
	[DataRow("not json")]
	internal async Task ASyncWithoutNextBatchIsTransient(string body) {
		using Rig rig = new();
		rig.Handler.Enqueue(HttpStatusCode.OK, body);

		Assert.AreEqual(SyncOutcome.Transient, (await Sync(rig).ConfigureAwait(false)).Outcome);
	}

	[TestMethod]
	[DataRow(HttpStatusCode.Unauthorized, """{"errcode": "M_UNKNOWN_TOKEN"}""", SyncOutcome.Unauthorized, "401 M_UNKNOWN_TOKEN")]
	[DataRow(HttpStatusCode.Forbidden, """{"errcode": "M_FORBIDDEN"}""", SyncOutcome.Unauthorized, "403 M_FORBIDDEN")]
	[DataRow((HttpStatusCode) 429, """{"errcode": "M_LIMIT_EXCEEDED"}""", SyncOutcome.RetryAfter, "429 M_LIMIT_EXCEEDED")]
	[DataRow(HttpStatusCode.BadGateway, "<html>", SyncOutcome.Transient, "502")]
	[DataRow(HttpStatusCode.BadRequest, """{"errcode": "M_UNKNOWN_POS"}""", SyncOutcome.Rejected, "400 M_UNKNOWN_POS")]
	internal async Task ErrorStatusesMapToOutcomes(HttpStatusCode status, string body, SyncOutcome expected, string error) {
		using Rig rig = new();
		rig.Handler.Enqueue(status, body);

		SyncResult result = await Sync(rig).ConfigureAwait(false);

		Assert.AreEqual(expected, result.Outcome);
		Assert.AreEqual(error, result.Error);
	}

	[TestMethod]
	internal async Task RateLimitCarriesTheServersDelay() {
		using Rig rig = new();
		rig.Handler.Enqueue((HttpStatusCode) 429, """{"errcode": "M_LIMIT_EXCEEDED", "retry_after_ms": 2500}""");

		SyncResult result = await Sync(rig).ConfigureAwait(false);

		Assert.AreEqual(TimeSpan.FromMilliseconds(2500), result.Delay);
	}

	[TestMethod]
	internal async Task NetworkErrorAndTimeoutAreTransientAndShort() {
		using Rig rig = new();
		rig.Handler.EnqueueThrow(new HttpRequestException($"refused {TestData.Homeserver}"));
		rig.Handler.EnqueueThrow(new TaskCanceledException("timed out", new TimeoutException()));

		SyncResult network = await Sync(rig).ConfigureAwait(false);
		SyncResult timeout = await Sync(rig).ConfigureAwait(false);

		Assert.AreEqual(SyncOutcome.Transient, network.Outcome);
		Assert.AreEqual("network error", network.Error);
		Assert.AreEqual(SyncOutcome.Transient, timeout.Outcome);
		Assert.AreEqual("timeout", timeout.Error);
	}

	[TestMethod]
	internal async Task CallerCancellationPropagates() {
		using Rig rig = new();
		rig.Handler.EnqueueThrow(new TaskCanceledException());
		using CancellationTokenSource cts = new();
		await cts.CancelAsync().ConfigureAwait(false);

		await Assert.ThrowsAsync<TaskCanceledException>(() => rig.Client.SyncAsync(TestData.RoomId, CommandParserTests.BotUserId, "s1", TimeSpan.FromSeconds(30), cts.Token)).ConfigureAwait(false);
	}

	[TestMethod]
	[DataRow(MatrixSyncClient.MaxResponseBytes, SyncOutcome.Ok)]
	[DataRow(MatrixSyncClient.MaxResponseBytes + 1, SyncOutcome.Transient)]
	internal async Task ResponseBodyIsCappedAtOneMiB(int length, SyncOutcome expected) {
		using Rig rig = new();
		const string head = "{\"next_batch\": \"s2\", \"pad\": \"";
		rig.Handler.Enqueue(HttpStatusCode.OK, head + new string('x', length - head.Length - 2) + "\"}");

		Assert.AreEqual(expected, (await Sync(rig).ConfigureAwait(false)).Outcome);
	}
}

// Room events as the homeserver sends them, for the sync and listener tests
internal static class SyncJson {
	internal const string Alice = "@alice:example.org";

	// A sync answer with the events in the notification room
	internal static string Batch(string next, params string[] events) => Rooms(next, Room(TestData.RoomId, events));

	// extraContent: more members of content, as JSON starting with a comma
	internal static string Message(string body, string sender = Alice, string msgType = "m.text", long? age = 1000, string id = "$e1", long? originServerTs = null, string? formattedBody = null, string extraContent = "") {
		string unsigned = age == null ? "" : ", \"unsigned\": {\"age\": " + age.Value.ToString(CultureInfo.InvariantCulture) + "}";
		string origin = originServerTs == null ? "" : ", \"origin_server_ts\": " + originServerTs.Value.ToString(CultureInfo.InvariantCulture);
		string html = formattedBody == null ? "" : ", \"format\": \"org.matrix.custom.html\", \"formatted_body\": " + JsonSerializer.Serialize(formattedBody);

		return "{\"type\": \"m.room.message\", \"event_id\": " + JsonSerializer.Serialize(id) + ", \"sender\": " + JsonSerializer.Serialize(sender) + ", \"content\": {\"msgtype\": " + JsonSerializer.Serialize(msgType) + ", \"body\": " + JsonSerializer.Serialize(body) + html + extraContent + "}" + unsigned + origin + "}";
	}

	// One joined room with its timeline, for Rooms
	internal static string Room(string roomId, params string[] events) => JsonSerializer.Serialize(roomId) + ": {\"timeline\": {\"events\": [" + string.Join(", ", events) + "]}}";

	internal static string Rooms(string next, params string[] rooms) => "{\"next_batch\": " + JsonSerializer.Serialize(next) + ", \"rooms\": {\"join\": {" + string.Join(", ", rooms) + "}}}";
}
#pragma warning restore CA1812 // MSTest uses this class during runtime
