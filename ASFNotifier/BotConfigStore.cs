using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text.Json;

namespace ASFNotifier;

// Hooks run on arbitrary threads, hence the concurrent map; keyed by bot name
internal sealed class BotConfigStore {
	private readonly ConcurrentDictionary<string, BotSettings> Settings = new(StringComparer.Ordinal);

	internal BotSettings? Get(string? botName) => botName == null ? null : Settings.GetValueOrDefault(botName);

	// Whether an enabled bot turns this event on. Called on NLog's logging thread: the enumeration takes no lock
	internal bool AnyEnables(NotificationKind kind) {
		foreach (KeyValuePair<string, BotSettings> bot in Settings) {
			if (bot.Value.Enabled && bot.Value.Events.TryGetValue(kind, out bool enabled) && enabled) {
				return true;
			}
		}

		return false;
	}

	internal void Remove(string botName) => Settings.TryRemove(botName, out _);

	// A reload replaces the old values whole; no usable block means inheriting everything
	internal void Replace(string botName, IReadOnlyDictionary<string, JsonElement>? additionalConfigProperties, INotifierLog log) {
		if (BotSettings.Parse(botName, additionalConfigProperties, log) is { } parsed) {
			Settings[botName] = parsed;
		} else {
			Remove(botName);
		}
	}
}
