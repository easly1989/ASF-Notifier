using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text.Json;

namespace ASFNotifier;

// Hooks run on arbitrary threads, hence the concurrent map; keyed by bot name
internal sealed class BotConfigStore {
	private readonly ConcurrentDictionary<string, BotSettings> Settings = new(StringComparer.Ordinal);

	internal BotSettings? Get(string? botName) => botName == null ? null : Settings.GetValueOrDefault(botName);

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
