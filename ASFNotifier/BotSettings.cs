using System;
using System.Collections.Generic;
using System.Text.Json;

namespace ASFNotifier;

// The ASFNotifier object of one config/<bot>.json (spec section 7.2); the room stays global
internal sealed class BotSettings {
	private const string ConfigKey = "ASFNotifier";

	internal bool Enabled { get; private init; } = true;
	internal IReadOnlyDictionary<NotificationKind, bool> Events { get; private init; } = new Dictionary<NotificationKind, bool>();

	// null: the bot has no usable block and inherits the global settings
	internal static BotSettings? Parse(string botName, IReadOnlyDictionary<string, JsonElement>? additionalConfigProperties, INotifierLog log) {
		ArgumentNullException.ThrowIfNull(botName);
		ArgumentNullException.ThrowIfNull(log);

		if ((additionalConfigProperties == null) || !additionalConfigProperties.TryGetValue(ConfigKey, out JsonElement block)) {
			return null;
		}

		PrefixedLog prefixed = new($"Bot {botName}: ", log);

		if (block.ValueKind != JsonValueKind.Object) {
			prefixed.Warning($"{ConfigKey} must be a JSON object, inheriting the global settings");

			return null;
		}

		foreach (JsonProperty property in block.EnumerateObject()) {
			if (property.Name is not (nameof(Enabled) or nameof(Events))) {
				prefixed.Warning($"{ConfigKey}.{property.Name} is not a known per-bot key, ignoring it");
			}
		}

		return new BotSettings {
			Enabled = NotifierSettings.ReadBool(block, nameof(Enabled), true, prefixed),
			Events = NotifierSettings.ReadEvents(block, prefixed, true)
		};
	}

	private sealed class PrefixedLog(string prefix, INotifierLog inner) : INotifierLog {
		public void Error(string message) => inner.Error(prefix + message);
		public void Info(string message) => inner.Info(prefix + message);
		public void Warning(string message) => inner.Warning(prefix + message);
	}
}
