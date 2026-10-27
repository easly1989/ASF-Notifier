using System;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ASFNotifier.Tests;

#pragma warning disable CA1812 // MSTest uses this class during runtime
[TestClass]
internal sealed class NotifierSettingsTests {
	private static bool Leaks(FakeLog log, params string[] secrets) => log.All.Any(line => secrets.Any(secret => line.Contains(secret, StringComparison.Ordinal)));

	[TestMethod]
	internal void AbsentBlockIsIdleWithOneInfoLine() {
		FakeLog log = new();

		NotifierSettings settings = NotifierSettings.Parse(null, TestData.Token, log);

		Assert.IsFalse(settings.IsConfigured);
		Assert.HasCount(1, log.Infos);
		Assert.IsEmpty(log.Warnings);
		Assert.IsEmpty(log.Errors);

		FakeLog other = new();
		Assert.IsFalse(TestData.Settings("""{"Unrelated": 1}""", null, other).IsConfigured);
		Assert.HasCount(1, other.Infos);
		Assert.IsEmpty(other.Errors);
	}

	[TestMethod]
	internal void DisabledIsIdleWithoutErrors() {
		FakeLog log = new();

		NotifierSettings settings = TestData.Settings("""{"ASFNotifier": {"Enabled": false}}""", null, log);

		Assert.IsFalse(settings.Enabled);
		Assert.IsFalse(settings.IsConfigured);
		Assert.IsEmpty(log.Errors);
	}

	[TestMethod]
	[DataRow("1", 1, false)]
	[DataRow("30", 30, false)]
	[DataRow("1440", 1440, false)]
	[DataRow("0", 5, true)]
	[DataRow("1441", 5, true)]
	[DataRow("-3", 5, true)]
	[DataRow("5.5", 5, true)]
	[DataRow("\"ten\"", 5, true)]
	internal void DisconnectedNotifyAfterMinutesParsing(string value, int expected, bool warns) {
		FakeLog log = new();

		NotifierSettings settings = TestData.Settings(TestData.ConfigJson($", \"DisconnectedNotifyAfterMinutes\": {value}"), TestData.Token, log);

		Assert.AreEqual(expected, settings.DisconnectedNotifyAfterMinutes);
		Assert.HasCount(warns ? 1 : 0, log.Warnings);
		Assert.IsTrue(settings.IsConfigured);
	}

	[TestMethod]
	internal void DisconnectedNotifyAfterMinutesDefaultsToFive() =>
		Assert.AreEqual(5, TestData.Settings(TestData.ConfigJson(), TestData.Token).DisconnectedNotifyAfterMinutes);

	[TestMethod]
	[DataRow("env-token", "json-token", "env-token")]
	[DataRow("", "json-token", "json-token")]
	[DataRow("   ", "json-token", "json-token")]
	[DataRow(null, "json-token", "json-token")]
	internal void EnvironmentTokenWinsWhenNonEmpty(string? environment, string json, string expected) {
		NotifierSettings settings = TestData.Settings($$$"""{"ASFNotifier": {"Matrix": {"HomeserverUrl": "{{{TestData.Homeserver}}}", "RoomId": "{{{TestData.RoomId}}}", "AccessToken": "{{{json}}}"}} }""", environment);

		Assert.AreEqual(expected, settings.AccessToken);
	}

	[TestMethod]
	internal void EnvironmentTokenAloneDoesNotWarn() {
		FakeLog log = new();

		TestData.Settings(TestData.ConfigJson(), TestData.Token, log);

		Assert.IsEmpty(log.Warnings);
	}

	[TestMethod]
	internal void EventsMapIsParsedAndBadEntriesWarn() {
		FakeLog log = new();

		NotifierSettings settings = TestData.Settings(TestData.ConfigJson(""", "Events": {"BotStopped": true, "FarmingFinished": false, "Bogus": true, "AsfUpdated": "yes", "Test": true, "Suppressed": false}"""), TestData.Token, log);

		Assert.HasCount(2, settings.Events);
		Assert.IsTrue(settings.Events[NotificationKind.BotStopped]);
		Assert.IsFalse(settings.Events[NotificationKind.FarmingFinished]);
		Assert.HasCount(4, log.Warnings);
		Assert.IsTrue(log.Warnings.Any(static line => line.Contains("Bogus", StringComparison.Ordinal)));
	}

	[TestMethod]
	internal void FullConfigWithEnvironmentToken() {
		FakeLog log = new();

		NotifierSettings settings = TestData.Settings(TestData.ConfigJson(), TestData.Token, log);

		Assert.IsTrue(settings.IsConfigured);
		Assert.IsNull(settings.NotConfiguredReason);
		Assert.IsTrue(settings.Enabled);
		Assert.AreEqual(TestData.Homeserver, settings.HomeserverUrl);
		Assert.AreEqual(TestData.RoomId, settings.RoomId);
		Assert.AreEqual(TestData.Token, settings.AccessToken);
		Assert.IsEmpty(log.Warnings);
		Assert.IsEmpty(log.Errors);
		Assert.IsTrue(log.Infos.Any(static line => line.Contains("environment", StringComparison.OrdinalIgnoreCase)));
		Assert.IsFalse(Leaks(log, TestData.Token));
	}

