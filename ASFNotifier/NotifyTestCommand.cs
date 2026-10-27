using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

namespace ASFNotifier;

internal enum NotifyTestAction : byte {
	NotOurs,
	Denied,
	Cooldown,
	NotConfigured,
	Send
}

// Pure decision for !notifytest; ASF types end in the adapter, which turns EAccess into one bool
internal readonly record struct NotifyTestDecision(NotifyTestAction Action, int WaitSeconds = 0) {
	internal const string CommandName = "notifytest";
	internal static readonly TimeSpan Cooldown = TimeSpan.FromSeconds(10);

	internal static NotifyTestDecision Decide(string? commandName, bool isMasterOrHigher, DateTimeOffset? lastTestAt, DateTimeOffset now, string? notConfiguredReason) {
		if (!string.Equals(commandName, CommandName, StringComparison.OrdinalIgnoreCase)) {
			return new NotifyTestDecision(NotifyTestAction.NotOurs);
		}

		if (!isMasterOrHigher) {
			return new NotifyTestDecision(NotifyTestAction.Denied);
		}

		if ((lastTestAt != null) && ((now - lastTestAt.Value) < Cooldown)) {
			return new NotifyTestDecision(NotifyTestAction.Cooldown, (int) Math.Ceiling((Cooldown - (now - lastTestAt.Value)).TotalSeconds));
		}

		return new NotifyTestDecision(notConfiguredReason == null ? NotifyTestAction.Send : NotifyTestAction.NotConfigured);
	}
}

internal sealed class NotifyTestCommand(NotifierSettings settings, INotificationChannel? channel, TimeProvider timeProvider, TimeSpan? sendTimeout) {
	private static readonly TimeSpan DefaultSendTimeout = TimeSpan.FromSeconds(15);

	private readonly ConcurrentDictionary<string, DateTimeOffset> LastTestByBot = new(StringComparer.Ordinal);

	// Returns null when the command is not ours or the caller may not know it exists
	internal async Task<string?> HandleAsync(string botName, string? commandName, bool isMasterOrHigher, CancellationToken cancellationToken) {
		DateTimeOffset now = timeProvider.GetUtcNow();
		DateTimeOffset? last = LastTestByBot.TryGetValue(botName, out DateTimeOffset previous) ? previous : null;
		string? notConfigured = channel == null ? (settings.NotConfiguredReason ?? "no channel") : null;

		NotifyTestDecision decision = NotifyTestDecision.Decide(commandName, isMasterOrHigher, last, now, notConfigured);

		switch (decision.Action) {
			case NotifyTestAction.Cooldown:
				return $"<{botName}> Please wait {decision.WaitSeconds.ToString(CultureInfo.InvariantCulture)} s before the next test.";
			case NotifyTestAction.NotConfigured:
				return $"<{botName}> ASF-Notifier is not configured: {notConfigured}.";
			case NotifyTestAction.Send when channel != null:
				LastTestByBot[botName] = now;

				return await Send(botName, channel, now, cancellationToken).ConfigureAwait(false);
			default:
				return null;
		}
	}

	private async Task<string> Send(string botName, INotificationChannel target, DateTimeOffset now, CancellationToken cancellationToken) {
		using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		timeout.CancelAfter(sendTimeout ?? DefaultSendTimeout);

		FormattedMessage message = Formatter.Format(new Notification(NotificationKind.Test, botName, now));

		try {
			SendResult result = await target.SendAsync(message, Guid.NewGuid().ToString("N"), true, timeout.Token).ConfigureAwait(false);

			return result.Outcome == SendOutcome.Sent ? $"<{botName}> Test notification sent to Matrix." : $"<{botName}> Test notification failed: {result.Error ?? "unknown error"}";
		} catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) {
			return $"<{botName}> Test notification failed: timeout";
		}
	}
}
