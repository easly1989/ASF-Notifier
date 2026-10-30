using System;
using System.Collections.Generic;
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
internal sealed class CommandListenerTests {
	private const string Bob = "@bob:example.org";
	private const string Mallory = "@mallory:example.org";
	private const string WhoAmI = """{"user_id": "@asf:example.org"}""";

	private static readonly string[] SaveThenRun = ["save s3", "run status"];
	private static readonly string[] WithinTheSkew = ["status skew", "status originskew"];

	private sealed class FakeHost : ICommandHost {
		internal bool Accept { get; set; } = true;
		internal List<string> Events { get; } = [];
		internal List<Notification> Replies { get; } = [];
		// A task for the commands that should not finish at once; null: Run answers at once
		internal Func<string, Task<CommandRun>?> Pending { get; set; } = static _ => null;
		internal List<(CommandAccess Access, string Command)> Runs { get; } = [];
		internal Func<CommandAccess, string, CommandRun> Run { get; set; } = static (_, command) => new CommandRun(true, $"<bot1> done: {command}");
		internal string? Stored { get; set; }
		internal string? Prefix { get; set; } = "!";

		string? ICommandHost.CommandPrefix => Prefix;

		string? ICommandHost.LoadSyncToken() => Stored;

		bool ICommandHost.Reply(Notification reply) {
			Replies.Add(reply);

			return Accept;
		}

		Task<CommandRun> ICommandHost.RunAsync(CommandAccess access, string command) {
			Runs.Add((access, command));
			Events.Add("run " + command);

			return Pending(command) ?? Task.FromResult(Run(access, command));
		}

		void ICommandHost.SaveSyncToken(string token) {
			Stored = token;
			Events.Add("save " + token);
		}
	}

	private sealed class Rig : IDisposable {
		private readonly MatrixSyncClient Sync;

		internal FakeHandler Handler { get; } = new();
		internal FakeHost Host { get; } = new();
		internal HttpClient Http { get; }
		internal CommandListener Listener { get; }
		internal FakeLog Log { get; } = new();
		internal FakeClock Time { get; } = new();

		internal Rig(string users = $"{SyncJson.Alice}=Master,{Bob}=Operator", Func<TimeSpan, CancellationToken, Task>? delay = null) {
			Http = MatrixSyncClient.CreateHttpClient(Handler);
			Sync = new MatrixSyncClient(Http, TestData.Homeserver, TestData.Token, Time);
			Listener = new CommandListener(Sync, TestData.RoomId, CommandAllowList.Parse(users, new FakeLog()), TestData.Token, Host, Time, Log, delay);
		}

		public void Dispose() {
			Listener.Dispose();
			Sync.Dispose();
			Http.Dispose();
			Handler.Dispose();
		}

		internal Task<TimeSpan> Poll() => Listener.PollOnceAsync(CancellationToken.None);

		// whoami, then the initial sync; afterwards each Poll reads one batch
		internal async Task Started() {
			Handler.Enqueue(HttpStatusCode.OK, WhoAmI);
			Handler.Enqueue(HttpStatusCode.OK, SyncJson.Batch("s1"));
			Assert.AreEqual(TimeSpan.Zero, await Poll().ConfigureAwait(false));
		}

		internal async Task Receive(params string[] events) {
			Handler.Enqueue(HttpStatusCode.OK, SyncJson.Batch($"s{Handler.Requests.Count + 1}", events));
			Assert.AreEqual(TimeSpan.Zero, await Poll().ConfigureAwait(false));
		}
	}

	[TestMethod]
	internal async Task TheFirstSyncStartsFromNowAndSavesThePosition() {
		using Rig rig = new();
		rig.Handler.Enqueue(HttpStatusCode.OK, WhoAmI);
		rig.Handler.Enqueue(HttpStatusCode.OK, SyncJson.Batch("s1", SyncJson.Message("!status")));

		await rig.Poll().ConfigureAwait(false);

		Assert.AreEqual("@asf:example.org", rig.Listener.BotUserId);
		Assert.IsEmpty(rig.Host.Runs);
		Assert.AreEqual("s1", rig.Host.Stored);
		string query = new Uri(rig.Handler.Requests[1].Uri).Query;
		Assert.IsFalse(query.Contains("since=", StringComparison.Ordinal));
		StringAssert.Contains(query, "&timeout=0", StringComparison.Ordinal);
	}

