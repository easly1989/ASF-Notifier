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
	AsfError,
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
	bool LoginFailed = false, // the reason came from the login result (spec section 8)
	bool AsfGaveUp = false, // a give-up error backs this stop, so a mass stop does not drop it (spec section 5.3)
	string? Method = null, // AsfError: the ASF method that logged the error, part of the dedupe key (spec section 5.6)
	string? Text = null // AsfError: the first line of the entry, trimmed
);

internal sealed record FormattedMessage(string PlainText, string Html);
