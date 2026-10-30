using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ASFNotifier.Tests;

#pragma warning disable CA1812 // MSTest uses this class during runtime
[TestClass]
internal sealed class RateLimiterTests {
	private readonly FakeClock Clock = new();
	private readonly List<Notification> Queued = [];

	private readonly RateLimiter Limiter;

	private DateTimeOffset Start;

	// MSTest makes a new instance per test
	public RateLimiterTests() => Limiter = new RateLimiter(Clock);

	[TestMethod]
	internal void DedupeWindowEdges() {
		Assert.IsTrue(Pass(NotificationKind.FarmingFinished, "bot1"));

		Clock.Advance(RateLimiter.DedupeWindow - TimeSpan.FromSeconds(1));
		Assert.IsFalse(Pass(NotificationKind.FarmingFinished, "bot1"), "9:59 is dropped");

		Clock.Advance(TimeSpan.FromSeconds(1));
		Assert.IsTrue(Pass(NotificationKind.FarmingFinished, "bot1"), "10:00 is sent");
	}

	[TestMethod]
	internal void DifferentBotsOrKindsAreNotDeduped() {
		Assert.IsTrue(Pass(NotificationKind.FarmingFinished, "bot1"));
		Assert.IsTrue(Pass(NotificationKind.FarmingFinished, "bot2"));
		Assert.IsTrue(Pass(NotificationKind.FarmingStarted, "bot1"));
		Assert.IsTrue(Pass(NotificationKind.BotStopped, "bot1"));
		Assert.IsTrue(Pass(NotificationKind.BotStoppedByAsf, "bot1"));
		Assert.HasCount(5, Queued);
	}

	[TestMethod]
	internal void ReconnectedClosingASentDisconnectIsNeverDeduped() {
		Assert.IsTrue(Pass(NotificationKind.BotDisconnected, "bot1"));
		Clock.Advance(TimeSpan.FromMinutes(1));
		Assert.IsTrue(Pass(NotificationKind.BotReconnected, "bot1"));

		// A second outage reported 10.5 minutes after the first one
		Clock.Advance(TimeSpan.FromMinutes(9.5));
		Assert.IsTrue(Pass(NotificationKind.BotDisconnected, "bot1"));

		// Within 10 minutes of the first BotReconnected, but it closes the BotDisconnected just sent
		Clock.Advance(TimeSpan.FromMinutes(0.25));
		Assert.IsTrue(Pass(NotificationKind.BotReconnected, "bot1"));
	}

	[TestMethod]
	internal void ReconnectedAfterADroppedDisconnectIsDeduped() {
		Assert.IsTrue(Pass(NotificationKind.BotDisconnected, "bot1"));
		Clock.Advance(TimeSpan.FromMinutes(1));
		Assert.IsTrue(Pass(NotificationKind.BotReconnected, "bot1"));

		// Deduped, so it closes nothing that was sent
		Clock.Advance(TimeSpan.FromMinutes(4));
		Assert.IsFalse(Pass(NotificationKind.BotDisconnected, "bot1"));

		Clock.Advance(TimeSpan.FromMinutes(1));
		Assert.IsFalse(Pass(NotificationKind.BotReconnected, "bot1"));
	}

	[TestMethod]
	internal void TheTwentyFirstMessageInAnHourIsDropped() {
		FillTheHour();

		Clock.Advance(TimeSpan.FromMinutes(1));
		Assert.IsFalse(Pass(NotificationKind.FarmingFinished, "late"));

		// The first slot frees exactly one hour after it was taken
		Clock.Advance(Start + RateLimiter.CapWindow - TimeSpan.FromSeconds(1) - Clock.Now);
		Assert.IsFalse(Pass(NotificationKind.FarmingFinished, "late2"));

		Clock.Advance(TimeSpan.FromSeconds(1));
		Assert.IsTrue(Pass(NotificationKind.FarmingFinished, "late3"));
	}

	[TestMethod]
	internal void ABackwardsClockJumpMutesNothing() {
		Assert.IsTrue(Pass(NotificationKind.BotStoppedByAsf, "bot1"));
		FillTheHour(19);

		// The wall clock steps back an hour; the windows run on monotonic time
		Clock.Now -= TimeSpan.FromHours(1);
		Clock.Advance(RateLimiter.DedupeWindow);
		Assert.IsTrue(Pass(NotificationKind.BotStoppedByAsf, "bot1"));

		Clock.Advance(RateLimiter.CapWindow);
		Assert.IsTrue(Pass(NotificationKind.FarmingFinished, "after"));
	}