	[TestMethod]
	internal async Task AnAllowedCommandRunsAndIsAnsweredAsAReply() {
		using Rig rig = new();
		await rig.Started().ConfigureAwait(false);

		await rig.Receive(SyncJson.Message("!status bot1", id: "$cmd")).ConfigureAwait(false);

		Assert.AreEqual((CommandAccess.Master, "status bot1"), rig.Host.Runs.Single());
		Notification reply = rig.Host.Replies.Single();
		Assert.AreEqual(NotificationKind.CommandReply, reply.Kind);
		Assert.IsNull(reply.BotName);
		Assert.AreEqual("<bot1> done: status bot1", reply.Text);
		Assert.AreEqual("$cmd", reply.InReplyTo);
		Assert.AreEqual(TestData.RoomId, reply.RoomId);

		// The next poll long-polls from the saved position
		string query = new Uri(rig.Handler.Requests[^1].Uri).Query;
		StringAssert.Contains(query, "&since=s1", StringComparison.Ordinal);
		StringAssert.Contains(query, "&timeout=30000", StringComparison.Ordinal);
	}

	[TestMethod]
	internal async Task TheAuditLineHasSenderVerbAndOutcomeButNeverTheArguments() {
		using Rig rig = new();
		await rig.Started().ConfigureAwait(false);

		await rig.Receive(SyncJson.Message("!redeem bot1 AAAAA-BBBBB-CCCCC")).ConfigureAwait(false);

		string line = rig.Log.Infos.Single(static line => line.StartsWith("Matrix command", StringComparison.Ordinal));
		Assert.AreEqual($"Matrix command from {SyncJson.Alice}: REDEEM → ran", line);
		Assert.IsFalse(rig.Log.All.Any(static line => line.Contains("AAAAA", StringComparison.Ordinal) || line.Contains("bot1", StringComparison.Ordinal)));
	}

	[TestMethod]
	internal async Task ThePositionIsSavedBeforeAnyCommandRuns() {
		using Rig rig = new();
		await rig.Started().ConfigureAwait(false);
		rig.Host.Events.Clear();

		await rig.Receive(SyncJson.Message("!status")).ConfigureAwait(false);

		CollectionAssert.AreEqual(SaveThenRun, rig.Host.Events);
	}

	[TestMethod]
	internal async Task ARestartResumesFromTheSavedPosition() {
		using Rig rig = new();
		rig.Host.Stored = "saved";
		rig.Handler.Enqueue(HttpStatusCode.OK, WhoAmI);
		rig.Handler.Enqueue(HttpStatusCode.OK, SyncJson.Batch("s2", SyncJson.Message("!status")));

		await rig.Poll().ConfigureAwait(false);

		string query = new Uri(rig.Handler.Requests[1].Uri).Query;
		StringAssert.Contains(query, "&since=saved", StringComparison.Ordinal);
		StringAssert.Contains(query, "&timeout=30000", StringComparison.Ordinal);
		Assert.HasCount(1, rig.Host.Runs);
		Assert.AreEqual("s2", rig.Host.Stored);
	}

	[TestMethod]
	internal async Task AnEmptyBatchIsNotSaved() {
		using Rig rig = new();
		await rig.Started().ConfigureAwait(false);
		rig.Host.Events.Clear();

		await rig.Receive().ConfigureAwait(false);
		await rig.Receive().ConfigureAwait(false);

		Assert.IsEmpty(rig.Host.Events);
		Assert.AreEqual("s1", rig.Host.Stored);

		// The listener itself moves on
		StringAssert.Contains(new Uri(rig.Handler.Requests[^1].Uri).Query, "&since=s3", StringComparison.Ordinal);
	}

	[TestMethod]
	internal async Task ARejectedPositionStartsAgainFromNow() {
		using Rig rig = new();
		rig.Host.Stored = "stale";
		rig.Handler.Enqueue(HttpStatusCode.OK, WhoAmI);
		rig.Handler.Enqueue(HttpStatusCode.BadRequest, """{"errcode": "M_UNKNOWN_POS"}""");
		rig.Handler.Enqueue(HttpStatusCode.OK, SyncJson.Batch("fresh", SyncJson.Message("!status")));

		Assert.AreEqual(TimeSpan.Zero, await rig.Poll().ConfigureAwait(false));
		Assert.AreEqual(TimeSpan.Zero, await rig.Poll().ConfigureAwait(false));

		Assert.HasCount(1, rig.Log.Warnings);
		StringAssert.Contains(rig.Log.Warnings[0], "400 M_UNKNOWN_POS", StringComparison.Ordinal);
		Assert.IsFalse(new Uri(rig.Handler.Requests[2].Uri).Query.Contains("since=", StringComparison.Ordinal));
		Assert.IsEmpty(rig.Host.Runs);
		Assert.AreEqual("fresh", rig.Host.Stored);
	}

