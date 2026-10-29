using System;
using System.Collections.Generic;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ASFNotifier.Tests;

#pragma warning disable CA1812 // MSTest uses this class during runtime
[TestClass]
internal sealed class BotSettingsTests {
	private static Dictionary<string, JsonElement> Props(string? block) => JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(block == null ? "{}" : "{\"ASFNotifier\": " + block + "}")!;

	private static BotSettings? Bot(string? block, FakeLog? log = null) => BotSettings.Parse("bot1", Props(block), log ?? new FakeLog());

	private static Notification Of(NotificationKind kind, string? bot = "bot1") => new(kind, bot, DateTimeOffset.UnixEpoch);

	private static string EventsJson(bool? value, NotificationKind kind) => value is { } v ? "\"Events\": {\"" + kind + "\": " + (v ? "true" : "false") + "}" : "";

	private static NotifierSettings Global(bool enabled, bool? key, NotificationKind kind) {
		List<string> parts = [];

		if (!enabled) {
			parts.Add("\"Enabled\": false");
		}

		if (key != null) {
			parts.Add(EventsJson(key, kind));
		}

		return TestData.Settings(TestData.ConfigJson(parts.Count > 0 ? ", " + string.Join(", ", parts) : ""), TestData.Token);
	}

	private static BotSettings? BotOf(bool enabled, bool? key, NotificationKind kind) {
		List<string> parts = [];

		if (!enabled) {
			parts.Add("\"Enabled\": false");
		}

		if (key != null) {
			parts.Add(EventsJson(key, kind));
		}

		return Bot("{" + string.Join(", ", parts) + "}");
	}

	[TestMethod]
	internal void FullPrecedenceTable() {
		bool?[] keys = [null, true, false];

		foreach (NotificationKind kind in new[] { NotificationKind.FarmingFinished, NotificationKind.FarmingStarted, NotificationKind.AsfError }) {
			bool builtIn = kind == NotificationKind.FarmingFinished;

			foreach (bool globalEnabled in new[] { true, false }) {
				foreach (bool botEnabled in new[] { true, false }) {
					foreach (bool? botKey in keys) {
						foreach (bool? globalKey in keys) {
							// global Enabled, bot Enabled, bot key, global key, built-in default
							bool expected = globalEnabled && botEnabled && (botKey ?? globalKey ?? builtIn);

							bool actual = Filter.Allows(Of(kind), Global(globalEnabled, globalKey, kind), BotOf(botEnabled, botKey, kind));

							Assert.AreEqual(expected, actual, $"{kind}: global {globalEnabled}/{globalKey}, bot {botEnabled}/{botKey}");
						}
					}
				}
			}
		}
	}

	[TestMethod]
	internal void ABotWithoutTheBlockInheritsTheGlobalSettings() {
		NotifierSettings global = Global(true, false, NotificationKind.FarmingFinished);
		BotConfigStore store = new();

		store.Replace("bot1", Props(null), new FakeLog());

		Assert.IsNull(store.Get("bot1"));
		Assert.IsFalse(Filter.Allows(Of(NotificationKind.FarmingFinished), global, store.Get("bot1")));
		Assert.IsTrue(Filter.Allows(Of(NotificationKind.BotDisconnected), global, store.Get("bot1")));
	}

	[TestMethod]
	internal void ADisabledBotSendsNothingAndTheOtherBotIsUnaffected() {
		NotifierSettings global = Global(true, null, NotificationKind.FarmingFinished);
		BotConfigStore store = new();

		store.Replace("quiet", Props("{\"Enabled\": false, \"Events\": {\"BotDisconnected\": true}}"), new FakeLog());

		Assert.IsFalse(Filter.Allows(Of(NotificationKind.BotDisconnected, "quiet"), global, store.Get("quiet")));
		Assert.IsTrue(Filter.Allows(Of(NotificationKind.BotDisconnected, "other"), global, store.Get("other")));
	}

