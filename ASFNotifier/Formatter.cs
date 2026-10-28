using System;
using System.Text;

namespace ASFNotifier;

internal static class Formatter {
	internal static FormattedMessage Format(Notification notification) {
		ArgumentNullException.ThrowIfNull(notification);

		string bot = notification.BotName ?? "";
		string boldBot = $"<b>{Html(bot)}</b>";

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
			NotificationKind.FarmingFinished => new FormattedMessage($"✅ {bot} — farming finished", $"✅ {boldBot} — farming finished"),
			NotificationKind.FarmingStarted => new FormattedMessage($"▶️ {bot} — farming started", $"▶️ {boldBot} — farming started"),
			NotificationKind.FarmingStopped => new FormattedMessage($"⏸️ {bot} — farming stopped", $"⏸️ {boldBot} — farming stopped"),
			NotificationKind.AsfUpdated => new FormattedMessage($"⬆️ ASF updated {notification.FromVersion} → {notification.ToVersion}", $"⬆️ ASF updated {Code(notification.FromVersion ?? "")} → {Code(notification.ToVersion ?? "")}"),
			NotificationKind.Suppressed => new FormattedMessage($"⚠️ ASF-Notifier — {Suppressed(notification.Count)} (rate limit)", $"⚠️ <b>ASF-Notifier</b> — {Suppressed(notification.Count)} (rate limit)"),
			NotificationKind.Test => new FormattedMessage($"🧪 ASF-Notifier test from {bot} — delivery works", $"🧪 <b>ASF-Notifier</b> test from {boldBot} — delivery works"),
			_ => throw new NotSupportedException(notification.Kind.ToString())
		};
	}

	// Spec section 8: whole minutes below 2 hours, then hours and minutes. Rounded, so a timer firing a few milliseconds
	// early still reads as the configured threshold
	internal static string Duration(TimeSpan? duration) {
		// Half away from zero, by hand: the trimmed ASF has no MidpointRounding (issue #23)
		double total = (duration ?? TimeSpan.Zero).TotalMinutes;
		long minutes = (long) (total < 0 ? -Math.Floor(-total + 0.5) : Math.Floor(total + 0.5));

		return minutes switch {
			1 => "1 minute",
			< 120 => $"{minutes} minutes",
			_ => $"{minutes / 60} h {minutes % 60} min"
		};
	}

	private static string Code(string value) => $"<code>{Html(value)}</code>";

	// The trimmed ASF has no WebUtility (issue #23). Escapes what HTML needs; other characters pass through as they are
	private static string Html(string value) {
		StringBuilder html = new(value.Length);

		foreach (char c in value) {
			string? entity = c switch {
				'&' => "&amp;",
				'<' => "&lt;",
				'>' => "&gt;",
				'"' => "&quot;",
				'\'' => "&#39;",
				_ => null
			};

			if (entity == null) {
				html.Append(c);
			} else {
				html.Append(entity);
			}
		}

		return html.ToString();
	}

	private static string Suppressed(int? count) => count == 1 ? "1 notification suppressed" : $"{count} notifications suppressed";
}
