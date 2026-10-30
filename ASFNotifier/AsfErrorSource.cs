using System;

namespace ASFNotifier;

// An Error or Fatal entry as the NLog tap saw it. The exception is read off the logging thread: its type may be sent, its
// text is only checked for the token
internal sealed record LoggedError(string LoggerName, string Message, DateTimeOffset At, Exception? Exception = null);

// Spec section 5.6: turns an ASF error entry into an AsfError notification, or null when it must not be forwarded
internal static class AsfErrorSource {
	// ASF's own logger (SharedInfo.ASF); a bot logs under its bot name
	internal const string AsfLoggerName = "ASF";

	internal const int MaxTextLength = 300;

	// Every Error the plugin writes carries this method name, so the plugin's own entries never loop back
	internal const string SelfMethod = nameof(ASFNotifier);

	// stopReported: whether BotStoppedByAsf is on for that bot, so its give-up errors are not reported twice
	internal static Notification? ToNotification(LoggedError entry, string accessToken, Func<string, bool> isBot, Func<string, bool> stopReported) {
		ArgumentNullException.ThrowIfNull(entry);
		ArgumentException.ThrowIfNullOrEmpty(accessToken);
		ArgumentNullException.ThrowIfNull(isBot);
		ArgumentNullException.ThrowIfNull(stopReported);

		// ArchiLogger writes "{method}() {message}", or "{method}()" when it logs an exception
		int end = entry.Message.IndexOf("()", StringComparison.Ordinal);

		if (end <= 0) {
			return null;
		}

		string method = entry.Message[..end];

		if ((method == SelfMethod) || !IsMethodName(method)) {
			return null;
		}

		// The whole entry, exception included, so a token anywhere in it keeps all of it out of the room. An exception
		// whose text cannot be read cannot be checked, so its entry stays out too
		string? exceptionText;

		try {
			exceptionText = entry.Exception?.ToString();
		} catch (Exception) {
			return null;
		}

		if (entry.Message.Contains(accessToken, StringComparison.Ordinal) || (exceptionText?.Contains(accessToken, StringComparison.Ordinal) == true)) {
			return null;
		}

		string? botName;

		if (entry.LoggerName == AsfLoggerName) {
			botName = null;
		} else if (isBot(entry.LoggerName)) {
			botName = entry.LoggerName;
		} else {
			// Libraries and ASF's web host log under other names and not in ArchiLogger's shape
			return null;
		}

		// The error behind a BotStoppedByAsf message is that message already
		if ((botName != null) && GiveUpLogTap.GiveUpMethods.Contains(method) && stopReported(botName)) {
			return null;
		}

		string text = FirstLine(entry.Message[(end + 2)..].Trim());

		// An exception entry has no text of its own; its type says enough, its message may hold anything
		if ((text.Length == 0) && (entry.Exception != null)) {
			text = entry.Exception.GetType().Name;
		}

		return new Notification(NotificationKind.AsfError, botName, entry.At, Method: method, Text: Trim(text));
	}

	private static string FirstLine(string text) {
		for (int i = 0; i < text.Length; i++) {
			if (text[i] is '\r' or '\n') {
				return text[..i].TrimEnd();
			}
		}

		return text;
	}

	// ASF passes [CallerMemberName]: an identifier, ".ctor" at most; anything else is not ArchiLogger's shape
	private static bool IsMethodName(string method) {
		foreach (char c in method) {
			if (!char.IsLetterOrDigit(c) && (c is not ('_' or '.' or '<' or '>'))) {
				return false;
			}
		}

		return true;
	}

	// At most MaxTextLength characters with the ellipsis, never half a surrogate pair
	private static string Trim(string text) {
		if (text.Length <= MaxTextLength) {
			return text;
		}

		int cut = MaxTextLength - 1;

		if (char.IsHighSurrogate(text[cut - 1])) {
			cut--;
		}

		return text[..cut] + "…";
	}
}
