using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace ASFNotifier;

internal sealed class NotifierSettings {
	internal const string EnvironmentTokenVariable = "ASF_NOTIFIER_MATRIX_ACCESS_TOKEN";

	private const int DefaultDisconnectedNotifyAfterMinutes = 5;
	private const string ConfigKey = "ASFNotifier";

	private static readonly Dictionary<string, NotificationKind> ConfigurableKinds = Enum.GetValues<NotificationKind>().Where(static kind => kind is not (NotificationKind.Suppressed or NotificationKind.Test)).ToDictionary(static kind => kind.ToString(), static kind => kind, StringComparer.Ordinal);

	internal string? AccessToken { get; private init; }
	internal int DisconnectedNotifyAfterMinutes { get; private init; } = DefaultDisconnectedNotifyAfterMinutes;
	internal bool Enabled { get; private init; } = true;
	internal IReadOnlyDictionary<NotificationKind, bool> Events { get; private init; } = new Dictionary<NotificationKind, bool>();
	internal string? HomeserverUrl { get; private init; }
	internal bool IsConfigured => NotConfiguredReason == null;
	internal string? NotConfiguredReason { get; private init; }
	internal string? RoomId { get; private init; }

	private static NotifierSettings Idle(string reason, bool enabled = true) => new() { Enabled = enabled, NotConfiguredReason = reason };

	// Never put a config value, the token above all, into a log line: only key names
	internal static NotifierSettings Parse(IReadOnlyDictionary<string, JsonElement>? additionalConfigProperties, string? environmentToken, INotifierLog log) {
		ArgumentNullException.ThrowIfNull(log);

		if ((additionalConfigProperties == null) || !additionalConfigProperties.TryGetValue(ConfigKey, out JsonElement block)) {
			log.Info($"{ConfigKey} is not configured in ASF.json, the plugin stays idle");

			return Idle($"{ConfigKey} block not found in ASF.json");
		}

		if (block.ValueKind != JsonValueKind.Object) {
			log.Error($"{ConfigKey} in ASF.json must be a JSON object, the plugin stays idle");

			return Idle($"{ConfigKey} in ASF.json is not a JSON object");
		}

		string? homeserver = null;
		string? roomId = null;
		string? jsonToken = null;

		if (block.TryGetProperty("Matrix", out JsonElement matrix) && (matrix.ValueKind == JsonValueKind.Object)) {
			homeserver = ReadString(matrix, "HomeserverUrl")?.TrimEnd('/');
			roomId = ReadString(matrix, "RoomId");
			jsonToken = ReadString(matrix, "AccessToken");
		}

		bool environmentWins = !string.IsNullOrWhiteSpace(environmentToken);

		// IPC serves ASF.json whatever the plugin does with it, so warn before any early return
		if (jsonToken != null) {
			log.Warning(environmentWins
				? "ASF.json still contains Matrix.AccessToken; it is ignored but still served by GET /Api/ASF; delete it"
				: $"The Matrix access token in ASF.json is exposed through ASF's IPC (GET /Api/ASF and ASF-ui); move it to the environment variable {EnvironmentTokenVariable}");
		}

		if (!ReadBool(block, "Enabled", true, log)) {
			log.Info($"{ConfigKey} is disabled in ASF.json, the plugin stays idle");

			return Idle("Enabled is false", false);
		}

		string? token;

		if (environmentWins) {
			token = environmentToken?.Trim();
			log.Info($"Matrix access token taken from the environment variable {EnvironmentTokenVariable}");
		} else if (jsonToken != null) {
			token = jsonToken;
			log.Info("Matrix access token taken from ASF.json");
		} else {
			token = null;
		}

		List<string> missing = [];

		if (homeserver == null) {
			missing.Add("Matrix.HomeserverUrl");
		}

		if (roomId == null) {
			missing.Add("Matrix.RoomId");
		}

		if (token == null) {
			missing.Add($"Matrix.AccessToken (or the environment variable {EnvironmentTokenVariable})");
		}

		string? reason = missing.Count > 0 ? $"missing {string.Join(", ", missing)}" : null;

		if (homeserver != null) {
			if (!Uri.TryCreate(homeserver, UriKind.Absolute, out Uri? uri) || ((uri.Scheme != Uri.UriSchemeHttps) && (uri.Scheme != Uri.UriSchemeHttp)) || (uri.Query.Length > 0) || (uri.Fragment.Length > 0) || (uri.UserInfo.Length > 0)) {
				reason = Append(reason, "invalid Matrix.HomeserverUrl (expected an http or https URL without credentials, query or fragment)");
			} else if (uri.Scheme == Uri.UriSchemeHttp) {
				log.Warning("Matrix.HomeserverUrl uses http://, the access token travels unencrypted; use https:// unless the homeserver is on a trusted LAN");
			}
		}

		if ((roomId != null) && !roomId.StartsWith('!')) {
			reason = Append(reason, "invalid Matrix.RoomId (expected a room ID starting with '!', room aliases are not supported)");
		}

		if ((token != null) && token.Any(static c => char.IsControl(c) || char.IsWhiteSpace(c))) {
			reason = Append(reason, "invalid Matrix.AccessToken (must not contain whitespace or control characters)");
		}

		if (reason != null) {
			log.Error($"{ConfigKey} stays idle: {reason}");
		}

		return new NotifierSettings {
			AccessToken = token,
			DisconnectedNotifyAfterMinutes = ReadMinutes(block, log),
			Events = ReadEvents(block, log),
			HomeserverUrl = homeserver,
			NotConfiguredReason = reason,
			RoomId = roomId
		};
	}

