using System;
using System.Net;

namespace ASFNotifier;

internal static class Formatter {
	internal static FormattedMessage Format(Notification notification) {
		ArgumentNullException.ThrowIfNull(notification);

		string bot = notification.BotName ?? "";
		string boldBot = $"<b>{WebUtility.HtmlEncode(bot)}</b>";

		// The other kinds arrive with their event tickets
		return notification.Kind switch {
			NotificationKind.BotStoppedByAsf when notification.RequiredInput is { } input => new FormattedMessage($"🔴 {bot} — ASF stopped the bot, it needs {input} input", $"🔴 {boldBot} — ASF stopped the bot, it needs {Code(input)} input"),
			NotificationKind.BotStoppedByAsf when notification is { Reason: { } reason, LoginFailed: true } => new FormattedMessage($"🔴 {bot} — login failed ({reason}), ASF stopped the bot", $"🔴 {boldBot} — login failed ({Code(reason)}), ASF stopped the bot"),
			NotificationKind.BotStoppedByAsf when notification.Reason is { } reason => new FormattedMessage($"🔴 {bot} — ASF stopped the bot ({reason})", $"🔴 {boldBot} — ASF stopped the bot ({Code(reason)})"),
			NotificationKind.BotStoppedByAsf => new FormattedMessage($"🔴 {bot} — ASF stopped the bot", $"🔴 {boldBot} — ASF stopped the bot"),
			NotificationKind.BotStopped => new FormattedMessage($"⏹️ {bot} — bot stopped", $"⏹️ {boldBot} — bot stopped"),
			NotificationKind.BotDisconnected when notification.Reason is { } reason => new FormattedMessage($"🟠 {bot} — disconnected for {Duration(notification.Duration)} ({reason})", $"🟠 {boldBot} — disconnected for {Duration(notification.Duration)} ({Code(reason)})"),
			NotificationKind.BotDisconnected => new FormattedMessage($"🟠 {bot} — disconnected for {Duration(notification.Duration)}", $"🟠 {boldBot} — disconnected for {Duration(notification.Duration)}"),
			NotificationKind.BotReconnected => new FormattedMessage($"🟢 {bot} — reconnected after {Duration(notification.Duration)}", $"🟢 {boldBot} — reconnected after {Duration(notification.Duration)}"),
			NotificationKind.Test => new FormattedMessage($"🧪 ASF-Notifier test from {bot} — delivery works", $"🧪 <b>ASF-Notifier</b> test from {boldBot} — delivery works"),
			_ => throw new NotSupportedException(notification.Kind.ToString())
		};
	}

	// Spec section 8: whole minutes below 2 hours, then hours and minutes. Rounded, so a timer firing a few milliseconds
	// early still reads as the configured threshold
	internal static string Duration(TimeSpan? duration) {
		long minutes = (long) Math.Round((duration ?? TimeSpan.Zero).TotalMinutes, MidpointRounding.AwayFromZero);

		return minutes switch {
			1 => "1 minute",
			< 120 => $"{minutes} minutes",
			_ => $"{minutes / 60} h {minutes % 60} min"
		};
	}

	private static string Code(string value) => $"<code>{WebUtility.HtmlEncode(value)}</code>";
}
