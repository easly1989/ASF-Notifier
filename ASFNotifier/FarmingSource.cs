using System;

namespace ASFNotifier;

// Spec section 5.4: maps the IBotCardsFarmerInfo callbacks to notifications; pure, so it is testable without a Bot
internal static class FarmingSource {
	internal static Notification? OnFinished(string botName, bool farmedSomething, DateTimeOffset now) => farmedSomething ? new Notification(NotificationKind.FarmingFinished, botName, now) : null;

	internal static Notification OnStarted(string botName, DateTimeOffset now) => new(NotificationKind.FarmingStarted, botName, now);

	internal static Notification OnStopped(string botName, DateTimeOffset now) => new(NotificationKind.FarmingStopped, botName, now);
}
