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
	Test,
	CommandReply
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
	string? Text = null, // CommandReply: ASF's answer, already capped (spec section 9.1)
	string? InReplyTo = null, // CommandReply: the event ID of the command
	string? RoomId = null // CommandReply: the command room, when it is not the notification room
);

// RoomId null: the configured room. InReplyTo: sent as a Matrix reply to that event
internal sealed record FormattedMessage(string PlainText, string Html, string? RoomId = null, string? InReplyTo = null);
