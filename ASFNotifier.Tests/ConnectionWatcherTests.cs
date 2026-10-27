using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SteamKit2;

namespace ASFNotifier.Tests;

#pragma warning disable CA1812 // MSTest uses this class during runtime
[TestClass]
internal sealed class ConnectionWatcherTests {
	private readonly FakeGiveUpLog Tap = new();
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
		Tap.Entries["bot1"] = (new TapEntry(Time.Now, "HandleLoginResult", EResult.InvalidPassword), null);
		Time.Advance(TimeSpan.FromSeconds(1));

		Notification? result = watcher.OnDisconnected("bot1", EResult.OK, false, null);

		Assert.IsNotNull(result);
		Assert.AreEqual(NotificationKind.BotStoppedByAsf, result.Kind);
		Assert.AreEqual("InvalidPassword", result.Reason);
		Assert.IsTrue(result.LoginFailed);
	}

	[TestMethod]
	internal void AGiveUpBeforeALogOnIsNotBlamedForALaterStop() {
		ConnectionWatcher watcher = Watcher();

		// 12:00:00 ASF gives up; 12:00:20 started by hand and logged on; 12:00:50 stopped by hand
		Tap.Entries["bot1"] = (new TapEntry(Time.Now, "HandleLoginResult", EResult.InvalidPassword), null);
		Time.Advance(TimeSpan.FromSeconds(20));
		watcher.OnLoggedOn("bot1");
		Time.Advance(TimeSpan.FromSeconds(30));

		Notification? result = watcher.OnDisconnected("bot1", EResult.OK, false, null);

		Assert.IsNotNull(result);
		Assert.AreEqual(NotificationKind.BotStopped, result.Kind);
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
		Tap.Entries["bot1"] = (new TapEntry(Time.Now, "HandleLoginResult", EResult.InvalidPassword), null);
		ShuttingDown = true;

		Assert.IsNull(watcher.OnDisconnected("bot1", EResult.OK, false, null));
		Assert.IsNull(watcher.OnDisconnected("bot2", EResult.OK, false, "SteamGuard"));
	}

	// Pins the assumption behind "report once": Stop() disconnects at most once per connection, so the
	// first classified stop ends the outage and its evidence, and the next stop disconnect is a new run
	[TestMethod]
	internal void AClassifiedStopEndsTheOutageAndTheNextStopIsANewRun() {
		ConnectionWatcher watcher = Watcher();

		Assert.IsNull(watcher.OnDisconnected("bot1", EResult.NoConnection, true, null));
		Tap.Entries["bot1"] = (new TapEntry(Time.Now, "RequestInput", null), null);
		Assert.AreEqual(NotificationKind.BotStoppedByAsf, watcher.OnDisconnected("bot1", EResult.OK, false, null)?.Kind);
		Assert.IsFalse(watcher.IsTracked("bot1"));
		Assert.IsFalse(Tap.Entries.ContainsKey("bot1"));

		// Started again by hand, it needs the code again before it ever logs on
		Time.Advance(TimeSpan.FromMinutes(10));
		Notification? again = watcher.OnDisconnected("bot1", EResult.OK, false, "SteamGuard");

		Assert.IsNotNull(again);
		Assert.AreEqual(Time.Now, again.OccurredAt);
	}

	[TestMethod]
	internal void LogOnAndDestroyClearTheBotsStateAndEvidence() {
		ConnectionWatcher watcher = Watcher();
		Tap.Entries["bot1"] = (null, new TapEntry(Time.Now, "OnLoggedOff", EResult.LoggedInElsewhere));
		Tap.Entries["bot2"] = (null, new TapEntry(Time.Now, "OnLoggedOff", EResult.LoggedInElsewhere));

		watcher.OnDisconnected("bot1", EResult.NoConnection, true, null);
		watcher.OnDisconnected("bot2", EResult.NoConnection, true, null);
		watcher.OnLoggedOn("bot1");
		watcher.OnDestroy("bot2");

		Assert.IsFalse(watcher.IsTracked("bot1"));
		Assert.IsFalse(watcher.IsTracked("bot2"));
		Assert.AreEqual(0, Tap.Entries.Count);
	}

	private ConnectionWatcher Watcher() => new(Tap, () => ShuttingDown, Time);

	private sealed class FakeGiveUpLog : IGiveUpLog {
		internal Dictionary<string, (TapEntry? GiveUpError, TapEntry? Reason)> Entries { get; } = [];

		public void Forget(string botName) => Entries.Remove(botName);
		public (TapEntry? GiveUpError, TapEntry? Reason) Get(string botName) => Entries.GetValueOrDefault(botName);
	}
}
#pragma warning restore CA1812 // MSTest uses this class during runtime
