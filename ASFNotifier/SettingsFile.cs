using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using ArchiSteamFarm;

namespace ASFNotifier;

// config/plugins/ASFNotifier.json, the ASF-PluginConfig settings file (storage standard v1): { "formatVersion": 1, "global": { }, "bots": { "<bot>": { } } }.
// Where it has a value, it replaces the legacy ASFNotifier key of ASF.json or of the bot's config for that scope, whole
internal static class SettingsFile {
	internal const string ConfigKey = "ASFNotifier";

	private const int FormatVersion = 1;
	internal const string FileName = "ASFNotifier.json";

	private static readonly JsonDocumentOptions Options = new() { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip };

	internal static string DefaultPath => Path.Combine(SharedInfo.ConfigDirectory, "plugins", FileName);

	// bot null is the global scope. The legacy properties with the ASFNotifier key replaced by the file's value, or legacy
	// itself when the file has none. Never log a value
	internal static IReadOnlyDictionary<string, JsonElement>? Resolve(string path, IReadOnlyDictionary<string, JsonElement>? legacy, string? bot, INotifierLog log, out bool fromFile) {
		ArgumentException.ThrowIfNullOrEmpty(path);
		ArgumentNullException.ThrowIfNull(log);

		fromFile = false;

		if (Read(path, bot, log) is not { } value) {
			return legacy;
		}

		fromFile = true;

		Dictionary<string, JsonElement> merged = legacy == null ? [] : new Dictionary<string, JsonElement>(legacy, StringComparer.Ordinal);

		if (merged.ContainsKey(ConfigKey)) {
			// ASF.json is served by GET /Api/ASF, so a leftover token there stays exposed
			log.Warning($"{(bot == null ? "ASF.json" : $"Bot {bot}: the bot config")} still has an {ConfigKey} key; it is ignored in favour of {FileName}, delete it");
		}

		merged[ConfigKey] = value;

		return merged;
	}

	private static JsonElement? Read(string path, string? bot, INotifierLog log) {
		try {
			if (!File.Exists(path)) {
				return null;
			}

			byte[] bytes = File.ReadAllBytes(path);

			if (bytes.Length == 0) {
				return null;
			}

			// Notepad saves a UTF-8 byte order mark, which JsonDocument refuses
			ReadOnlyMemory<byte> json = bytes.AsSpan().StartsWith((ReadOnlySpan<byte>) [0xEF, 0xBB, 0xBF]) ? bytes.AsMemory(3) : bytes;

			using JsonDocument document = JsonDocument.Parse(json, Options);
			JsonElement root = document.RootElement;

			if ((root.ValueKind != JsonValueKind.Object) || !root.TryGetMember("formatVersion", out JsonElement version) || (version.ValueKind != JsonValueKind.Number) || (version.GetDouble() != FormatVersion)) {
				log.Error($"{FileName} has a format this plugin does not know (expected \"formatVersion\": {FormatVersion}), using the {ConfigKey} key of the ASF configs instead");

				return null;
			}

			JsonElement scope;

			if (bot == null) {
				if (!root.TryGetMember("global", out scope)) {
					return null;
				}
			} else if (!root.TryGetMember("bots", out JsonElement bots) || (bots.ValueKind != JsonValueKind.Object) || !bots.TryGetMember(bot, out scope)) {
				return null;
			}

			return scope.Clone();
		} catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException) {
			// The message of a JsonException carries a position, never a value
			log.Error($"{FileName} could not be read ({e.GetType().Name}), using the {ConfigKey} key of the ASF configs instead");

			return null;
		}
	}
}
