using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NLog;
using NLog.Config;
using NLog.Targets;
using SteamKit2;

namespace ASFNotifier.Tests;

// A real NLog, in a private LogFactory, written to the way ASF's ArchiLogger writes: "{method}() {message}" under the bot's name
#pragma warning disable CA1812 // MSTest uses this class during runtime
[TestClass]
internal sealed class GiveUpLogTapTests {
	[TestMethod]
	internal void KeepsTheLatestGiveUpErrorPerBot() {
		using LogFactory factory = Factory();
		FakeClock time = new();
		Logger bot1 = factory.GetLogger("bot1");

		using GiveUpLogTap tap = new(factory, time);

		bot1.Error("RequestInput() input is invalid!");
		time.Advance(TimeSpan.FromSeconds(5));
		bot1.Error("HandleLoginResult() Received InvalidPassword error code 3 times in a row. Your password for this account is most likely wrong, aborting!");
		factory.GetLogger("bot2").Error("OnConnected() SteamLogin is invalid!");

		(TapEntry? error, TapEntry? reason) = tap.Get("bot1");

		Assert.AreEqual(new TapEntry(time.Now, "HandleLoginResult", EResult.InvalidPassword), error);
		Assert.AreEqual(new TapEntry(time.Now, "HandleLoginResult", EResult.InvalidPassword), reason);
		Assert.AreEqual(new TapEntry(time.Now, "OnConnected", null), tap.Get("bot2").GiveUpError);
		Assert.AreEqual((null, null), tap.Get("bot3"));
	}

	[TestMethod]
	internal void UnrelatedEntriesDoNotCount() {
		using LogFactory factory = Factory();
		Logger bot1 = factory.GetLogger("bot1");

		using GiveUpLogTap tap = new(factory, new FakeClock());

		// Errors from other methods, warnings and infos from give-up methods, an exception line, the ASF logger
		bot1.Error("OnFarmingFinished() Unable to login to Steam: InvalidPassword/Invalid");
		bot1.Error("Steam() InvalidPassword");
		bot1.Warn("OnConnected() SteamLogin is invalid!");
		bot1.Info("RequestInput() input is invalid!");
		bot1.Error(new InvalidOperationException(), "HandleLoginResult()");
		bot1.Error("no method prefix InvalidPassword");
		bot1.Debug("HandleLoginResult() Unable to login to Steam: InvalidPassword/Invalid");
		factory.GetLogger("ASF").Info("OnLoggedOn() Logged off of Steam: LogonSessionReplaced");

		Assert.AreEqual((null, null), tap.Get("bot1"));
		Assert.IsNull(tap.Get("ASF").GiveUpError);
	}

	[TestMethod]
	internal void ReasonEntryKeepsTheLatestOneThatNamesAResult() {
		using LogFactory factory = Factory();
		FakeClock time = new();
		Logger bot1 = factory.GetLogger("bot1");

		using GiveUpLogTap tap = new(factory, time);

		bot1.Warn("HandleLoginResult() Unable to login to Steam: InvalidPassword/Invalid");
		DateTimeOffset warned = time.Now;
		time.Advance(TimeSpan.FromSeconds(20));

		// An entry without a result does not wipe the reason
		bot1.Error("HandleLoginResult() Received some error code 3 times in a row, aborting!");

		(TapEntry? error, TapEntry? reason) = tap.Get("bot1");

		Assert.AreEqual(new TapEntry(time.Now, "HandleLoginResult", null), error);
		Assert.AreEqual(new TapEntry(warned, "HandleLoginResult", EResult.InvalidPassword), reason);

		bot1.Info("OnLoggedOff() Logged off of Steam: LogonSessionReplaced");
		Assert.AreEqual(new TapEntry(time.Now, "OnLoggedOff", EResult.LogonSessionReplaced), tap.Get("bot1").Reason);

		// Only HandleLoginResult and OnLoggedOff give a reason at any level
		bot1.Info("OnConnected() Logged off of Steam: AccountDisabled");
		Assert.AreEqual(EResult.LogonSessionReplaced, tap.Get("bot1").Reason?.Reason);
	}