	[TestMethod]
	internal void OneEventOnAndOneOffLeaveTheRestInherited() {
		NotifierSettings global = Global(true, null, NotificationKind.FarmingFinished);
		BotSettings? bot = Bot("{\"Events\": {\"FarmingStarted\": true, \"FarmingFinished\": false}}");

		Assert.IsTrue(Filter.Allows(Of(NotificationKind.FarmingStarted), global, bot));
		Assert.IsFalse(Filter.Allows(Of(NotificationKind.FarmingFinished), global, bot));
		Assert.IsTrue(Filter.Allows(Of(NotificationKind.BotDisconnected), global, bot));
		Assert.IsFalse(Filter.Allows(Of(NotificationKind.BotStopped), global, bot));
	}

	[TestMethod]
	internal void ReloadReplacesTheOldValuesAndDestroyRemovesThem() {
		BotConfigStore store = new();

		store.Replace("bot1", Props("{\"Enabled\": false, \"Events\": {\"FarmingStarted\": true}}"), new FakeLog());
		Assert.IsFalse(store.Get("bot1")!.Enabled);

		store.Replace("bot1", Props("{\"Events\": {\"FarmingFinished\": false}}"), new FakeLog());

		BotSettings reloaded = store.Get("bot1")!;
		Assert.IsTrue(reloaded.Enabled);
		Assert.HasCount(1, reloaded.Events);
		Assert.IsFalse(reloaded.Events[NotificationKind.FarmingFinished]);

		store.Replace("bot1", Props(null), new FakeLog());
		Assert.IsNull(store.Get("bot1"));

		store.Replace("bot1", Props("{\"Enabled\": false}"), new FakeLog());
		store.Remove("bot1");
		Assert.IsNull(store.Get("bot1"));
	}

	[TestMethod]
	internal void AnAsfWideNotificationBypassesThePerBotLayer() {
		NotifierSettings global = Global(true, null, NotificationKind.AsfUpdated);
		BotSettings disabled = Bot("{\"Enabled\": false}")!;

		Assert.IsTrue(Filter.Allows(Of(NotificationKind.AsfUpdated, null), global, disabled));
		Assert.IsTrue(Filter.Allows(Of(NotificationKind.Suppressed, null), global, disabled));
		Assert.IsFalse(Filter.Allows(Of(NotificationKind.AsfUpdated, null), Global(false, null, NotificationKind.AsfUpdated), disabled));
	}

	[TestMethod]
	internal void AsfUpdatedInABotConfigIsIgnoredWithAWarning() {
		FakeLog log = new();
		BotSettings bot = Bot("{\"Events\": {\"AsfUpdated\": false}}", log)!;

		Assert.IsEmpty(bot.Events);
		Assert.HasCount(1, log.Warnings);
		Assert.Contains("bot1", log.Warnings[0], StringComparison.Ordinal);
		Assert.Contains("AsfUpdated", log.Warnings[0], StringComparison.Ordinal);
	}

	[TestMethod]
	internal void UnknownKeysAndInvalidTypesWarnAndFallBackToInherited() {
		FakeLog log = new();
		BotSettings bot = Bot("{\"Enabled\": \"no\", \"Room\": \"!x:y\", \"Events\": {\"Nope\": true, \"FarmingFinished\": \"off\", \"Test\": true, \"FarmingStarted\": true}}", log)!;

		Assert.IsTrue(bot.Enabled);
		Assert.HasCount(1, bot.Events);
		Assert.IsTrue(bot.Events[NotificationKind.FarmingStarted]);
		Assert.HasCount(5, log.Warnings);
		Assert.IsEmpty(log.Errors);
	}

	[TestMethod]
	internal void ABlockThatIsNotAnObjectAndAnEventsThatIsNotAnObjectInherit() {
		FakeLog log = new();

		Assert.IsNull(Bot("true", log));

		BotSettings bot = Bot("{\"Events\": [1]}", log)!;

		Assert.IsTrue(bot.Enabled);
		Assert.IsEmpty(bot.Events);
		Assert.HasCount(2, log.Warnings);
	}
}
#pragma warning restore CA1812 // MSTest uses this class during runtime