	[TestMethod]
	internal void HttpHomeserverWarnsOnceAndTrailingSlashIsIgnored() {
		FakeLog log = new();

		NotifierSettings settings = TestData.Settings("""{"ASFNotifier": {"Matrix": {"HomeserverUrl": "http://matrix.example.org/", "RoomId": "!room:example.org"}}}""", TestData.Token, log);

		Assert.IsTrue(settings.IsConfigured);
		Assert.AreEqual("http://matrix.example.org", settings.HomeserverUrl);
		Assert.HasCount(1, log.Warnings);
		StringAssert.Contains(log.Warnings[0], "http://", StringComparison.Ordinal);
	}

	[TestMethod]
	internal void HttpsHomeserverDoesNotWarn() {
		FakeLog log = new();

		TestData.Settings(TestData.ConfigJson(), TestData.Token, log);

		Assert.IsEmpty(log.Warnings);
	}

	[TestMethod]
	[DataRow("not a url")]
	[DataRow("ftp://matrix.example.org")]
	internal void InvalidHomeserverUrlIsAnError(string url) {
		FakeLog log = new();

		NotifierSettings settings = TestData.Settings($$$"""{"ASFNotifier": {"Matrix": {"HomeserverUrl": "{{{url}}}", "RoomId": "!room:example.org"}} }""", TestData.Token, log);

		Assert.IsFalse(settings.IsConfigured);
		Assert.HasCount(1, log.Errors);
		StringAssert.Contains(log.Errors[0], "Matrix.HomeserverUrl", StringComparison.Ordinal);
	}

	[TestMethod]
	[DataRow("HomeserverUrl", """{"RoomId": "!room:example.org"}""")]
	[DataRow("RoomId", """{"HomeserverUrl": "https://matrix.example.org"}""")]
	internal void MissingKeyIsOneErrorNamingIt(string key, string matrix) {
		FakeLog log = new();

		NotifierSettings settings = TestData.Settings($$$"""{"ASFNotifier": {"Matrix": {{{matrix}}}} }""", TestData.Token, log);

		Assert.IsFalse(settings.IsConfigured);
		Assert.HasCount(1, log.Errors);
		StringAssert.Contains(log.Errors[0], "Matrix." + key, StringComparison.Ordinal);
		StringAssert.Contains(settings.NotConfiguredReason, "Matrix." + key, StringComparison.Ordinal);
		Assert.IsFalse(Leaks(log, TestData.Token, TestData.RoomId, TestData.Homeserver));
	}

	[TestMethod]
	internal void MissingTokenIsOneErrorNamingTheKey() {
		FakeLog log = new();

		NotifierSettings settings = TestData.Settings(TestData.ConfigJson(), null, log);

		Assert.IsFalse(settings.IsConfigured);
		Assert.HasCount(1, log.Errors);
		StringAssert.Contains(log.Errors[0], "Matrix.AccessToken", StringComparison.Ordinal);
		StringAssert.Contains(log.Errors[0], "ASF_NOTIFIER_MATRIX_ACCESS_TOKEN", StringComparison.Ordinal);
	}

	[TestMethod]
	internal void NonBooleanEnabledWarnsAndDefaultsToTrue() {
		FakeLog log = new();

		NotifierSettings settings = TestData.Settings(TestData.ConfigJson(""", "Enabled": "no" """), TestData.Token, log);

		Assert.IsTrue(settings.Enabled);
		Assert.HasCount(1, log.Warnings);
	}

	[TestMethod]
	internal void NonObjectBlockIsIdleWithOneError() {
		FakeLog log = new();

		NotifierSettings settings = TestData.Settings("""{"ASFNotifier": 5}""", TestData.Token, log);

		Assert.IsFalse(settings.IsConfigured);
		Assert.HasCount(1, log.Errors);
	}

	[TestMethod]
	internal void NothingConfiguredNamesEveryMissingKeyInOneLine() {
		FakeLog log = new();

		NotifierSettings settings = TestData.Settings("""{"ASFNotifier": {}}""", null, log);

		Assert.IsFalse(settings.IsConfigured);
		Assert.HasCount(1, log.Errors);
		StringAssert.Contains(log.Errors[0], "Matrix.HomeserverUrl", StringComparison.Ordinal);
		StringAssert.Contains(log.Errors[0], "Matrix.RoomId", StringComparison.Ordinal);
		StringAssert.Contains(log.Errors[0], "Matrix.AccessToken", StringComparison.Ordinal);
	}

	[TestMethod]
	internal void TokenFromJsonWarnsAboutIpc() {
		FakeLog log = new();

		NotifierSettings settings = TestData.Settings($$$"""{"ASFNotifier": {"Matrix": {"HomeserverUrl": "{{{TestData.Homeserver}}}", "RoomId": "{{{TestData.RoomId}}}", "AccessToken": "{{{TestData.Token}}}"}} }""", null, log);

		Assert.IsTrue(settings.IsConfigured);
		Assert.AreEqual(TestData.Token, settings.AccessToken);
		Assert.HasCount(1, log.Warnings);
		StringAssert.Contains(log.Warnings[0], "IPC", StringComparison.Ordinal);
		StringAssert.Contains(log.Warnings[0], "ASF_NOTIFIER_MATRIX_ACCESS_TOKEN", StringComparison.Ordinal);
		Assert.IsTrue(log.Infos.Any(static line => line.Contains("ASF.json", StringComparison.Ordinal)));
		Assert.IsFalse(Leaks(log, TestData.Token));
	}
}
#pragma warning restore CA1812 // MSTest uses this class during runtime
