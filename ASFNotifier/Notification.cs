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
	int? Count = null
);

internal sealed record FormattedMessage(string PlainText, string Html);