	[TestMethod]
	[DataRow(NotificationKind.BotStopped)]
	[DataRow(NotificationKind.BotStoppedByAsf)]
	internal void AStopClosesTheOutage(NotificationKind stop) {
		Assert.IsTrue(Pass(NotificationKind.BotDisconnected, "bot1"));
		Clock.Advance(TimeSpan.FromMinutes(1));
		Assert.IsTrue(Pass(NotificationKind.BotReconnected, "bot1"));

		Clock.Advance(TimeSpan.FromMinutes(9.5));
		Assert.IsTrue(Pass(NotificationKind.BotDisconnected, "bot1"));
		Clock.Advance(TimeSpan.FromSeconds(10));
		Assert.IsTrue(Pass(stop, "bot1"));

		// The stop ended the outage, so this one closes nothing that was sent
		Clock.Advance(TimeSpan.FromSeconds(5));
		Assert.IsFalse(Pass(NotificationKind.BotReconnected, "bot1"));
	}

	[TestMethod]
	internal void ARefusedSummaryKeepsTheCount() {
		Assert.IsTrue(Pass(NotificationKind.FarmingFinished, "bot1"));
		Assert.IsFalse(Pass(NotificationKind.FarmingFinished, "bot1"));
		Assert.IsFalse(Pass(NotificationKind.FarmingFinished, "bot1"));

		Assert.IsTrue(Limiter.Pass(new Notification(NotificationKind.FarmingFinished, "bot2", Clock.Now), notification => notification.Kind != NotificationKind.Suppressed));
		Assert.IsTrue(Pass(NotificationKind.FarmingFinished, "bot3"));

		Assert.AreEqual(2, Queued.Single(static notification => notification.Kind == NotificationKind.Suppressed).Count);
	}

	[TestMethod]
	internal void ARefusedMessageLeavesNoTrace() {
		Assert.IsFalse(Limiter.Pass(new Notification(NotificationKind.FarmingFinished, "bot1", Clock.Now), static _ => false));

		// Neither deduped nor counted as suppressed
		Assert.IsTrue(Pass(NotificationKind.FarmingFinished, "bot1"));
		Assert.AreEqual(NotificationKind.FarmingFinished, Queued.Single().Kind);
	}

	[TestMethod]
	internal void ARefusedDisconnectOpensNoOutage() {
		Assert.IsTrue(Pass(NotificationKind.BotDisconnected, "bot1"));
		Clock.Advance(TimeSpan.FromMinutes(1));
		Assert.IsTrue(Pass(NotificationKind.BotReconnected, "bot1"));

		Clock.Advance(TimeSpan.FromMinutes(9.5));
		Assert.IsFalse(Limiter.Pass(new Notification(NotificationKind.BotDisconnected, "bot1", Clock.Now), static _ => false));

		Clock.Advance(TimeSpan.FromSeconds(15));
		Assert.IsFalse(Pass(NotificationKind.BotReconnected, "bot1"));
	}

	[TestMethod]
	[DataRow(NotificationKind.BotStoppedByAsf)]
	[DataRow(NotificationKind.BotReconnected)]
	internal void ExemptKindsPassWithTheCapFull(NotificationKind kind) {
		FillTheHour();

		Assert.IsTrue(Pass(kind, "bot1"));
		Assert.IsFalse(Pass(NotificationKind.FarmingFinished, "bot2"));
	}

	[TestMethod]
	[DataRow(NotificationKind.BotStoppedByAsf)]
	[DataRow(NotificationKind.BotReconnected)]
	internal void ExemptKindsStillFollowDedupe(NotificationKind kind) {
		Assert.IsTrue(Pass(kind, "bot1"));
		Clock.Advance(TimeSpan.FromMinutes(5));
		Assert.IsFalse(Pass(kind, "bot1"));
	}

	[TestMethod]
	internal void ExemptKindsUseASlotWhenOneIsFree() {
		Assert.IsTrue(Pass(NotificationKind.BotStoppedByAsf, "bot1"));
		FillTheHour(19);

		Assert.IsFalse(Pass(NotificationKind.FarmingFinished, "bot2"));
	}

