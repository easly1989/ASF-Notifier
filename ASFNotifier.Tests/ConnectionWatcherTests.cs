using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SteamKit2;

namespace ASFNotifier.Tests;

#pragma warning disable CA1812 // MSTest uses this class during runtime
[TestClass]
internal sealed class ConnectionWatcherTests {
	private readonly Dictionary<string, (TapEntry? GiveUpError, TapEntry? Reason)> Tap = [];
	private readonly FakeTimeProvider Time = new();
	private bool ShuttingDown;

	[TestMethod]
	internal void AStopWithoutAnAsfErrorIsBotStopped() {
		ConnectionWatcher watcher = Watcher();

		Notification? result = watcher.OnDisconnected("bot1", EResult.OK, false, null);

		Assert.IsNotNull(result);
		Assert.AreEqual(NotificationKind.BotStopped, result.Kind);
		Assert.AreEqual(Time.Now, result.OccurredAt);
	}

	[TestMethod]
	internal void ADisconnectWhileKeepRunningIsNoStop() {
		ConnectionWatcher watcher = Watcher();

		Assert.IsNull(watcher.OnDisconnected("bot1", EResult.NoConnection, true, null));
		Assert.IsTrue(watcher.IsTracked("bot1"));
	}

	[TestMethod]
	internal void TheErrorIsComparedWithTheStopDisconnectNotWithTheStartOfTheOutage() {
		ConnectionWatcher watcher = Watcher();

		// The outage starts with failed attempts ASF retries, then ASF gives up minutes later
		Assert.IsNull(watcher.OnDisconnected("bot1", EResult.InvalidPassword, true, null));
		Time.Advance(TimeSpan.FromMinutes(3));
		Tap["bot1"] = (new TapEntry(Time.Now, "HandleLoginResult", EResult.InvalidPassword), null);
		Time.Advance(TimeSpan.FromSeconds(1));

		Notification? result = watcher.OnDisconnected("bot1", EResult.OK, false, null);

		Assert.IsNotNull(result);
		Assert.AreEqual(NotificationKind.BotStoppedByAsf, result.Kind);
		Assert.AreEqual("InvalidPassword", result.Reason);
		Assert.IsTrue(result.LoginFailed);
	}

	[TestMethod]
	internal void NeededInputIsReportedWithTheInput() {
		Notification? result = Watcher().OnDisconnected("bot1", EResult.OK, false, "TwoFactorAuthentication");

		Assert.IsNotNull(result);
		Assert.AreEqual(NotificationKind.BotStoppedByAsf, result.Kind);
		Assert.AreEqual("TwoFactorAuthentication", result.RequiredInput);
	}

	[TestMethod]
	internal void NothingWhileShuttingDown() {
		ConnectionWatcher watcher = Watcher();
		Tap["bot1"] = (new TapEntry(Time.Now, "HandleLoginResult", EResult.InvalidPassword), null);
		ShuttingDown = true;

		Assert.IsNull(watcher.OnDisconnected("bot1", EResult.OK, false, null));
		Assert.IsNull(watcher.OnDisconnected("bot2", EResult.OK, false, "SteamGuard"));
	}

	[TestMethod]
	internal void ABotStartedAgainIsReportedAgainWhenItStopsAgain() {
		ConnectionWatcher watcher = Watcher();

		Assert.IsNotNull(watcher.OnDisconnected("bot1", EResult.OK, false, "SteamGuard"));

		// Started again by hand, it needs the code again before it ever logs on
		Time.Advance(TimeSpan.FromMinutes(10));
		Notification? again = watcher.OnDisconnected("bot1", EResult.OK, false, "SteamGuard");

		Assert.IsNotNull(again);
		Assert.AreEqual(Time.Now, again.OccurredAt);
	}

	[TestMethod]
	internal void LogOnAndDestroyClearTheBotsState() {
		ConnectionWatcher watcher = Watcher();

		watcher.OnDisconnected("bot1", EResult.NoConnection, true, null);
		watcher.OnDisconnected("bot2", EResult.NoConnection, true, null);
		watcher.OnLoggedOn("bot1");
		watcher.OnDestroy("bot2");

		Assert.IsFalse(watcher.IsTracked("bot1"));
		Assert.IsFalse(watcher.IsTracked("bot2"));
	}

	private ConnectionWatcher Watcher() => new(bot => Tap.GetValueOrDefault(bot), () => ShuttingDown, Time);
}
#pragma warning restore CA1812 // MSTest uses this class during runtime