	[TestMethod]
	internal void SurvivesAConfigurationReload() {
		using LogFactory factory = Factory();
		FakeClock time = new();
		Logger bot1 = factory.GetLogger("bot1");
		List<Action> deferred = [];

		using GiveUpLogTap tap = new(factory, time, deferred.Add);

		bot1.Error("OnConnected() SteamLogin is invalid!");
		Assert.IsNotNull(tap.Get("bot1").GiveUpError);

		// ASF or the user replaces the configuration; a logger created before keeps working, so does a new one.
		// NLog raises the event under its configuration lock, so the handler only defers the work.
		factory.Configuration = new LoggingConfiguration();
		Assert.AreEqual(0, factory.Configuration.LoggingRules.Count);
		RunAll(deferred);
		time.Advance(TimeSpan.FromMinutes(1));
		bot1.Error("HandleLoginResult() Unable to login to Steam: AccountDisabled/Invalid");
		factory.GetLogger("bot2").Error("OnLoggedOn() steamParentalCode is invalid!");

		Assert.AreEqual(new TapEntry(time.Now, "HandleLoginResult", EResult.AccountDisabled), tap.Get("bot1").GiveUpError);
		Assert.AreEqual(new TapEntry(time.Now, "OnLoggedOn", null), tap.Get("bot2").GiveUpError);
		Assert.AreEqual(1, factory.Configuration.LoggingRules.Count(static rule => rule.RuleName == GiveUpLogTap.RuleName));

		// Two more reloads before the deferred work runs: only the active configuration gets the rule
		LoggingConfiguration replaced = new();
		factory.Configuration = replaced;
		factory.Configuration = new LoggingConfiguration();
		RunAll(deferred);
		time.Advance(TimeSpan.FromMinutes(1));
		bot1.Error("RequestInput() input is invalid!");

		Assert.AreEqual(new TapEntry(time.Now, "RequestInput", null), tap.Get("bot1").GiveUpError);
		Assert.AreEqual(0, replaced.LoggingRules.Count);
		Assert.AreEqual(1, factory.Configuration.LoggingRules.Count);
	}

	[TestMethod]
	internal void RuleGoesFirstSoAFinalRuleCannotHideEntries() {
		using NullTarget sink = new("sink");
		LoggingConfiguration configuration = new();
		configuration.LoggingRules.Add(new LoggingRule("*", LogLevel.Trace, sink) { Final = true });

		using LogFactory factory = new() { Configuration = configuration };

		using GiveUpLogTap tap = new(factory, new FakeClock());

		factory.GetLogger("bot1").Error("OnConnected() SteamLogin is invalid!");

		Assert.IsNotNull(tap.Get("bot1").GiveUpError);
	}

	[TestMethod]
	internal void ForgetAndDisposeLeaveNothingBehind() {
		using LogFactory factory = Factory();
		Logger bot1 = factory.GetLogger("bot1");
		List<Action> deferred = [];
		GiveUpLogTap tap = new(factory, new FakeClock(), deferred.Add);

		bot1.Error("OnConnected() SteamLogin is invalid!");
		tap.Forget("bot1");
		Assert.AreEqual((null, null), tap.Get("bot1"));

		// A reload whose deferred registration only runs after Dispose
		factory.Configuration = new LoggingConfiguration();
		tap.Dispose();
		RunAll(deferred);
		bot1.Error("OnConnected() SteamLogin is invalid!");
		factory.Configuration = new LoggingConfiguration();
		RunAll(deferred);
		bot1.Error("OnConnected() SteamLogin is invalid!");

		Assert.AreEqual((null, null), tap.Get("bot1"));
		Assert.IsFalse(factory.Configuration.LoggingRules.Any(static rule => rule.RuleName == GiveUpLogTap.RuleName));
	}

