using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SteamKit2;

namespace ASFNotifier.Tests;

#pragma warning disable CA1812 // MSTest uses this class during runtime
[TestClass]
internal sealed class ConnectionWatcherTests {
	private readonly FakeHost Host = new();
	private readonly FakeGiveUpLog Tap = new();
	private readonly FakeTimeProvider Time = new();
	private bool ShuttingDown;

	[TestMethod]
	internal void AStopWithoutAnAsfErrorIsBotStopped() {
		using ConnectionWatcher watcher = Watcher();

		Notification? result = watcher.OnDisconnected("bot1", EResult.OK, false, null);

		Assert.IsNotNull(result);
		Assert.AreEqual(NotificationKind.BotStopped, result.Kind);
		Assert.AreEqual(Time.Now, result.OccurredAt);
	}

	[TestMethod]
	internal void ADisconnectWhileKeepRunningIsNoStop() {
		using ConnectionWatcher watcher = Watcher();

		Assert.IsNull(watcher.OnDisconnected("bot1", EResult.NoConnection, true, null));
		Assert.IsTrue(watcher.IsTracked("bot1"));
	}

	[TestMethod]
	internal void TheErrorIsComparedWithTheStopDisconnectNotWithTheStartOfTheOutage() {
		using ConnectionWatcher watcher = Watcher();

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
		using ConnectionWatcher watcher = Watcher();

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
		using ConnectionWatcher watcher = Watcher();
		Notification? result = watcher.OnDisconnected("bot1", EResult.OK, false, "TwoFactorAuthentication");

		Assert.IsNotNull(result);
		Assert.AreEqual(NotificationKind.BotStoppedByAsf, result.Kind);
		Assert.AreEqual("TwoFactorAuthentication", result.RequiredInput);
	}

	[TestMethod]
	internal void NothingWhileShuttingDown() {
		using ConnectionWatcher watcher = Watcher();
		Tap.Entries["bot1"] = (new TapEntry(Time.Now, "HandleLoginResult", EResult.InvalidPassword), null);
		ShuttingDown = true;

		Assert.IsNull(watcher.OnDisconnected("bot1", EResult.OK, false, null));
		Assert.IsNull(watcher.OnDisconnected("bot2", EResult.OK, false, "SteamGuard"));
	}

