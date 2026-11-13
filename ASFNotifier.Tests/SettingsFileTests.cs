using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ASFNotifier.Tests;

#pragma warning disable CA1812 // MSTest uses this class during runtime
[TestClass]
internal sealed class SettingsFileTests {
	private const string FileSecret = "syt_FILE_SECRET_1111";
	private const string LegacySecret = "syt_LEGACY_SECRET_2222";

	private static string FileJson(string global = "{\"Matrix\": {\"HomeserverUrl\": \"https://file.example.org\", \"RoomId\": \"!file:example.org\"}}", string bots = "{\"bot1\": {\"Enabled\": false}}") =>
		"{\"formatVersion\": 1, \"global\": " + global + ", \"bots\": " + bots + "}";

	private static Dictionary<string, JsonElement> Legacy(string json = "{\"ASFNotifier\": {\"Matrix\": {\"HomeserverUrl\": \"https://legacy.example.org\", \"AccessToken\": \"" + LegacySecret + "\"}}, \"Other\": 1}") =>
		JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json) ?? throw new InvalidOperationException(nameof(json));

	private static string? Homeserver(IReadOnlyDictionary<string, JsonElement>? properties) =>
		properties?["ASFNotifier"].GetProperty("Matrix").GetProperty("HomeserverUrl").GetString();

	private static string WriteTemp(byte[] content) {
		string directory = Directory.CreateTempSubdirectory("asfnotifier-settings").FullName;
		string path = Path.Combine(directory, "ASFNotifier.json");
		File.WriteAllBytes(path, content);

		return path;
	}

	private static string WriteTemp(string content) => WriteTemp(Encoding.UTF8.GetBytes(content));

	private static IReadOnlyDictionary<string, JsonElement>? Resolve(string path, IReadOnlyDictionary<string, JsonElement>? legacy, string? bot, FakeLog log, out bool fromFile) {
		try {
			return SettingsFile.Resolve(path, legacy, bot, log, out fromFile);
		} finally {
			Directory.Delete(Path.GetDirectoryName(path)!, true);
		}
	}

	[TestMethod]
	internal void MissingFileUsesLegacySilently() {
		FakeLog log = new();
		Dictionary<string, JsonElement> legacy = Legacy();

		IReadOnlyDictionary<string, JsonElement>? result = SettingsFile.Resolve(Path.Combine(Path.GetTempPath(), "asfnotifier-missing", "ASFNotifier.json"), legacy, null, log, out bool fromFile);

		Assert.IsFalse(fromFile);
		Assert.AreSame(legacy, result);
		Assert.IsEmpty(log.All.ToList());
	}

	[TestMethod]
	internal void EmptyFileUsesLegacy() {
		FakeLog log = new();
		Dictionary<string, JsonElement> legacy = Legacy();

		IReadOnlyDictionary<string, JsonElement>? result = Resolve(WriteTemp(""), legacy, null, log, out bool fromFile);

		Assert.IsFalse(fromFile);
		Assert.AreSame(legacy, result);
		Assert.IsEmpty(log.Errors);
	}

	[TestMethod]
	internal void ByteOrderMarkIsAccepted() {
		FakeLog log = new();
		byte[] json = [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(FileJson())];

		IReadOnlyDictionary<string, JsonElement>? result = Resolve(WriteTemp(json), Legacy(), null, log, out bool fromFile);

		Assert.IsTrue(fromFile);
		Assert.AreEqual("https://file.example.org", Homeserver(result));
		Assert.IsEmpty(log.Errors);
	}

	[TestMethod]
	[DataRow("{\"formatVersion\": 2, \"global\": {}}")]
	[DataRow("{\"global\": {}}")]
	[DataRow("{\"formatVersion\": \"1\", \"global\": {}}")]
	[DataRow("[1]")]
	internal void UnknownFormatVersionLogsAndFallsBack(string content) {
		FakeLog log = new();
		Dictionary<string, JsonElement> legacy = Legacy();

		IReadOnlyDictionary<string, JsonElement>? result = Resolve(WriteTemp(content), legacy, null, log, out bool fromFile);

		Assert.IsFalse(fromFile);
		Assert.AreSame(legacy, result);
		Assert.HasCount(1, log.Errors);
	}

	[TestMethod]
	internal void BadJsonLogsAndFallsBackWithoutLeakingContent() {
		FakeLog log = new();
		Dictionary<string, JsonElement> legacy = Legacy();

		IReadOnlyDictionary<string, JsonElement>? result = Resolve(WriteTemp("{\"formatVersion\": 1, \"global\": {\"Matrix\": {\"AccessToken\": \"" + FileSecret + "\" oops"), legacy, null, log, out bool fromFile);

		Assert.IsFalse(fromFile);
		Assert.AreSame(legacy, result);
		Assert.HasCount(1, log.Errors);
		Assert.DoesNotContain(FileSecret, string.Join('\n', log.All), StringComparison.Ordinal);
	}

	[TestMethod]
	internal void GlobalScopeReplacesLegacyKeyWholeAndKeepsOtherKeys() {
		FakeLog log = new();

		IReadOnlyDictionary<string, JsonElement>? result = Resolve(WriteTemp(FileJson()), Legacy(), null, log, out bool fromFile);

		Assert.IsTrue(fromFile);
		Assert.IsNotNull(result);
		Assert.AreEqual("https://file.example.org", Homeserver(result));

		// Whole replacement: the legacy token is gone
		Assert.IsFalse(result["ASFNotifier"].GetProperty("Matrix").TryGetProperty("AccessToken", out _));
		Assert.AreEqual(1, result["Other"].GetInt32());
		Assert.HasCount(1, log.Warnings);
	}

	[TestMethod]
	internal void BotScopeReadsOnlyThatBot() {
		string path = WriteTemp(FileJson());

		try {
			FakeLog log = new();
			Dictionary<string, JsonElement> legacy = Legacy("{\"SteamLogin\": \"x\"}");

			IReadOnlyDictionary<string, JsonElement>? result = SettingsFile.Resolve(path, legacy, "bot1", log, out bool fromFile);

			Assert.IsTrue(fromFile);
			Assert.IsNotNull(result);
			Assert.IsFalse(result["ASFNotifier"].GetProperty("Enabled").GetBoolean());
			Assert.IsEmpty(log.Warnings);

			// A bot the file does not list, and a bot scope never reads the global part
			IReadOnlyDictionary<string, JsonElement>? other = SettingsFile.Resolve(path, legacy, "bot2", log, out bool otherFromFile);

			Assert.IsFalse(otherFromFile);
			Assert.AreSame(legacy, other);
		} finally {
			Directory.Delete(Path.GetDirectoryName(path)!, true);
		}
	}

	[TestMethod]
	internal void MissingScopeFallsBackToLegacy() {
		FakeLog log = new();
		Dictionary<string, JsonElement> legacy = Legacy();

		IReadOnlyDictionary<string, JsonElement>? result = Resolve(WriteTemp("{\"formatVersion\": 1}"), legacy, null, log, out bool fromFile);

		Assert.IsFalse(fromFile);
		Assert.AreSame(legacy, result);
		Assert.IsEmpty(log.All.ToList());
	}

	[TestMethod]
	internal void FileWithoutLegacyKeyDoesNotWarn() {
		FakeLog log = new();

		IReadOnlyDictionary<string, JsonElement>? result = Resolve(WriteTemp(FileJson()), null, null, log, out bool fromFile);

		Assert.IsTrue(fromFile);
		Assert.AreEqual("https://file.example.org", Homeserver(result));
		Assert.IsEmpty(log.All.ToList());
	}

	[TestMethod]
	internal void IgnoredLegacyKeyWarnsWithoutSecretValues() {
		FakeLog log = new();

		Resolve(WriteTemp(FileJson("{\"Matrix\": {\"AccessToken\": \"" + FileSecret + "\"}}")), Legacy(), null, log, out _);

		Assert.HasCount(1, log.Warnings);
		Assert.Contains("ignored", log.Warnings[0], StringComparison.Ordinal);

		string logged = string.Join('\n', log.All);
		Assert.DoesNotContain(FileSecret, logged, StringComparison.Ordinal);
		Assert.DoesNotContain(LegacySecret, logged, StringComparison.Ordinal);
	}

	[TestMethod]
	internal void ParseFromFileDoesNotWarnAboutIpcExposure() {
		FakeLog log = new();
		Dictionary<string, JsonElement> properties = Legacy("{\"ASFNotifier\": {\"Matrix\": {\"HomeserverUrl\": \"" + TestData.Homeserver + "\", \"RoomId\": \"" + TestData.RoomId + "\", \"AccessToken\": \"" + FileSecret + "\"}}}");

		NotifierSettings fromFile = NotifierSettings.Parse(properties, null, log, true);

		Assert.IsTrue(fromFile.IsConfigured);
		Assert.AreEqual(FileSecret, fromFile.AccessToken);
		Assert.IsEmpty(log.Warnings);
		Assert.DoesNotContain(FileSecret, string.Join('\n', log.All), StringComparison.Ordinal);

		// The same block read from ASF.json is served by IPC and still warns
		FakeLog legacyLog = new();
		NotifierSettings.Parse(properties, null, legacyLog);

		Assert.HasCount(1, legacyLog.Warnings);
	}

	[TestMethod]
	internal void EnvironmentTokenWinsOverFileToken() {
		Dictionary<string, JsonElement> properties = Legacy("{\"ASFNotifier\": {\"Matrix\": {\"HomeserverUrl\": \"" + TestData.Homeserver + "\", \"RoomId\": \"" + TestData.RoomId + "\", \"AccessToken\": \"" + FileSecret + "\"}}}");

		NotifierSettings settings = NotifierSettings.Parse(properties, TestData.Token, new FakeLog(), true);

		Assert.AreEqual(TestData.Token, settings.AccessToken);
	}

	[TestMethod]
	internal void SchemaEventsMatchConfigurableKinds() {
		using Stream stream = typeof(NotifierSettings).Assembly.GetManifestResourceStream("ASFNotifier.config.schema.json") ?? throw new InvalidOperationException("Schema resource not embedded");
		using JsonDocument schema = JsonDocument.Parse(stream);
		JsonElement root = schema.RootElement;

		Assert.AreEqual("ASFNotifier", root.GetProperty("configKey").GetString());
		Assert.AreEqual("file", root.GetProperty("storage").GetString());
		Assert.AreEqual(1, root.GetProperty("formatVersion").GetInt32());
		Assert.AreEqual("restart", root.GetProperty("reload").GetString());

		string[] expectedGlobal = [.. NotifierSettings.ConfigurableKinds.Keys.Order(StringComparer.Ordinal)];
		string[] expectedBot = [.. expectedGlobal.Where(static name => name != nameof(NotificationKind.AsfUpdated))];

		JsonElement global = root.GetProperty("global").GetProperty("properties");
		JsonElement bot = root.GetProperty("bot").GetProperty("properties");

		CollectionAssert.AreEqual(expectedGlobal, global.GetProperty("Events").GetProperty("properties").EnumerateObject().Select(static p => p.Name).Order(StringComparer.Ordinal).ToArray());
		CollectionAssert.AreEqual(expectedBot, bot.GetProperty("Events").GetProperty("properties").EnumerateObject().Select(static p => p.Name).Order(StringComparer.Ordinal).ToArray());
		Assert.IsTrue(global.GetProperty("Matrix").GetProperty("properties").GetProperty("AccessToken").GetProperty("writeOnly").GetBoolean());

		// Booleans carry no default, so the plugin default of section 6 stays authoritative
		foreach (JsonProperty property in global.GetProperty("Events").GetProperty("properties").EnumerateObject().Concat(bot.GetProperty("Events").GetProperty("properties").EnumerateObject())) {
			Assert.IsFalse(property.Value.TryGetProperty("default", out _), property.Name);
		}
	}
}
