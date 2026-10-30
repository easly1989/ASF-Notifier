using System;
using System.Collections.Generic;
using System.Linq;

namespace ASFNotifier;

// The ASF access a Matrix user can be given. Owner is never granted from Matrix (spec section 9.1)
internal enum CommandAccess : byte {
	FamilySharing,
	Operator,
	Master
}

// Spec section 9.1: who may run ASF commands from the room. Read from the environment only, never from ASF.json, which
// ASF's IPC serves verbatim
internal static class CommandAllowList {
	internal const string EnvironmentVariable = "ASF_NOTIFIER_COMMAND_USERS";

	private const int MaxUserIdLength = 255;

	// Comma-separated "@user:server=Level" entries. A bad entry is refused with an error and the others still apply;
	// an empty result keeps commands from Matrix off
	internal static IReadOnlyDictionary<string, CommandAccess> Parse(string? value, INotifierLog log) {
		ArgumentNullException.ThrowIfNull(log);

		Dictionary<string, CommandAccess> users = new(StringComparer.Ordinal);

		if (string.IsNullOrWhiteSpace(value)) {
			return users;
		}

		string[] entries = value.Split(',');

		for (int i = 0; i < entries.Length; i++) {
			string entry = entries[i].Trim();

			if (entry.Length == 0) {
				continue;
			}

			// The entry text is never logged: a token pasted into the wrong variable must not reach the log
			string where = $"{EnvironmentVariable} entry {i + 1}";

			// A localpart may contain '=', a level never does
			int separator = entry.LastIndexOf('=');
			string userId = separator > 0 ? entry[..separator].Trim() : "";
			string level = separator > 0 ? entry[(separator + 1)..].Trim() : "";

			if (!IsUserId(userId)) {
				log.Error($"{where} is not \"@user:server=Level\", ignoring it");

				continue;
			}

			if (string.Equals(level, "Owner", StringComparison.OrdinalIgnoreCase)) {
				log.Error($"{where} ({userId}): Owner cannot be granted from Matrix, the highest level is Master; ignoring it");

				continue;
			}

			if (ParseLevel(level) is not { } access) {
				log.Error($"{where} ({userId}): unknown access level, expected FamilySharing, Operator or Master; ignoring it");

				continue;
			}

			if (!users.TryAdd(userId, access)) {
				log.Error($"{where} ({userId}) repeats a user, ignoring it");
			}
		}

		if (users.Count > 0) {
			log.Info($"Commands from Matrix are allowed for {string.Join(", ", users.Select(static user => $"{user.Key} ({user.Value})"))}");
		}

		return users;
	}

	// The grammar's outline only (@localpart:server); the homeserver checks the rest
	private static bool IsUserId(string text) {
		int colon = text.IndexOf(':', StringComparison.Ordinal);

		return (text.Length <= MaxUserIdLength) && text.StartsWith('@') && (colon > 1) && (colon < (text.Length - 1)) && !text.Any(static c => char.IsWhiteSpace(c) || char.IsControl(c));
	}

	private static CommandAccess? ParseLevel(string level) {
		foreach (CommandAccess access in Enum.GetValues<CommandAccess>()) {
			if (string.Equals(level, access.ToString(), StringComparison.OrdinalIgnoreCase)) {
				return access;
			}
		}

		return null;
	}
}
