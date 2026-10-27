using System;
using System.Net;

namespace ASFNotifier;

internal static class Formatter {
	internal static FormattedMessage Format(Notification notification) {
		ArgumentNullException.ThrowIfNull(notification);

		// The other kinds arrive with their event tickets
		return notification.Kind switch {
			NotificationKind.Test => new FormattedMessage($"🧪 ASF-Notifier test from {notification.BotName} — delivery works", $"🧪 <b>ASF-Notifier</b> test from <b>{WebUtility.HtmlEncode(notification.BotName)}</b> — delivery works"),
			_ => throw new NotSupportedException(notification.Kind.ToString())
		};
	}
}
