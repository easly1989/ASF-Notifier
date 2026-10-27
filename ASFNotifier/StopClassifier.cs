using System;
using SteamKit2;

namespace ASFNotifier;

// What the log tap keeps of one ASF log entry: never its text
internal sealed record TapEntry(DateTimeOffset At, string Method, EResult? Reason);

// Spec section 5.3: tells a bot ASF gave up on from any other stop, since the hook alone cannot (fact 3)
internal static class StopClassifier {
	internal const string HandleLoginResult = nameof(HandleLoginResult);
	internal static readonly TimeSpan Window = TimeSpan.FromSeconds(60);

	// t is the time of the disconnect that revealed the stop, never the time of classifying it.
	// Step 2 (report once) is the watcher's: a classified stop ends the bot's outage.
	internal static Notification? Classify(string botName, DateTimeOffset t, bool shuttingDown, string? requiredInput, EResult hookReason, TapEntry? giveUpError, TapEntry? reasonEntry) {
		if (shuttingDown) {
			return null;
		}

		if (requiredInput != null) {
			return new Notification(NotificationKind.BotStoppedByAsf, botName, t, RequiredInput: requiredInput);
		}

		if ((giveUpError == null) || !IsNear(giveUpError, t)) {
			return new Notification(NotificationKind.BotStopped, botName, t);
		}

		if (IsReason(hookReason)) {
			return new Notification(NotificationKind.BotStoppedByAsf, botName, t, hookReason.ToString(), LoginFailed: true);
		}

		if (giveUpError.Reason is { } fromError) {
			return new Notification(NotificationKind.BotStoppedByAsf, botName, t, fromError.ToString(), LoginFailed: giveUpError.Method == HandleLoginResult);
		}

		if (reasonEntry is { Reason: { } fromEntry } && IsNear(reasonEntry, t)) {
			return new Notification(NotificationKind.BotStoppedByAsf, botName, t, fromEntry.ToString(), LoginFailed: reasonEntry.Method == HandleLoginResult);
		}

		return new Notification(NotificationKind.BotStoppedByAsf, botName, t);
	}

	internal static bool IsReason(EResult result) => result is not (EResult.OK or EResult.Invalid) && Enum.IsDefined(result);

	// The first token that names an EResult; ASF formats EResult by name in every language, so this needs no localisation
	internal static EResult? ParseReason(string message) {
		ArgumentNullException.ThrowIfNull(message);

		int start = -1;

		for (int i = 0; i <= message.Length; i++) {
			if ((i < message.Length) && char.IsLetterOrDigit(message[i])) {
				if (start < 0) {
					start = i;
				}

				continue;
			}

			if (start < 0) {
				continue;
			}

			ReadOnlySpan<char> token = message.AsSpan(start, i - start);
			start = -1;

			// Enum.TryParse also accepts numbers, which would turn any count in the text into a reason
			if (!char.IsDigit(token[0]) && Enum.TryParse(token, false, out EResult result) && IsReason(result)) {
				return result;
			}
		}

		return null;
	}

	private static bool IsNear(TapEntry entry, DateTimeOffset t) => (entry.At - t).Duration() <= Window;
}
