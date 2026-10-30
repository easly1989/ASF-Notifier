using System;

namespace ASFNotifier;

// Spec section 5.6: the glue between the NLog tap and Emit, without ASF types so it is unit-tested. isBot and emit come
// from the adapter (Bot.BotsReadOnly and its Emit: filter, rate limiter, send queue)
internal sealed class AsfErrorForwarder(Func<NotifierSettings?> settings, BotConfigStore botConfigs, Func<string, bool> isBot, Func<Notification, bool> emit, INotifierLog log) {
	private static readonly Notification GlobalProbe = new(NotificationKind.AsfError, null, default);

	// On NLog's logging thread, for each Error and Fatal entry: whether the global setting or a bot can turn AsfError on.
	// No allocation, no lock, never throws
	internal bool Wanted {
		get {
			try {
				return settings() is { IsConfigured: true } current && (Filter.Allows(GlobalProbe, current) || botConfigs.AnyEnables(NotificationKind.AsfError));
			} catch (Exception) {
				return false;
			}
		}
	}

	// On the thread pool. Its own failure is logged as the plugin (AsfLog), which is never forwarded, so it cannot loop
	internal void Forward(LoggedError entry) {
		try {
			if (settings() is not { AccessToken: { } token } current) {
				return;
			}

			Notification? notification = AsfErrorSource.ToNotification(entry, token, isBot, bot => Filter.Allows(new Notification(NotificationKind.BotStoppedByAsf, bot, entry.At), current, botConfigs.Get(bot)));

			if (notification != null) {
				emit(notification);
			}
		} catch (Exception e) {
			log.Error($"Forwarding an ASF error failed ({e.GetType().Name})");
		}
	}
}