	[TestMethod]
	internal async Task OwnMessagesNoticesAndOtherMessageTypesAreIgnored() {
		using Rig rig = new();
		await rig.Started().ConfigureAwait(false);

		await rig.Receive(
			SyncJson.Message("!status", "@asf:example.org"),
			SyncJson.Message("!status", msgType: "m.notice"),
			SyncJson.Message("!status", msgType: "m.emote"),
			SyncJson.Message("!status", msgType: "m.file"),
			"""{"type": "m.room.member", "event_id": "$m", "sender": "@alice:example.org", "content": {"membership": "join", "body": "!status", "msgtype": "m.text"}}"""
		).ConfigureAwait(false);

		Assert.IsEmpty(rig.Host.Runs);
		Assert.IsEmpty(rig.Host.Replies);
		Assert.IsEmpty(rig.Log.All);
	}

	[TestMethod]
	internal async Task MessagesFromOtherRoomsAreIgnored() {
		using Rig rig = new();
		await rig.Started().ConfigureAwait(false);
		rig.Handler.Enqueue(HttpStatusCode.OK, SyncJson.Rooms("s3", SyncJson.Room("!other:example.org", SyncJson.Message("!status"))));

		await rig.Poll().ConfigureAwait(false);

		Assert.IsEmpty(rig.Host.Runs);
		Assert.AreEqual("s1", rig.Host.Stored);
	}

	[TestMethod]
	internal async Task ChatThatIsNotACommandIsIgnoredSilently() {
		using Rig rig = new();
		await rig.Started().ConfigureAwait(false);

		await rig.Receive(SyncJson.Message("status bot1 looks fine"), SyncJson.Message("hi", Mallory, id: "$m")).ConfigureAwait(false);

		Assert.IsEmpty(rig.Host.Runs);
		Assert.IsEmpty(rig.Log.All);
	}

	[TestMethod]
	internal async Task ASenderNotInTheAllowListGetsNoAnswer() {
		using Rig rig = new();
		await rig.Started().ConfigureAwait(false);

		await rig.Receive(SyncJson.Message("!status bot1", Mallory), SyncJson.Message("!status", "@Alice:example.org")).ConfigureAwait(false);

		Assert.IsEmpty(rig.Host.Runs);
		Assert.IsEmpty(rig.Host.Replies);
		Assert.HasCount(2, rig.Log.Warnings);
		Assert.AreEqual($"Matrix command from {Mallory}: STATUS → refused, sender not in the allow-list (not logged again for 10 minutes)", rig.Log.Warnings[0]);
	}

	[TestMethod]
	internal async Task ARefusedSenderIsLoggedOncePerWindowForABoundedNumberOfSenders() {
		using Rig rig = new();
		await rig.Started().ConfigureAwait(false);

		await rig.Receive(SyncJson.Message("!status", Mallory, id: "$m1"), SyncJson.Message("!2fa", Mallory, id: "$m2")).ConfigureAwait(false);
		Assert.HasCount(1, rig.Log.Warnings);

		// A crowd of strangers: only the first ones are logged
		await rig.Receive(Enumerable.Range(0, 20).Select(static i => SyncJson.Message("!status", $"@stranger{i}:example.org", id: $"$s{i}")).ToArray()).ConfigureAwait(false);
		Assert.HasCount(CommandListener.RefusedSenderLimit, rig.Log.Warnings);
		Assert.IsEmpty(rig.Host.Runs);
		Assert.IsEmpty(rig.Host.Replies);

		// A window later Mallory is logged again
		rig.Time.Advance(CommandListener.RefusedLogWindow);
		await rig.Receive(SyncJson.Message("!status", Mallory, id: "$m3")).ConfigureAwait(false);
		Assert.HasCount(CommandListener.RefusedSenderLimit + 1, rig.Log.Warnings);
	}

	[TestMethod]
	internal async Task TheSendersAccessLevelIsPassedToAsf() {
		using Rig rig = new();
		await rig.Started().ConfigureAwait(false);

		await rig.Receive(SyncJson.Message("@asf pause bot1", Bob)).ConfigureAwait(false);

		Assert.AreEqual((CommandAccess.Operator, "pause bot1"), rig.Host.Runs.Single());
	}

	[TestMethod]
	internal async Task NoAnswerFromAsfSendsNoReply() {
		using Rig rig = new();
		rig.Host.Run = static (_, _) => new CommandRun(true, null);
		await rig.Started().ConfigureAwait(false);

		await rig.Receive(SyncJson.Message("!exit", Bob)).ConfigureAwait(false);

		Assert.HasCount(1, rig.Host.Runs);
		Assert.IsEmpty(rig.Host.Replies);
		StringAssert.EndsWith(rig.Log.Infos.Single(), "EXIT → ran, no answer", StringComparison.Ordinal);
	}

