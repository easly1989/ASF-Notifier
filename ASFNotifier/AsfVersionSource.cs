using System;
using ArchiSteamFarm.Core;

namespace ASFNotifier;

internal enum AsfVersionAction : byte {
	None, // same version
	Store, // nothing usable stored: record the running version, no message
	Updated, // stored is older: message, then record
	Downgrade // stored is newer: log, record, no message
}

internal sealed record AsfVersionDecision(AsfVersionAction Action, Version? Stored = null);

// Spec section 5.5: pure decision core, the adapter in ASFNotifier only reads and writes ASF.GlobalDatabase
internal static class AsfVersionSource {
	internal const string StorageKey = "ASFNotifier.LastAsfVersion";

	// The ASF assembly, not the plugin's
	internal static Version RunningVersion => typeof(ASF).Assembly.GetName().Version ?? throw new InvalidOperationException(nameof(RunningVersion));

	internal static AsfVersionDecision Decide(string? stored, Version current) {
		ArgumentNullException.ThrowIfNull(current);

		if (!Version.TryParse(stored, out Version? parsed)) {
			return new AsfVersionDecision(AsfVersionAction.Store);
		}

		// "6.3.10" and "6.3.10.0" are the same version
		parsed = new Version(parsed.Major, parsed.Minor, Math.Max(parsed.Build, 0), Math.Max(parsed.Revision, 0));

		return parsed.CompareTo(current) switch {
			< 0 => new AsfVersionDecision(AsfVersionAction.Updated, parsed),
			> 0 => new AsfVersionDecision(AsfVersionAction.Downgrade, parsed),
			_ => new AsfVersionDecision(AsfVersionAction.None)
		};
	}

	internal static Notification Updated(Version from, Version to, DateTimeOffset now) => new(NotificationKind.AsfUpdated, null, now, FromVersion: from.ToString(), ToVersion: to.ToString());
}
