using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ASFNotifier.Tests;

// The adapter's AsfError glue, end to end through the filter, the rate limiter and a recording queue, the way the
// adapter's Emit chains them
#pragma warning disable CA1812 // MSTest uses this class during runtime
[TestClass]
internal sealed class AsfErrorForwarderTests {
	private const string GlobalOn = ", \"Events\": {\"AsfError\": true}";

	private static readonly string[] Bot1 = ["bot1"];
	private static readonly string[] Bot1AndAsf = ["bot1", "ASF"];
	private static readonly string[] StartThenOnConnected = ["Start", "OnConnected"];

	private sealed class Rig {
		private readonly RateLimiter Limiter;

		internal BotConfigStore Bots { get; } = new();
		internal AsfErrorForwarder Forwarder { get; }
		internal FakeLog Log { get; } = new();
		internal List<Notification> Queued { get; } = [];
		internal NotifierSettings? Settings { get; set; }
		internal FakeClock Time { get; } = new();

		internal Rig(string? extra = "", Func<Notification, bool>? emit = null) {
			Settings = extra == null ? null : TestData.Settings(TestData.ConfigJson(extra), TestData.Token);
			Limiter = new RateLimiter(Time);
			Forwarder = new AsfErrorForwarder(() => Settings, Bots, static name => name is "bot1" or "bot2", emit ?? Emit, Log);
		}

		internal void Bot(string name, string block) => Bots.Replace(name, JsonSerializer.Deserialize<Dictionary<string, JsonElement>>("{\"ASFNotifier\": " + block + "}"), new FakeLog());

		internal void Error(string logger, string message, Exception? exception = null) => Forwarder.Forward(new LoggedError(logger, message, Time.Now, exception));

		internal string?[] Sent() => Queued.Where(static notification => notification.Kind == NotificationKind.AsfError).Select(static notification => notification.BotName ?? "ASF").ToArray();

		private bool Emit(Notification notification) =>
			(Settings != null) && Filter.Allows(notification, Settings, Bots.Get(notification.BotName)) && Limiter.Pass(notification, queued => {
				Queued.Add(queued);

				return true;
			});
	}

	[TestMethod]
	internal void WithoutSettingsNothingIsWantedOrForwarded() {
		Rig rig = new(null);

		rig.Error("bot1", "Start() failed");

		Assert.IsFalse(rig.Forwarder.Wanted);
		Assert.IsEmpty(rig.Queued);
		Assert.IsEmpty(rig.Log.All);
	}

	[TestMethod]
	internal void OffByDefault() {
		Rig rig = new();

		rig.Error("bot1", "Start() failed");
		rig.Error("ASF", "Update() failed");

		Assert.IsFalse(rig.Forwarder.Wanted);
		Assert.IsEmpty(rig.Queued);
	}

	[TestMethod]
	internal void GloballyOnABotLoggerNamesTheBotAndTheAsfLoggerNamesNone() {
		Rig rig = new(GlobalOn);

		rig.Error("bot1", "Start() failed");
		rig.Error("ASF", "Update() failed");
		rig.Error("Microsoft.AspNetCore.Hosting", "Start() failed");

		Assert.IsTrue(rig.Forwarder.Wanted);
		CollectionAssert.AreEqual(Bot1AndAsf, rig.Sent());
		Assert.AreEqual("Start", rig.Queued[0].Method);
	}

	[TestMethod]
	internal void ABotOverrideTurnsItOnForThatBotOnly() {
		Rig rig = new();
		rig.Bot("bot1", """{"Events": {"AsfError": true}}""");

		rig.Error("bot1", "Start() failed");
		rig.Error("bot2", "Start() failed");
		rig.Error("ASF", "Update() failed");

		Assert.IsTrue(rig.Forwarder.Wanted);
		CollectionAssert.AreEqual(Bot1, rig.Sent());
	}

	[TestMethod]
	internal void ABotOverrideTurnsItOffForThatBot() {
		Rig rig = new(GlobalOn);
		rig.Bot("bot2", """{"Events": {"AsfError": false}}""");

		rig.Error("bot1", "Start() failed");
		rig.Error("bot2", "Start() failed");

		CollectionAssert.AreEqual(Bot1, rig.Sent());
	}

	[TestMethod]
	internal void ADisabledBotOrPluginWantsNothing() {
		Rig disabledBot = new();
		disabledBot.Bot("bot1", """{"Enabled": false, "Events": {"AsfError": true}}""");

		Rig disabledPlugin = new();
		disabledPlugin.Settings = TestData.Settings("""{"ASFNotifier": {"Enabled": false, "Events": {"AsfError": true}}}""", TestData.Token);

		Assert.IsFalse(disabledBot.Forwarder.Wanted);
		Assert.IsFalse(disabledPlugin.Forwarder.Wanted);
	}

	[TestMethod]
	internal void GiveUpErrorsAreSkippedWhereBotStoppedByAsfIsOn() {
		Rig rig = new(GlobalOn);
		rig.Bot("bot2", """{"Events": {"BotStoppedByAsf": false}}""");

		rig.Error("bot1", "OnConnected() SteamLogin is invalid!");
		rig.Error("bot1", "Start() failed");
		rig.Error("bot2", "OnConnected() SteamLogin is invalid!");

		CollectionAssert.AreEqual(StartThenOnConnected, rig.Queued.Select(static notification => notification.Method).ToArray());
		Assert.AreEqual("bot2", rig.Queued[1].BotName);
	}

	[TestMethod]
	internal void ARepeatedErrorIsSentOnce() {
		Rig rig = new(GlobalOn);

		rig.Error("bot1", "Start() failed");
		rig.Error("bot1", "Start() failed");

		Assert.HasCount(1, rig.Queued);
	}

	[TestMethod]
	internal void ThePluginsOwnLinesAndTheTokenStayOut() {
		Rig rig = new(GlobalOn);

		rig.Error("ASF", "ASFNotifier() Matrix rejected the credentials or permissions (401 M_UNKNOWN_TOKEN)");
		rig.Error("bot1", $"Start() token {TestData.Token}");

		Assert.IsEmpty(rig.Queued);
	}

	[TestMethod]
	internal void AFailureIsLoggedAsThePluginWithoutDetailsAndNeverThrows() {
		Rig rig = new(GlobalOn, static _ => throw new InvalidOperationException($"boom {TestData.Token}"));

		rig.Error("bot1", "Start() failed");

		Assert.AreEqual("Forwarding an ASF error failed (InvalidOperationException)", rig.Log.Errors.Single());
	}
}
#pragma warning restore CA1812 // MSTest uses this class during runtime