	[TestMethod]
	[DataRow(5 * 60 * 1000L, true, DisplayName = "Five minutes old runs")]
	[DataRow((5 * 60 * 1000L) + 1000, false, DisplayName = "Older is dropped")]
	internal async Task CommandsOlderThanFiveMinutesAreDropped(long ageMs, bool runs) {
		using Rig rig = new();
		await rig.Started().ConfigureAwait(false);

		await rig.Receive(SyncJson.Message("!status", age: ageMs)).ConfigureAwait(false);

		Assert.HasCount(runs ? 1 : 0, rig.Host.Runs);

		if (!runs) {
			Assert.IsEmpty(rig.Host.Replies);
			StringAssert.EndsWith(rig.Log.Infos.Single(), "STATUS → dropped, older than 5 minutes", StringComparison.Ordinal);
		}
	}

	[TestMethod]
	internal async Task WithoutAgeTheOriginTimestampDecidesAndNeitherMeansTooOld() {
		using Rig rig = new();
		await rig.Started().ConfigureAwait(false);
		long now = rig.Time.Now.ToUnixTimeMilliseconds();

		await rig.Receive(
			SyncJson.Message("!status fresh", age: null, id: "$fresh", originServerTs: now - 60_000),
			SyncJson.Message("!status old", age: null, id: "$old", originServerTs: now - (6 * 60_000)),
			SyncJson.Message("!status unknown", age: null, id: "$unknown"),
			SyncJson.Message("!status absurd", age: null, id: "$absurd", originServerTs: long.MinValue)
		).ConfigureAwait(false);

		Assert.AreEqual("status fresh", rig.Host.Runs.Single().Command);
	}

	// A remote server's clock, or a forged timestamp, must not keep a command fresh for as long as its date lies ahead
	[TestMethod]
	internal async Task CommandsDatedInTheFutureAreDropped() {
		using Rig rig = new();
		await rig.Started().ConfigureAwait(false);
		long now = rig.Time.Now.ToUnixTimeMilliseconds();

		await rig.Receive(
			SyncJson.Message("!status age", age: -120_000, id: "$age", originServerTs: now),
			SyncJson.Message("!status origin", age: null, id: "$origin", originServerTs: now + 120_000),
			SyncJson.Message("!status skew", age: -30_000, id: "$skew"),
			SyncJson.Message("!status originskew", age: null, id: "$originskew", originServerTs: now + 30_000)
		).ConfigureAwait(false);

		CollectionAssert.AreEqual(WithinTheSkew, rig.Host.Runs.Select(static run => run.Command).ToArray());
		Assert.AreEqual(2, rig.Log.Infos.Count(static line => line.EndsWith("STATUS → dropped, no valid age (unknown or dated in the future)", StringComparison.Ordinal)));
	}

	// Commands run one at a time: one that took long ages the next before it runs
	[TestMethod]
	internal async Task TheAgeIsCheckedAgainRightBeforeEachCommand() {
		using Rig rig = new();
		rig.Host.Run = (_, command) => {
			rig.Time.Advance(TimeSpan.FromMinutes(4));

			return new CommandRun(true, "done " + command);
		};

		await rig.Started().ConfigureAwait(false);

		await rig.Receive(SyncJson.Message("!status slow", id: "$slow", age: 0), SyncJson.Message("!status next", id: "$next", age: 90_000)).ConfigureAwait(false);

		Assert.AreEqual("status slow", rig.Host.Runs.Single().Command);
		StringAssert.EndsWith(rig.Log.Infos[^1], "STATUS → dropped, older than 5 minutes", StringComparison.Ordinal);
	}

