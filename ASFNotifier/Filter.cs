using System;
using System.Collections.Generic;

namespace ASFNotifier;

internal static class Filter {
	// Spec section 6
	private static readonly Dictionary<NotificationKind, bool> Defaults = new() {
		[NotificationKind.BotStoppedByAsf] = true,
		[NotificationKind.BotDisconnected] = true,
		[NotificationKind.BotReconnected] = true,
		[NotificationKind.FarmingFinished] = true,
		[NotificationKind.AsfUpdated] = true,
		[NotificationKind.BotStopped] = false,
		[NotificationKind.FarmingStarted] = false,
		[NotificationKind.FarmingStopped] = false
	};

	internal static bool Allows(Notification notification, NotifierSettings settings) {
		ArgumentNullException.ThrowIfNull(notification);
		ArgumentNullException.ThrowIfNull(settings);

		if (!settings.Enabled) {
			return false;
		}

		if (settings.Events.TryGetValue(notification.Kind, out bool configured)) {
			return configured;
		}

		// Suppressed and Test are not configurable
		return !Defaults.TryGetValue(notification.Kind, out bool enabledByDefault) || enabledByDefault;
	}
}
