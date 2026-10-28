using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using ArchiSteamFarm.Steam;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ASFNotifier.Tests;

#pragma warning disable CA1812 // MSTest uses this class during runtime
[TestClass]
internal sealed class NotifyTestTests {
	private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

	private static (NotifyTestCommand Command, FakeChannel Channel, FakeClock Time) Create(SendResult? result = null, string? configJson = null, TimeSpan? timeout = null) {
		FakeClock time = new();
		FakeChannel channel = new(_ => Task.FromResult(result ?? SendResult.Sent()));
		NotifierSettings settings = TestData.Settings(configJson ?? TestData.ConfigJson(), TestData.Token);

		return (new NotifyTestCommand(settings, settings.IsConfigured ? channel : null, time, timeout), channel, time);
	}

	[TestMethod]
	internal void AccessDecisionIsMasterOrHigher() {
		Assert.IsFalse(ASFNotifier.IsMasterOrHigher(EAccess.None));
		Assert.IsFalse(ASFNotifier.IsMasterOrHigher(EAccess.FamilySharing));
		Assert.IsFalse(ASFNotifier.IsMasterOrHigher(EAccess.Operator));
		Assert.IsTrue(ASFNotifier.IsMasterOrHigher(EAccess.Master));
		Assert.IsTrue(ASFNotifier.IsMasterOrHigher(EAccess.Owner));
	}

	[TestMethod]
	[DataRow("notifytest", true, null, null, NotifyTestAction.Send)]
	[DataRow("NotifyTest", true, null, null, NotifyTestAction.Send)]
	[DataRow("NOTIFYTEST", true, null, null, NotifyTestAction.Send)]
	[DataRow("status", true, null, null, NotifyTestAction.NotOurs)]
	[DataRow(null, true, null, null, NotifyTestAction.NotOurs)]
	[DataRow("", true, null, null, NotifyTestAction.NotOurs)]
	[DataRow("notifytest", false, null, null, NotifyTestAction.Denied)]
	[DataRow("status", false, null, null, NotifyTestAction.NotOurs)]
	[DataRow("notifytest", true, 3, null, NotifyTestAction.Cooldown)]
	[DataRow("notifytest", true, 10, null, NotifyTestAction.Send)]
	[DataRow("notifytest", true, 9, null, NotifyTestAction.Cooldown)]
	[DataRow("notifytest", true, 0, null, NotifyTestAction.Cooldown)]
	[DataRow("notifytest", false, 3, null, NotifyTestAction.Denied)]
	[DataRow("notifytest", true, null, "missing Matrix.RoomId", NotifyTestAction.NotConfigured)]
	[DataRow("notifytest", false, null, "missing Matrix.RoomId", NotifyTestAction.Denied)]
	[DataRow("notifytest", true, 3, "missing Matrix.RoomId", NotifyTestAction.Cooldown)]
	internal void DecisionTable(string? command, bool master, int? secondsSinceLast, string? notConfigured, NotifyTestAction expected) {
		DateTimeOffset? last = secondsSinceLast == null ? null : Now - TimeSpan.FromSeconds(secondsSinceLast.Value);

		NotifyTestDecision decision = NotifyTestDecision.Decide(command, master, last, Now, notConfigured);

		Assert.AreEqual(expected, decision.Action);
	}

	[TestMethod]
	internal void CooldownReportsRemainingWholeSeconds() {
		NotifyTestDecision decision = NotifyTestDecision.Decide("notifytest", true, Now - TimeSpan.FromSeconds(3), Now, null);

		Assert.AreEqual(7, decision.WaitSeconds);
		Assert.AreEqual(1, NotifyTestDecision.Decide("notifytest", true, Now - TimeSpan.FromMilliseconds(9500), Now, null).WaitSeconds);
	}

	[TestMethod]
	internal async Task SendsDirectlyWithBypassAndRepliesSent() {
		(NotifyTestCommand command, FakeChannel channel, _) = Create();

		string? reply = await command.HandleAsync("bot1", "notifytest", true, CancellationToken.None).ConfigureAwait(false);

		Assert.AreEqual("<bot1> Test notification sent to Matrix.", reply);
		(FormattedMessage message, bool bypass) = channel.Calls.Single();
		Assert.IsTrue(bypass);
		Assert.AreEqual("🧪 ASF-Notifier test from bot1 — delivery works", message.PlainText);
	}

	// Test never reaches the rate limiter: the command holds neither the limiter nor the queue, and sends with the cap full
	[TestMethod]
	internal async Task SendsWithTheRateLimiterFull() {
		Type[] held = typeof(NotifyTestCommand).GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).Select(static field => field.FieldType).ToArray();
		CollectionAssert.DoesNotContain(held, typeof(RateLimiter));
		CollectionAssert.DoesNotContain(held, typeof(SendQueue));

		(NotifyTestCommand command, FakeChannel channel, FakeClock time) = Create();
		RateLimiter limiter = new(time);

		for (int i = 0; i <= RateLimiter.HourlyCap; i++) {
			limiter.Pass(new Notification(NotificationKind.FarmingFinished, $"bot{i}", time.Now), static _ => true);
		}

		Assert.IsFalse(limiter.Pass(new Notification(NotificationKind.FarmingFinished, "full", time.Now), static _ => true));