	[TestMethod]
	internal async Task AHungCommandIsAnsweredAfterTheTimeoutAndTheNextOneRuns() {
		List<TimeSpan> waits = [];
		TaskCompletionSource<CommandRun> hung = new(TaskCreationOptions.RunContinuationsAsynchronously);

		// The first command's timeout fires at once; the next one's never does, so that command finishes first
		Task Delay(TimeSpan wait, CancellationToken token) {
			lock (waits) {
				waits.Add(wait);

				return waits.Count == 1 ? Task.CompletedTask : Task.Delay(Timeout.InfiniteTimeSpan, token);
			}
		}

		using Rig rig = new(delay: Delay);
		rig.Host.Pending = command => command == "status hung" ? hung.Task : null;
		await rig.Started().ConfigureAwait(false);

		await rig.Receive(SyncJson.Message("!status hung", id: "$hung"), SyncJson.Message("!status next", id: "$next")).ConfigureAwait(false);

		Assert.AreEqual(TimeSpan.FromMinutes(2), CommandListener.CommandTimeout);
		Assert.AreEqual(CommandListener.CommandTimeout, waits[0]);
		Assert.HasCount(2, rig.Host.Replies);
		Assert.AreEqual("Still running, the answer will not be posted.", rig.Host.Replies[0].Text);
		Assert.AreEqual("$hung", rig.Host.Replies[0].InReplyTo);
		Assert.AreEqual("<bot1> done: status next", rig.Host.Replies[1].Text);
		StringAssert.EndsWith(rig.Log.Warnings.Single(), "STATUS → still running after 2 minutes, answer not posted", StringComparison.Ordinal);

		// ASF's work was not cancelled; when it ends, its answer is not posted
		hung.SetException(new InvalidOperationException("late"));
		await Task.Delay(50).ConfigureAwait(false);
		Assert.HasCount(2, rig.Host.Replies);
		Assert.IsEmpty(rig.Log.Errors);
	}

	[TestMethod]
	internal async Task StoppingEndsTheBatchBetweenCommands() {
		// A timeout that never fires, so the first command finishes normally and the loop sees the stop
		using Rig rig = new(delay: static (_, _) => Task.Delay(Timeout.InfiniteTimeSpan, CancellationToken.None));
		using CancellationTokenSource stop = new();
		rig.Host.Run = (_, command) => {
			stop.Cancel();

			return new CommandRun(true, command);
		};

		await rig.Started().ConfigureAwait(false);
		rig.Handler.Enqueue(HttpStatusCode.OK, SyncJson.Batch("s3", SyncJson.Message("!status one", id: "$1"), SyncJson.Message("!status two", id: "$2")));

		await Assert.ThrowsAsync<OperationCanceledException>(() => rig.Listener.PollOnceAsync(stop.Token)).ConfigureAwait(false);

		Assert.AreEqual("status one", rig.Host.Runs.Single().Command);
		Assert.IsEmpty(rig.Log.Errors);
	}

	[TestMethod]
	internal async Task APillMentionRunsButAReplyThatOnlyMentionsTheBotDoesNot() {
		using Rig rig = new();
		await rig.Started().ConfigureAwait(false);

		await rig.Receive(
			SyncJson.Message("ASF Bot: status bot1", id: "$pill", formattedBody: "<a href=\"https://matrix.to/#/@asf:example.org\">ASF Bot</a>: status bot1", extraContent: """, "m.mentions": {"user_ids": ["@asf:example.org"]}"""),

			// Clients mention the replied-to sender in every reply: answering a notice is not a command
			SyncJson.Message("restart please", id: "$reply", extraContent: """, "m.mentions": {"user_ids": ["@asf:example.org"]}, "m.relates_to": {"m.in_reply_to": {"event_id": "$notice"}}""")
		).ConfigureAwait(false);

		Assert.AreEqual("status bot1", rig.Host.Runs.Single().Command);
		Assert.AreEqual("$pill", rig.Host.Replies.Single().InReplyTo);
	}

	[TestMethod]
	internal async Task DisposingTheListenerDisposesItsHttpClient() {
		using Rig rig = new();

		rig.Listener.Dispose();

		await Assert.ThrowsAsync<ObjectDisposedException>(() => rig.Http.GetAsync(new Uri(TestData.Homeserver))).ConfigureAwait(false);
	}

	// The adapter stores the sync position without ASF's reflection-based serializer
	[TestMethod]
	internal void TheSyncPositionIsStoredAsAJsonString() {
		JsonElement stored = ASFNotifier.JsonString("s72_1/+\"&\\");

		Assert.AreEqual(JsonValueKind.String, stored.ValueKind);
		Assert.AreEqual("s72_1/+\"&\\", stored.GetString());
	}