	[TestMethod]
	internal void SummaryIsEmittedOnceBeforeTheNextPassingMessageWithTheCount() {
		FillTheHour();
		Assert.IsFalse(Pass(NotificationKind.FarmingFinished, "a"));
		Assert.IsFalse(Pass(NotificationKind.FarmingFinished, "b"));
		Assert.IsFalse(Pass(NotificationKind.FarmingFinished, "c"));
		Queued.Clear();

		Clock.Advance(RateLimiter.CapWindow);
		Assert.IsTrue(Pass(NotificationKind.FarmingFinished, "next"));
		Assert.IsTrue(Pass(NotificationKind.FarmingFinished, "after"));

		Assert.HasCount(3, Queued);
		Assert.AreEqual(NotificationKind.Suppressed, Queued[0].Kind);
		Assert.AreEqual(3, Queued[0].Count);
		Assert.IsNull(Queued[0].BotName);
		Assert.AreEqual("next", Queued[1].BotName);
		Assert.AreEqual("after", Queued[2].BotName);
	}

	[TestMethod]
	internal void SummaryCountsDedupeDropsToo() {
		Assert.IsTrue(Pass(NotificationKind.FarmingFinished, "bot1"));
		Assert.IsFalse(Pass(NotificationKind.FarmingFinished, "bot1"));
		Assert.IsFalse(Pass(NotificationKind.FarmingFinished, "bot1"));

		Assert.IsTrue(Pass(NotificationKind.FarmingFinished, "bot2"));

		Assert.AreEqual(2, Queued.Single(static notification => notification.Kind == NotificationKind.Suppressed).Count);
	}

	[TestMethod]
	internal void SummaryRidesAlongAnExemptMessageWithTheCapFull() {
		FillTheHour();
		Assert.IsFalse(Pass(NotificationKind.FarmingFinished, "a"));
		Queued.Clear();

		Assert.IsTrue(Pass(NotificationKind.BotStoppedByAsf, "bot1"));

		CollectionAssert.AreEqual(new[] { NotificationKind.Suppressed, NotificationKind.BotStoppedByAsf }, Queued.Select(static notification => notification.Kind).ToArray());
	}

	[TestMethod]
	internal void SummaryUsesASlot() {
		Assert.IsTrue(Pass(NotificationKind.FarmingFinished, "x"));
		Assert.IsFalse(Pass(NotificationKind.FarmingFinished, "x"));
		Clock.Advance(RateLimiter.CapWindow);

		// The summary and this message take two slots, so 18 more fit
		Assert.IsTrue(Pass(NotificationKind.FarmingFinished, "first"));
		Assert.AreEqual(NotificationKind.Suppressed, Queued[^2].Kind);
		FillTheHour(18);

		Assert.IsFalse(Pass(NotificationKind.FarmingFinished, "over"));
	}

	[TestMethod]
	internal void NothingIsEmittedWhenNoMessageFollows() {
		FillTheHour();
		Queued.Clear();

		Assert.IsFalse(Pass(NotificationKind.FarmingFinished, "a"));
		Clock.Advance(TimeSpan.FromHours(5));

		Assert.IsEmpty(Queued);
	}

	[TestMethod]
	internal void CommandRepliesAreNeverDeduped() {
		Assert.IsTrue(Pass(NotificationKind.CommandReply, null));
		Assert.IsTrue(Pass(NotificationKind.CommandReply, null));
		Assert.IsTrue(Pass(NotificationKind.CommandReply, null));
		Assert.HasCount(3, Queued);
	}

	[TestMethod]
	internal void CommandRepliesTakeSlotsAndFollowTheCap() {
		FillTheHour(RateLimiter.HourlyCap - 1);
		Assert.IsTrue(Pass(NotificationKind.CommandReply, null));

		Assert.IsFalse(Pass(NotificationKind.CommandReply, null));
		Assert.IsFalse(Pass(NotificationKind.FarmingFinished, "bot2"));
	}

	[TestMethod]
	internal void CommandRepliesDoNotUseTheAsfErrorSubCap() {
		for (int i = 0; i < 10; i++) {
			Assert.IsTrue(Pass(NotificationKind.CommandReply, null), $"reply {i}");
		}

		for (int i = 0; i < RateLimiter.AsfErrorHourlyCap; i++) {
			Assert.IsTrue(PassError("bot1", $"Method{i}"), $"error {i}");
		}

		Assert.IsFalse(PassError("bot1", "Method9"), "the sub-cap counts errors only");
	}