	[TestMethod]
	internal void ErrorAndFatalEntriesAreHandedOnOffTheLoggingThread() {
		using LogFactory factory = Factory();
		FakeClock time = new();
		List<Action> deferred = [];
		List<LoggedError> errors = [];

		using GiveUpLogTap tap = new(factory, time, deferred.Add, errors.Add);

		factory.GetLogger("bot1").Error("OnConnected() SteamLogin is invalid!");
		factory.GetLogger("ASF").Fatal("Init() broken");
		factory.GetLogger("ASF").Error(new InvalidOperationException("detail"), "OnBotDisconnected()");
		factory.GetLogger("bot1").Warn("OnConnected() only a warning");
		factory.GetLogger("bot1").Info("OnConnected() only an info");

		// Nothing runs inside NLog's Write
		Assert.IsEmpty(errors);
		RunAll(deferred);

		Assert.HasCount(3, errors);
		Assert.AreEqual(new LoggedError("bot1", "OnConnected() SteamLogin is invalid!", time.Now), errors[0]);
		Assert.AreEqual("ASF", errors[1].LoggerName);
		Assert.AreEqual("Init() broken", errors[1].Message);
		Assert.AreEqual("OnBotDisconnected()", errors[2].Message);
		Assert.IsInstanceOfType<InvalidOperationException>(errors[2].Exception);

		// The give-up record works as before
		Assert.IsNotNull(tap.Get("bot1").GiveUpError);
	}

	[TestMethod]
	internal void WithoutACallbackNothingIsHandedOn() {
		using LogFactory factory = Factory();
		List<Action> deferred = [];

		using GiveUpLogTap tap = new(factory, new FakeClock(), deferred.Add);

		factory.GetLogger("bot1").Error("Start() failed");

		Assert.IsEmpty(deferred);
	}

	// AsfError off everywhere: no task, no capture, only the cheap check
	[TestMethod]
	internal void WithAsfErrorOffNothingIsHandedOn() {
		using LogFactory factory = Factory();
		List<Action> deferred = [];
		List<LoggedError> errors = [];
		int asked = 0;

		using GiveUpLogTap tap = new(factory, new FakeClock(), deferred.Add, errors.Add, () => {
			asked++;

			return false;
		});

		factory.GetLogger("bot1").Error("OnConnected() SteamLogin is invalid!");
		factory.GetLogger("bot1").Warn("Start() only a warning");

		Assert.AreEqual(1, asked);
		Assert.IsEmpty(deferred);
		Assert.IsNotNull(tap.Get("bot1").GiveUpError);
	}

	// Nothing on the logging thread reads the exception's text, so one that cannot be printed costs nothing there
	[TestMethod]
	internal void AnExceptionThatCannotBePrintedKeepsTheGiveUpRecord() {
		using LogFactory factory = Factory();
		List<Action> deferred = [];
		List<LoggedError> errors = [];

		using GiveUpLogTap tap = new(factory, new FakeClock(), deferred.Add, errors.Add);

		factory.GetLogger("bot1").Error(new UnprintableException(), "OnConnected() SteamLogin is invalid!");

		Assert.AreEqual("OnConnected", tap.Get("bot1").GiveUpError?.Method);
		RunAll(deferred);
		Assert.IsInstanceOfType<UnprintableException>(errors.Single().Exception);
	}

	[TestMethod]
	internal void TheCallbackDoesNotRunAfterDispose() {
		using LogFactory factory = Factory();
		List<Action> deferred = [];
		List<LoggedError> errors = [];
		GiveUpLogTap tap = new(factory, new FakeClock(), deferred.Add, errors.Add);

		factory.GetLogger("bot1").Error("Start() failed");
		tap.Dispose();
		RunAll(deferred);
		factory.GetLogger("bot1").Error("Start() failed again");
		RunAll(deferred);

		Assert.IsEmpty(errors);
	}

	private static LogFactory Factory() => new() { Configuration = new LoggingConfiguration() };

	private static void RunAll(List<Action> deferred) {
		foreach (Action action in deferred) {
			action();
		}

		deferred.Clear();
	}

#pragma warning disable CA1064 // Only these tests throw and catch it
	private sealed class UnprintableException : Exception {
		public UnprintableException() { }
		public UnprintableException(string message) : base(message) { }
		public UnprintableException(string message, Exception innerException) : base(message, innerException) { }

		public override string ToString() => throw new InvalidOperationException("no text");
	}
#pragma warning restore CA1064 // Only these tests throw and catch it
}
#pragma warning restore CA1812 // MSTest uses this class during runtime