	[TestMethod]
	internal async Task EachSenderIsRateLimited() {
		using Rig rig = new();
		await rig.Started().ConfigureAwait(false);

		await rig.Receive(Enumerable.Range(0, CommandListener.SenderLimit + 1).Select(static i => SyncJson.Message($"!status {i}", id: $"$a{i}")).Append(SyncJson.Message("!status bob", Bob, id: "$b")).ToArray()).ConfigureAwait(false);

		Assert.HasCount(CommandListener.SenderLimit + 1, rig.Host.Runs);
		Assert.AreEqual("status bob", rig.Host.Runs[^1].Command);
		Assert.HasCount(1, rig.Log.Warnings);
		StringAssert.EndsWith(rig.Log.Warnings[0], "STATUS → dropped, sender rate limit", StringComparison.Ordinal);

		// A slot frees a full window after it was taken
		rig.Time.Advance(CommandListener.SenderWindow - TimeSpan.FromSeconds(1));
		await rig.Receive(SyncJson.Message("!status late", id: "$late", age: 0)).ConfigureAwait(false);
		Assert.AreNotEqual("status late", rig.Host.Runs[^1].Command);

		rig.Time.Advance(TimeSpan.FromSeconds(1));
		await rig.Receive(SyncJson.Message("!status later", id: "$later", age: 0)).ConfigureAwait(false);
		Assert.AreEqual("status later", rig.Host.Runs[^1].Command);
	}

	[TestMethod]
	[DataRow("!2fa bot1")]
	[DataRow("!  2FA bot1")]
	[DataRow("@asf 2faok bot1")]
	[DataRow("@ASF:example.org: !2fano bot1")]
	[DataRow("!input bot1 SteamGuard SECRETCODE")]
	[DataRow("!tran\u017Ffer bot1 SECRETCODE")]
	[DataRow("@asf encrypt AES SECRETCODE")]
	[DataRow("!PRIVACY bot1 1")]
	[DataRow("!transfer& bot1 730 6 trading bot2")]
	internal async Task DeniedCommandsAreRefusedWithoutRunningOrEchoingArguments(string body) {
		using Rig rig = new();
		await rig.Started().ConfigureAwait(false);

		await rig.Receive(SyncJson.Message(body, id: "$deny")).ConfigureAwait(false);

		Assert.IsEmpty(rig.Host.Runs);
		Notification reply = rig.Host.Replies.Single();
		Assert.AreEqual("This command cannot be run from Matrix.", reply.Text);
		Assert.AreEqual("$deny", reply.InReplyTo);
		StringAssert.EndsWith(rig.Log.Warnings.Single(), "→ refused, not allowed from Matrix", StringComparison.Ordinal);
		Assert.IsFalse(rig.Log.All.Any(static line => line.Contains("SECRETCODE", StringComparison.Ordinal) || line.Contains("bot1", StringComparison.Ordinal)));
	}

	[TestMethod]
	internal async Task AnAsfErrorIsReportedWithoutItsMessage() {
		using Rig rig = new();
		rig.Host.Run = static (_, _) => throw new InvalidOperationException("boom with details");
		await rig.Started().ConfigureAwait(false);

		await rig.Receive(SyncJson.Message("!status")).ConfigureAwait(false);

		Assert.AreEqual("The command failed, see the ASF log.", rig.Host.Replies.Single().Text);
		StringAssert.EndsWith(rig.Log.Warnings.Single(), "STATUS → failed (InvalidOperationException)", StringComparison.Ordinal);
		Assert.IsFalse(rig.Log.All.Any(static line => line.Contains("boom", StringComparison.Ordinal)));
	}

	[TestMethod]
	internal async Task WithoutABotTheSenderIsTold() {
		using Rig rig = new();
		rig.Host.Run = static (_, _) => new CommandRun(false, null);
		await rig.Started().ConfigureAwait(false);

		await rig.Receive(SyncJson.Message("!status")).ConfigureAwait(false);

		Assert.AreEqual("ASF has no bot to run the command on.", rig.Host.Replies.Single().Text);
	}

	[TestMethod]
	internal async Task ARefusedReplyIsAudited() {
		using Rig rig = new();
		rig.Host.Accept = false;
		await rig.Started().ConfigureAwait(false);

		await rig.Receive(SyncJson.Message("!status")).ConfigureAwait(false);

		StringAssert.EndsWith(rig.Log.Infos.Single(), "STATUS → ran, reply not sent", StringComparison.Ordinal);
	}

	[TestMethod]
	internal async Task LongAnswersAreCappedAtAWholeCharacter() {
		using Rig rig = new();
		string emoji = char.ConvertFromUtf32(0x1F7E2);
		// "<bot1> line\n" is 12 characters once '\r' is gone, so the emoji's first half would be the last one that fits
		rig.Host.Run = (_, _) => new CommandRun(true, "\r\n<bot1> line\r\n" + new string('x', CommandListener.MaxReplyLength - 13) + emoji + new string('y', 100));
		await rig.Started().ConfigureAwait(false);

		await rig.Receive(SyncJson.Message("!status")).ConfigureAwait(false);

		string text = rig.Host.Replies.Single().Text ?? "";
		StringAssert.StartsWith(text, "<bot1> line\nxxx", StringComparison.Ordinal);
		Assert.IsFalse(text.Contains('\r', StringComparison.Ordinal));
		StringAssert.EndsWith(text, "x\n… (truncated)", StringComparison.Ordinal);
		Assert.AreEqual(CommandListener.MaxReplyLength - 1 + "\n… (truncated)".Length, text.Length);
	}