	[TestMethod]
	internal void AsfErrorsAreDedupedByBotAndMethod() {
		Assert.IsTrue(PassError("bot1", "OnConnected"));
		Assert.IsFalse(PassError("bot1", "OnConnected"), "the same error again");
		Assert.IsTrue(PassError("bot1", "Start"), "another method");
		Assert.IsTrue(PassError("bot2", "OnConnected"), "another bot");
		Assert.IsTrue(PassError(null, "OnConnected"), "ASF itself");

		Clock.Advance(RateLimiter.DedupeWindow);
		Assert.IsTrue(PassError("bot1", "OnConnected"), "after the window");
	}

	[TestMethod]
	internal void ARecurringAsfErrorTakesOneSlotPerWindow() {
		for (int i = 0; i < 50; i++) {
			PassError("bot1", "OnConnected");
			Clock.Advance(TimeSpan.FromSeconds(10));
		}

		// 500 s of the same error: one message and one slot
		Assert.HasCount(1, Queued);

		// The next message carries the summary of the 49 repeats; the hour has room for the rest
		Assert.IsTrue(Pass(NotificationKind.FarmingFinished, "next"));
		Assert.AreEqual(49, Queued[1].Count);
		FillTheHour(RateLimiter.HourlyCap - 3);
		Assert.IsFalse(Pass(NotificationKind.FarmingFinished, "over"));
	}

	[TestMethod]
	internal void AnErrorStormKeepsToItsSubCapAndLeavesTheRestOfTheHour() {
		for (int i = 0; i < 10; i++) {
			Assert.AreEqual(i < RateLimiter.AsfErrorHourlyCap, PassError("bot1", $"Method{i}"), $"error {i}");
		}

		Assert.AreEqual(RateLimiter.AsfErrorHourlyCap, Queued.Count(static notification => notification.Kind == NotificationKind.AsfError));

		// The other events keep the rest of the hour: the summary of the 5 drops rides on the first one, 14 more fit
		Assert.IsTrue(Pass(NotificationKind.FarmingFinished, "first"));
		Assert.AreEqual(5, Queued[^2].Count);
		FillTheHour(RateLimiter.HourlyCap - RateLimiter.AsfErrorHourlyCap - 2);
		Assert.IsFalse(Pass(NotificationKind.FarmingFinished, "over"));

		// An error slot frees an hour after it was taken
		Clock.Advance(Start + RateLimiter.CapWindow - Clock.Now);
		Assert.IsTrue(PassError("bot1", "Method9"));
	}

	[TestMethod]
	internal void ErrorsCannotFillTheCapLeftByOtherEvents() {
		FillTheHour(RateLimiter.HourlyCap - 2);

		Assert.IsTrue(PassError("bot1", "Method0"));
		Assert.IsTrue(PassError("bot1", "Method1"));
		Assert.IsFalse(PassError("bot1", "Method2"), "the overall cap still applies");
	}

	[TestMethod]
	internal void AQueueThatRefusesIsReported() => Assert.IsFalse(Limiter.Pass(new Notification(NotificationKind.FarmingFinished, "bot1", Clock.Now), static _ => false));

	// Takes count slots with distinct bots, one minute apart, starting now
	private void FillTheHour(int count = RateLimiter.HourlyCap) {
		Start = Clock.Now;

		for (int i = 0; i < count; i++) {
			Assert.IsTrue(Pass(NotificationKind.FarmingFinished, $"fill{Start.Ticks}-{i}"), $"fill {i}");
			Clock.Advance(TimeSpan.FromSeconds(1));
		}
	}

	private bool PassError(string? bot, string method) =>
		Limiter.Pass(new Notification(NotificationKind.AsfError, bot, Clock.Now, Method: method, Text: "failed"), notification => {
			Queued.Add(notification);

			return true;
		});

	private bool Pass(NotificationKind kind, string? bot) =>
		Limiter.Pass(new Notification(kind, bot, Clock.Now), notification => {
			Queued.Add(notification);

			return true;
		});
}
#pragma warning restore CA1812 // MSTest uses this class during runtime
