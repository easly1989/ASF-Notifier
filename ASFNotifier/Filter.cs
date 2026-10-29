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
		[NotificationKind.AsfError] = false,
		[NotificationKind.BotStopped] = false,
		[NotificationKind.FarmingStarted] = false,
		[NotificationKind.FarmingStopped] = false
	};

	internal static bool Allows(Notification notification, NotifierSettings settings, BotSettings? bot = null) {
		ArgumentNullException.ThrowIfNull(notification);
		ArgumentNullException.ThrowIfNull(settings);

		if (!settings.Enabled) {
			return false;
		}

		// Notifications without a bot (AsfUpdated, Suppressed) never see the per-bot layer
		if ((bot != null) && (notification.BotName != null)) {
			if (!bot.Enabled) {
				return false;
			}

			if (bot.Events.TryGetValue(notification.Kind, out bool overridden)) {
				return overridden;
			}
		}

		if (settings.Events.TryGetValue(notification.Kind, out bool configured)) {
			return configured;
		}

		// Suppressed and Test are not configurable
		return !Defaults.TryGetValue(notification.Kind, out bool enabledByDefault) || enabledByDefault;
	}
}