	[TestMethod]
	internal async Task AShortAnswerIsKeptWhole() {
		using Rig rig = new();
		rig.Host.Run = static (_, _) => new CommandRun(true, "\n<bot1> one\n<bot2> two");
		await rig.Started().ConfigureAwait(false);

		await rig.Receive(SyncJson.Message("!status ASF")).ConfigureAwait(false);

		Assert.AreEqual("<bot1> one\n<bot2> two", rig.Host.Replies.Single().Text);
	}

	[TestMethod]
	internal async Task ACommandRoomOtherThanTheNotificationRoomIsAnsweredThere() {
		FakeHost host = new();
		using FakeHandler handler = new();
		using HttpClient client = MatrixSyncClient.CreateHttpClient(handler);
		using MatrixSyncClient sync = new(client, TestData.Homeserver, TestData.Token, new FakeClock());
		using CommandListener listener = new(sync, "!commands:example.org", CommandAllowList.Parse($"{SyncJson.Alice}=Master", new FakeLog()), TestData.Token, host, new FakeClock(), new FakeLog());
		handler.Enqueue(HttpStatusCode.OK, WhoAmI);
		handler.Enqueue(HttpStatusCode.OK, """{"next_batch": "s1"}""");
		handler.Enqueue(HttpStatusCode.OK, SyncJson.Rooms("s2", SyncJson.Room("!commands:example.org", SyncJson.Message("!status"))));

		await listener.PollOnceAsync(CancellationToken.None).ConfigureAwait(false);
		await listener.PollOnceAsync(CancellationToken.None).ConfigureAwait(false);

		StringAssert.Contains(Uri.UnescapeDataString(handler.Requests[1].Uri), "\"rooms\":[\"!commands:example.org\"]", StringComparison.Ordinal);
		Assert.AreEqual("!commands:example.org", host.Replies.Single().RoomId);
	}

	[TestMethod]
	internal async Task HomeserverErrorsBackOffAndLogOnlyOnStateChanges() {
		using Rig rig = new();
		rig.Handler.Enqueue(HttpStatusCode.InternalServerError);
		rig.Handler.Enqueue(HttpStatusCode.BadGateway);
		rig.Handler.Enqueue((HttpStatusCode) 429, """{"errcode": "M_LIMIT_EXCEEDED", "retry_after_ms": 2500}""");
		rig.Handler.Enqueue((HttpStatusCode) 429, """{"errcode": "M_LIMIT_EXCEEDED", "retry_after_ms": 0}""");
		rig.Handler.Enqueue(HttpStatusCode.OK, WhoAmI);
		rig.Handler.Enqueue(HttpStatusCode.OK, SyncJson.Batch("s1"));

		Assert.AreEqual(TimeSpan.FromSeconds(2), await rig.Poll().ConfigureAwait(false));
		Assert.AreEqual(TimeSpan.FromSeconds(4), await rig.Poll().ConfigureAwait(false));
		Assert.AreEqual(TimeSpan.FromMilliseconds(2500), await rig.Poll().ConfigureAwait(false));
		Assert.AreEqual(CommandListener.MinRetryAfter, await rig.Poll().ConfigureAwait(false));
		Assert.HasCount(1, rig.Log.Warnings);
		StringAssert.Contains(rig.Log.Warnings[0], "500", StringComparison.Ordinal);

		Assert.AreEqual(TimeSpan.Zero, await rig.Poll().ConfigureAwait(false));
		Assert.AreEqual("Reading the command room works again", rig.Log.Infos.Single());

		// The next failure starts the backoff over
		rig.Handler.Enqueue(HttpStatusCode.ServiceUnavailable);
		Assert.AreEqual(TimeSpan.FromSeconds(2), await rig.Poll().ConfigureAwait(false));
	}