		Assert.AreEqual("<bot1> Test notification sent to Matrix.", await command.HandleAsync("bot1", "notifytest", true, CancellationToken.None).ConfigureAwait(false));
		Assert.AreEqual(1, channel.Calls.Count);
	}

	[TestMethod]
	internal async Task LowAccessAndForeignCommandsReturnNullWithoutSending() {
		(NotifyTestCommand command, FakeChannel channel, _) = Create();

		Assert.IsNull(await command.HandleAsync("bot1", "notifytest", false, CancellationToken.None).ConfigureAwait(false));
		Assert.IsNull(await command.HandleAsync("bot1", "status", true, CancellationToken.None).ConfigureAwait(false));
		Assert.IsEmpty(channel.Calls);
	}

	[TestMethod]
	internal async Task CooldownIsPerBotAndExpires() {
		(NotifyTestCommand command, FakeChannel channel, FakeClock time) = Create();

		await command.HandleAsync("bot1", "notifytest", true, CancellationToken.None).ConfigureAwait(false);
		time.Advance(TimeSpan.FromSeconds(3));
		string? blocked = await command.HandleAsync("bot1", "NOTIFYTEST", true, CancellationToken.None).ConfigureAwait(false);
		string? other = await command.HandleAsync("bot2", "notifytest", true, CancellationToken.None).ConfigureAwait(false);

		Assert.AreEqual("<bot1> Please wait 7 s before the next test.", blocked);
		Assert.AreEqual("<bot2> Test notification sent to Matrix.", other);
		Assert.HasCount(2, channel.Calls);

		time.Advance(TimeSpan.FromSeconds(7));
		string? again = await command.HandleAsync("bot1", "notifytest", true, CancellationToken.None).ConfigureAwait(false);

		Assert.AreEqual("<bot1> Test notification sent to Matrix.", again);
	}

	[TestMethod]
	internal async Task DeniedCommandDoesNotStartTheCooldown() {
		(NotifyTestCommand command, _, _) = Create();

		await command.HandleAsync("bot1", "notifytest", false, CancellationToken.None).ConfigureAwait(false);
		string? reply = await command.HandleAsync("bot1", "notifytest", true, CancellationToken.None).ConfigureAwait(false);

		Assert.AreEqual("<bot1> Test notification sent to Matrix.", reply);
	}

	[TestMethod]
	internal async Task FailureRepliesWithShortError() {
		(NotifyTestCommand command, _, _) = Create(SendResult.Dropped("401 M_UNKNOWN_TOKEN"));

		string? reply = await command.HandleAsync("bot1", "notifytest", true, CancellationToken.None).ConfigureAwait(false);

		Assert.AreEqual("<bot1> Test notification failed: 401 M_UNKNOWN_TOKEN", reply);
	}

	[TestMethod]
	internal async Task NotConfiguredReplyNamesTheMissingKey() {
		(NotifyTestCommand command, FakeChannel channel, _) = Create(null, """{"ASFNotifier": {"Matrix": {"HomeserverUrl": "https://matrix.example.org"}}}""");

		string? reply = await command.HandleAsync("bot1", "notifytest", true, CancellationToken.None).ConfigureAwait(false);

		Assert.AreEqual("<bot1> ASF-Notifier is not configured: missing Matrix.RoomId.", reply);
		Assert.IsEmpty(channel.Calls);
	}

	[TestMethod]
	internal async Task HangingChannelRepliesTimeout() {
		FakeClock time = new();
		NotifierSettings settings = TestData.Settings(TestData.ConfigJson(), TestData.Token);
		FakeChannel hang = new(async ct => {
			await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);

			return SendResult.Sent();
		});
		NotifyTestCommand command = new(settings, hang, time, TimeSpan.FromMilliseconds(50));

		string? reply = await command.HandleAsync("bot1", "notifytest", true, CancellationToken.None).ConfigureAwait(false);

		Assert.AreEqual("<bot1> Test notification failed: timeout", reply);
	}

	[TestMethod]
	internal async Task TokenNeverAppearsInAnyReply() {
		using FakeHandler handler = new();
		using HttpClient client = MatrixChannel.CreateHttpClient(handler);
		FakeLog log = new();
		FakeClock time = new();
		MatrixChannel real = new(client, TestData.Homeserver, TestData.RoomId, TestData.Token, time, log);
		NotifierSettings settings = TestData.Settings(TestData.ConfigJson(), TestData.Token);
		NotifyTestCommand command = new(settings, real, time, null);
		string reflected = $$"""{"errcode": "{{TestData.Token}}", "error": "{{TestData.Token}}"}""";
		string?[] replies = new string?[5];
		HttpStatusCode[] statuses = [HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden, HttpStatusCode.InternalServerError, (HttpStatusCode) 429, HttpStatusCode.OK];

		for (int i = 0; i < statuses.Length; i++) {
			handler.Enqueue(statuses[i], reflected);
			time.Advance(TimeSpan.FromMinutes(1));
			replies[i] = await command.HandleAsync("bot1", "notifytest", true, CancellationToken.None).ConfigureAwait(false);
		}

		Assert.IsFalse(replies.Any(static reply => reply == null || reply.Contains(TestData.Token, StringComparison.Ordinal)));
		Assert.IsFalse(log.All.Any(static line => line.Contains(TestData.Token, StringComparison.Ordinal)));
	}

	[TestMethod]
	internal async Task ParallelCallsSendOnlyOnce() {
		(NotifyTestCommand command, FakeChannel channel, _) = Create();

		string?[] replies = await Task.WhenAll(Enumerable.Range(0, 50).Select(_ => Task.Run(() => command.HandleAsync("bot1", "notifytest", true, CancellationToken.None)))).ConfigureAwait(false);

		Assert.HasCount(1, channel.Calls);
		Assert.AreEqual(1, replies.Count(static reply => reply?.Contains("sent to Matrix", StringComparison.Ordinal) == true));
		Assert.AreEqual(49, replies.Count(static reply => reply?.Contains("Please wait", StringComparison.Ordinal) == true));
	}
}
#pragma warning restore CA1812 // MSTest uses this class during runtime
