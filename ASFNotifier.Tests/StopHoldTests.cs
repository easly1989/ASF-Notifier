using System;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ASFNotifier.Tests;

#pragma warning disable CA1812 // MSTest uses this class during runtime
[TestClass]
internal sealed class StopHoldTests {
	private static readonly DateTimeOffset T = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

	[TestMethod]
	internal void OneBotStopsAndIsSentOnceTheHoldEnds() {
		StopHold hold = new();
		hold.Add(Stop("bot1", T));

		Assert.AreEqual(T + StopHold.HoldTime, hold.NextDue);
		Assert.AreEqual(0, hold.Release(T + StopHold.HoldTime - TimeSpan.FromMilliseconds(1), false, 2).Count);

		// Other bots still run
		Assert.AreEqual("bot1", hold.Release(T + StopHold.HoldTime, false, 2).Single().BotName);
		Assert.IsNull(hold.NextDue);
	}

	// One bot is no mass stop, even though nothing runs any more
	[TestMethod]
	internal void TheOnlyBotStoppingIsSent() => Assert.AreEqual(1, Held(Stop("bot1", T)).Release(T + StopHold.HoldTime, false, 0).Count);

	[TestMethod]
	internal void AllBotsStoppingTogetherSendNothing() {
		// ASF's exit, restart and update stop every bot with Stop(true) before the process exits
		StopHold hold = Held(Stop("bot1", T), Stop("bot2", T.AddSeconds(1)), Stop("bot3", T.AddSeconds(2)));

		Assert.AreEqual(0, hold.Release(T + StopHold.HoldTime, false, 0).Count);
		Assert.IsNull(hold.NextDue);
	}

	[TestMethod]
	internal void TwoBotsStoppingWhileAnotherRunsAreSent() {
		StopHold hold = Held(Stop("bot1", T), Stop("bot2", T.AddSeconds(1)));

		Assert.AreEqual(1, hold.Release(T + StopHold.HoldTime, false, 1).Count);
		Assert.AreEqual(T.AddSeconds(1) + StopHold.HoldTime, hold.NextDue);
		Assert.AreEqual(1, hold.Release(T.AddSeconds(1) + StopHold.HoldTime, false, 1).Count);
	}

	[TestMethod]
	internal void TheProcessGoingDownDropsEverythingHeld() {
		StopHold hold = Held(Stop("bot1", T), new Notification(NotificationKind.BotStoppedByAsf, "bot2", T, RequiredInput: "SteamGuard"));

		Assert.AreEqual(0, hold.Release(T + StopHold.HoldTime, true, 5).Count);
		Assert.IsNull(hold.NextDue);
	}

	private static StopHold Held(params Notification[] notifications) {
		StopHold hold = new();

		foreach (Notification notification in notifications) {
			hold.Add(notification);
		}

		return hold;
	}

	private static Notification Stop(string bot, DateTimeOffset at) => new(NotificationKind.BotStopped, bot, at);
}
#pragma warning restore CA1812 // MSTest uses this class during runtime