	private static string Append(string? reason, string problem) => reason == null ? problem : $"{reason}; {problem}";

	private static bool ReadBool(JsonElement parent, string key, bool defaultValue, INotifierLog log) {
		if (!parent.TryGetProperty(key, out JsonElement value)) {
			return defaultValue;
		}

		if (value.ValueKind is JsonValueKind.True or JsonValueKind.False) {
			return value.GetBoolean();
		}

		log.Warning($"{ConfigKey}.{key} must be true or false, using {defaultValue}");

		return defaultValue;
	}

	private static Dictionary<NotificationKind, bool> ReadEvents(JsonElement block, INotifierLog log) {
		Dictionary<NotificationKind, bool> events = [];

		if (!block.TryGetProperty(nameof(Events), out JsonElement map)) {
			return events;
		}

		if (map.ValueKind != JsonValueKind.Object) {
			log.Warning($"{ConfigKey}.Events must be an object, ignoring it");

			return events;
		}

		foreach (JsonProperty property in map.EnumerateObject()) {
			if (!ConfigurableKinds.TryGetValue(property.Name, out NotificationKind kind)) {
				log.Warning($"{ConfigKey}.Events.{property.Name} is not a known event, ignoring it");

				continue;
			}

			if (property.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) {
				log.Warning($"{ConfigKey}.Events.{property.Name} must be true or false, ignoring it");

				continue;
			}

			events[kind] = property.Value.GetBoolean();
		}

		return events;
	}

	private static int ReadMinutes(JsonElement block, INotifierLog log) {
		if (!block.TryGetProperty(nameof(DisconnectedNotifyAfterMinutes), out JsonElement value)) {
			return DefaultDisconnectedNotifyAfterMinutes;
		}

		if ((value.ValueKind == JsonValueKind.Number) && value.TryGetInt32(out int minutes) && (minutes is >= 1 and <= 1440)) {
			return minutes;
		}

		log.Warning($"{ConfigKey}.DisconnectedNotifyAfterMinutes must be a whole number from 1 to 1440, using {DefaultDisconnectedNotifyAfterMinutes}");

		return DefaultDisconnectedNotifyAfterMinutes;
	}

	private static string? ReadString(JsonElement parent, string key) {
		if (!parent.TryGetProperty(key, out JsonElement value) || (value.ValueKind != JsonValueKind.String)) {
			return null;
		}

		string? text = value.GetString()?.Trim();

		return string.IsNullOrEmpty(text) ? null : text;
	}
}