	// Pins the assumption behind "report once": Stop() disconnects at most once per connection, so the
	// first classified stop ends the outage and its evidence, and the next stop disconnect is a new run
	[TestMethod]
	internal void AClassifiedStopEndsTheOutageAndTheNextStopIsANewRun() {
		using ConnectionWatcher watcher = Watcher();

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
		using ConnectionWatcher watcher = Watcher();
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

	[TestMethod]
	internal void AShortBlipProducesNothing() {
		using ConnectionWatcher watcher = Watcher();

		watcher.OnDisconnected("bot1", EResult.NoConnection, true, null);
		Time.Advance(TimeSpan.FromMinutes(4));

		Assert.IsNull(watcher.OnLoggedOn("bot1"));
		Assert.AreEqual(0, Time.ArmedTimers);

		Time.Advance(TimeSpan.FromMinutes(10));

		Assert.AreEqual(0, Host.Emitted.Count);
	}

	[TestMethod]
	internal void AnOutagePastTheThresholdIsReportedOnceThenReconnected() {
		using ConnectionWatcher watcher = Watcher();
		DateTimeOffset start = Time.Now;

		watcher.OnDisconnected("bot1", EResult.NoConnection, true, null);
		Time.Advance(TimeSpan.FromMinutes(5));

		Assert.AreEqual(1, Host.Emitted.Count);
		Notification disconnected = Host.Emitted[0];
		Assert.AreEqual(NotificationKind.BotDisconnected, disconnected.Kind);
		Assert.AreEqual("bot1", disconnected.BotName);
		Assert.AreEqual("NoConnection", disconnected.Reason);
		Assert.AreEqual(TimeSpan.FromMinutes(5), disconnected.Duration);
		Assert.AreEqual(start + TimeSpan.FromMinutes(5), disconnected.OccurredAt);

		// ASF keeps retrying: every failed attempt disconnects again, none of them reports the outage again
		for (int i = 0; i < 5; i++) {
			Time.Advance(TimeSpan.FromMinutes(1));
			watcher.OnDisconnected("bot1", EResult.NoConnection, true, null);
		}

		Time.Advance(TimeSpan.FromMinutes(20));

		Assert.AreEqual(1, Host.Emitted.Count);
		Assert.AreEqual(1, Time.CreatedTimers);

		Notification? reconnected = watcher.OnLoggedOn("bot1");

		Assert.IsNotNull(reconnected);
		Assert.AreEqual(NotificationKind.BotReconnected, reconnected.Kind);
		Assert.AreEqual(Time.Now - start, reconnected.Duration);
		Assert.IsFalse(watcher.IsTracked("bot1"));

		// The next outage is a new one
		Assert.IsNull(watcher.OnLoggedOn("bot1"));
	}

	[TestMethod]
	internal void RepeatedDisconnectsKeepOneTimerAndTheFirstDisconnectTime() {
		using ConnectionWatcher watcher = Watcher();
		DateTimeOffset start = Time.Now;

		watcher.OnDisconnected("bot1", EResult.NoConnection, true, null);
		Time.Advance(TimeSpan.FromMinutes(2));
		watcher.OnDisconnected("bot1", EResult.ServiceUnavailable, true, null);
		Time.Advance(TimeSpan.FromMinutes(2));
		watcher.OnDisconnected("bot1", EResult.OK, true, null);

		Assert.AreEqual(1, Time.CreatedTimers);
		Time.Advance(TimeSpan.FromMinutes(1));

		Assert.AreEqual(1, Host.Emitted.Count);
		Assert.AreEqual(start + TimeSpan.FromMinutes(5), Host.Emitted[0].OccurredAt);
		Assert.AreEqual(TimeSpan.FromMinutes(5), Host.Emitted[0].Duration);

		// The last reason that is not OK
		Assert.AreEqual("ServiceUnavailable", Host.Emitted[0].Reason);
	}

	[TestMethod]
	internal void AnOutageWithoutAReasonIsReportedWithout() {
		using ConnectionWatcher watcher = Watcher();

		watcher.OnDisconnected("bot1", EResult.OK, true, null);
		Time.Advance(TimeSpan.FromMinutes(5));

		Assert.AreEqual(1, Host.Emitted.Count);
		Assert.IsNull(Host.Emitted[0].Reason);
	}

	[TestMethod]
	internal void TheThresholdComesFromTheSettings() {
		using ConnectionWatcher watcher = Watcher(TimeSpan.FromMinutes(1));

		watcher.OnDisconnected("bot1", EResult.NoConnection, true, null);
		Time.Advance(TimeSpan.FromMinutes(1));

		Assert.AreEqual(1, Host.Emitted.Count);
		Assert.AreEqual(TimeSpan.FromMinutes(1), Host.Emitted[0].Duration);
	}

	[TestMethod]
	internal void AStopFoundByTheTimerRunsTheClassifierWithTheLatestDisconnectTime() {
		using ConnectionWatcher watcher = Watcher();

		watcher.OnDisconnected("bot1", EResult.NoConnection, true, null);
		Time.Advance(TimeSpan.FromMinutes(1));
		DateTimeOffset latest = Time.Now;
		watcher.OnDisconnected("bot1", EResult.NoConnection, true, null);

		// ASF gave up right after the latest disconnect; its Stop() fired no hook because the client was not connected
		Time.Advance(TimeSpan.FromSeconds(30));
		Tap.Entries["bot1"] = (new TapEntry(Time.Now, "HandleLoginResult", EResult.InvalidPassword), null);
		Host.Status = new BotStatus(false, false, null);
		Time.Advance(TimeSpan.FromMinutes(4));

		Assert.AreEqual(1, Host.Emitted.Count);
		Notification stop = Host.Emitted[0];
		Assert.AreEqual(NotificationKind.BotStoppedByAsf, stop.Kind);
		Assert.AreEqual("InvalidPassword", stop.Reason);
		Assert.AreEqual(latest, stop.OccurredAt);
		Assert.IsFalse(watcher.IsTracked("bot1"));
		Assert.IsFalse(Tap.Entries.ContainsKey("bot1"));
		Assert.IsNull(watcher.OnLoggedOn("bot1"));
	}

	[TestMethod]
	internal void AStopWithoutEvidenceFoundByTheTimerIsBotStoppedOrNeedsInput() {
		using ConnectionWatcher watcher = Watcher();

		watcher.OnDisconnected("bot1", EResult.NoConnection, true, null);
		watcher.OnDisconnected("bot2", EResult.NoConnection, true, null);
		Host.Statuses["bot1"] = new BotStatus(false, false, null);
		Host.Statuses["bot2"] = new BotStatus(false, false, "SteamGuard");
		Time.Advance(TimeSpan.FromMinutes(5));

		Assert.AreEqual(2, Host.Emitted.Count);
		Assert.AreEqual(NotificationKind.BotStopped, Host.Emitted.Single(static n => n.BotName == "bot1").Kind);
		Assert.AreEqual("SteamGuard", Host.Emitted.Single(static n => n.BotName == "bot2").RequiredInput);
	}

	[TestMethod]
	internal void AStopFoundByTheTimerWhileShuttingDownSendsNothing() {
		using ConnectionWatcher watcher = Watcher();

		watcher.OnDisconnected("bot1", EResult.NoConnection, true, null);
		Host.Status = new BotStatus(false, false, null);
		ShuttingDown = true;
		Time.Advance(TimeSpan.FromMinutes(5));

		Assert.AreEqual(0, Host.Emitted.Count);
		Assert.IsFalse(watcher.IsTracked("bot1"));
	}

	[TestMethod]
	internal void ALoggedOnBotAtTimerTimeProducesNothing() {
		using ConnectionWatcher watcher = Watcher();

		watcher.OnDisconnected("bot1", EResult.NoConnection, true, null);
		Host.Status = new BotStatus(true, true, null);
		Time.Advance(TimeSpan.FromMinutes(5));

		Assert.AreEqual(0, Host.Emitted.Count);
		Assert.IsFalse(watcher.IsTracked("bot1"));
		Assert.IsNull(watcher.OnLoggedOn("bot1"));
	}

	[TestMethod]
	internal void ABotGoneAtTimerTimeProducesNothing() {
		using ConnectionWatcher watcher = Watcher();

		watcher.OnDisconnected("bot1", EResult.NoConnection, true, null);
		Host.Status = null;
		Time.Advance(TimeSpan.FromMinutes(5));

		Assert.AreEqual(0, Host.Emitted.Count);
		Assert.IsFalse(watcher.IsTracked("bot1"));
	}

	[TestMethod]
	internal void AStopDisconnectDuringTheOutageCancelsTheTimer() {
		using ConnectionWatcher watcher = Watcher();

		watcher.OnDisconnected("bot1", EResult.NoConnection, true, null);
		Time.Advance(TimeSpan.FromMinutes(1));

		Assert.AreEqual(NotificationKind.BotStopped, watcher.OnDisconnected("bot1", EResult.OK, false, null)?.Kind);
		Assert.AreEqual(0, Time.ArmedTimers);

		Time.Advance(TimeSpan.FromMinutes(10));

		Assert.AreEqual(0, Host.Emitted.Count);
	}

	[TestMethod]
	internal void DestroyCancelsTheTimer() {
		using ConnectionWatcher watcher = Watcher();

		watcher.OnDisconnected("bot1", EResult.NoConnection, true, null);
		watcher.OnDestroy("bot1");

		Assert.AreEqual(0, Time.ArmedTimers);

		Time.Advance(TimeSpan.FromMinutes(10));

		Assert.AreEqual(0, Host.Emitted.Count);
	}

	[TestMethod]
	internal void DestroyAfterTheReportSendsNoReconnected() {
		using ConnectionWatcher watcher = Watcher();

		watcher.OnDisconnected("bot1", EResult.NoConnection, true, null);
		Time.Advance(TimeSpan.FromMinutes(5));
		watcher.OnDestroy("bot1");

		Assert.IsNull(watcher.OnLoggedOn("bot1"));
	}

	[TestMethod]
	internal void DisposeCancelsEveryTimer() {
		using ConnectionWatcher watcher = Watcher();

		watcher.OnDisconnected("bot1", EResult.NoConnection, true, null);
		watcher.OnDisconnected("bot2", EResult.NoConnection, true, null);
		watcher.Dispose();

		Assert.AreEqual(0, Time.ArmedTimers);

		Time.Advance(TimeSpan.FromMinutes(10));

		Assert.AreEqual(0, Host.Emitted.Count);
	}

	[TestMethod]
	internal void ATimerErrorGoesToTheHostAndEndsNothingElse() {
		using ConnectionWatcher watcher = Watcher();
		InvalidOperationException error = new("boom");

		watcher.OnDisconnected("bot1", EResult.NoConnection, true, null);
		Host.StatusError = error;
		Time.Advance(TimeSpan.FromMinutes(5));

		Assert.AreSame(error, Host.Errors.Single());
		Assert.AreEqual(0, Host.Emitted.Count);
	}

	private ConnectionWatcher Watcher(TimeSpan? notifyAfter = null) => new(Tap, () => ShuttingDown, Time, notifyAfter ?? TimeSpan.FromMinutes(5), Host);

	private sealed class FakeGiveUpLog : IGiveUpLog {
		internal Dictionary<string, (TapEntry? GiveUpError, TapEntry? Reason)> Entries { get; } = [];

		public void Forget(string botName) => Entries.Remove(botName);
		public (TapEntry? GiveUpError, TapEntry? Reason) Get(string botName) => Entries.GetValueOrDefault(botName);
	}

	private sealed class FakeHost : IWatcherHost {
		internal List<Notification> Emitted { get; } = [];
		internal List<Exception> Errors { get; } = [];

		// Every bot not in Statuses: still running, not logged on
		internal BotStatus? Status { get; set; } = new BotStatus(true, false, null);

		internal Exception? StatusError { get; set; }
		internal Dictionary<string, BotStatus> Statuses { get; } = [];

		public void Emit(Notification notification) => Emitted.Add(notification);

		public BotStatus? GetStatus(string botName) {
			if (StatusError != null) {
				throw StatusError;
			}

			return Statuses.TryGetValue(botName, out BotStatus status) ? status : Status;
		}

		public void OnError(Exception exception) => Errors.Add(exception);
	}
}
#pragma warning restore CA1812 // MSTest uses this class during runtime