	[TestMethod]
	internal async Task ARejectedTokenRetriesEveryTenMinutesWithOneError() {
		using Rig rig = new();
		rig.Handler.Enqueue(HttpStatusCode.Unauthorized, """{"errcode": "M_UNKNOWN_TOKEN"}""");
		rig.Handler.Enqueue(HttpStatusCode.Unauthorized, """{"errcode": "M_UNKNOWN_TOKEN"}""");

		Assert.AreEqual(CommandListener.UnauthorizedRetry, await rig.Poll().ConfigureAwait(false));
		Assert.AreEqual(CommandListener.UnauthorizedRetry, await rig.Poll().ConfigureAwait(false));

		StringAssert.Contains(rig.Log.Errors.Single(), "401 M_UNKNOWN_TOKEN", StringComparison.Ordinal);
		Assert.AreEqual(TimeSpan.FromMinutes(10), CommandListener.UnauthorizedRetry);
	}

	[TestMethod]
	internal async Task TheBackgroundLoopBacksOffAndStopsOnDispose() {
		List<TimeSpan> waits = [];
		using SemaphoreSlim waited = new(0);

		// The start-up delay passes at once; the first backoff then waits until the listener is disposed
		async Task Delay(TimeSpan wait, CancellationToken token) {
			lock (waits) {
				waits.Add(wait);
			}

			if (wait == CommandListener.StartDelay) {
				return;
			}

			waited.Release();
			await Task.Delay(Timeout.InfiniteTimeSpan, token).ConfigureAwait(false);
		}

		using Rig rig = new(delay: Delay);
		rig.Handler.Enqueue(HttpStatusCode.InternalServerError);

		rig.Listener.Start();
		Assert.IsTrue(await waited.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false));
		rig.Listener.Dispose();

		lock (waits) {
			CollectionAssert.AreEqual(new[] { CommandListener.StartDelay, TimeSpan.FromSeconds(2) }, waits);
		}
	}

	// Spec section 11: the token never appears in a log line, an error, a reply or a URL, whatever the homeserver or
	// ASF sends back
	[TestMethod]
	internal async Task TheAccessTokenNeverLeaks() {
		using Rig rig = new();
		string reflected = $$"""{"errcode": "{{TestData.Token}}", "error": "bad token {{TestData.Token}}", "retry_after_ms": 10}""";
		rig.Handler.Enqueue(HttpStatusCode.Unauthorized, reflected);
		rig.Handler.Enqueue(HttpStatusCode.InternalServerError, reflected);
		rig.Handler.Enqueue((HttpStatusCode) 429, reflected);
		rig.Handler.EnqueueThrow(new HttpRequestException($"failed with {TestData.Token}"));
		rig.Handler.EnqueueThrow(new TaskCanceledException(TestData.Token, new TimeoutException()));
		rig.Handler.Enqueue(HttpStatusCode.OK, WhoAmI);
		rig.Handler.Enqueue(HttpStatusCode.BadRequest, reflected);
		rig.Handler.Enqueue(HttpStatusCode.OK, SyncJson.Batch("s1"));

		// Eight responses, seven polls: the poll whose whoami succeeds also takes the rejected first sync
		for (int i = 0; i < 7; i++) {
			await rig.Poll().ConfigureAwait(false);
		}

		rig.Host.Run = static (_, command) => command.StartsWith("boom", StringComparison.Ordinal) ? throw new InvalidOperationException(TestData.Token) : new CommandRun(true, $"<bot1> {command} {TestData.Token}");

		await rig.Receive(
			SyncJson.Message($"!status {TestData.Token}", id: "$1"),
			SyncJson.Message($"!{TestData.Token}", id: "$2"),
			SyncJson.Message($"!2fa {TestData.Token}", id: "$3"),
			SyncJson.Message($"!boom {TestData.Token}", id: "$4"),
			SyncJson.Message($"!status {TestData.Token}", Mallory, id: "$5"),
			SyncJson.Message($"!status {TestData.Token}", age: 3_600_000, id: "$6")
		).ConfigureAwait(false);

		Assert.HasCount(4, rig.Host.Replies);
		Assert.IsNotEmpty(rig.Log.Errors);
		Assert.IsNotEmpty(rig.Log.Warnings);

		IEnumerable<string> outputs = rig.Log.All
			.Concat(rig.Host.Replies.Select(static reply => reply.Text ?? ""))
			.Concat(rig.Host.Replies.Select(static reply => Formatter.Format(reply)).SelectMany(static message => new[] { message.PlainText, message.Html }))
			.Concat(rig.Handler.Requests.Select(static request => request.Uri + request.Body));

		Assert.IsFalse(outputs.Any(static output => output.Contains(TestData.Token, StringComparison.OrdinalIgnoreCase)), string.Join("\n", outputs.Where(static output => output.Contains(TestData.Token, StringComparison.OrdinalIgnoreCase))));
	}
}
#pragma warning restore CA1812 // MSTest uses this class during runtime
