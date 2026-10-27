using System;

namespace ASFNotifier;

internal enum NotificationKind : byte {
	BotStoppedByAsf,
	BotStopped,
	BotDisconnected,
	BotReconnected,
	FarmingFinished,
	FarmingStarted,
	FarmingStopped,
	AsfUpdated,
	Suppressed,
	Test
}

internal sealed record Notification(
	NotificationKind Kind,
	string? BotName,
	DateTimeOffset OccurredAt,
	string? Reason = null,
	string? RequiredInput = null,
	TimeSpan? Duration = null,
	string? FromVersion = null,
	string? ToVersion = null,
	int? Count = null,
	bool LoginFailed = false // the reason came from the login result (spec section 8)
);

internal sealed record FormattedMessage(string PlainText, string Html);
