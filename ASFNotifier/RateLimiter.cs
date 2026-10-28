using System;
using System.Collections.Generic;
using System.Threading;

namespace ASFNotifier;

// Spec section 4.5: after the filter (and, for stops, the stop hold), before the queue. Test never comes here.
// Lock order: Lock is held while calling enqueue, so the summary lands right before its message; the queue never
// calls back into the limiter, and the limiter never calls the watcher or the stop hold.
internal sealed class RateLimiter(TimeProvider timeProvider) {
	internal const int HourlyCap = 20;
	internal static readonly TimeSpan CapWindow = TimeSpan.FromHours(1);
	internal static readonly TimeSpan DedupeWindow = TimeSpan.FromMinutes(10);

	// Windows and slots use monotonic timestamps, so a wall clock stepping back mutes nothing
	private readonly Dictionary<(NotificationKind Kind, string? BotName), long> LastSent = [];
	private readonly Lock Lock = new();

	// Bots whose BotDisconnected went out and has no BotReconnected or stop yet
	private readonly HashSet<string> OpenOutages = new(StringComparer.Ordinal);

	// When each slot of the rolling hour was taken, oldest first
	private readonly Queue<long> Slots = new();

	private int SuppressedCount;

	// true when the notification passed and enqueue accepted it; a pass with drops before it enqueues the Suppressed
	// summary first
	internal bool Pass(Notification notification, Func<Notification, bool> enqueue) {
		ArgumentNullException.ThrowIfNull(notification);
		ArgumentNullException.ThrowIfNull(enqueue);

		lock (Lock) {
			long now = timeProvider.GetTimestamp();

			while ((Slots.Count > 0) && (timeProvider.GetElapsedTime(Slots.Peek(), now) >= CapWindow)) {
				Slots.Dequeue();
			}

			(NotificationKind, string?) key = (notification.Kind, notification.BotName);
			bool closesOutage = (notification is { Kind: NotificationKind.BotReconnected, BotName: { } bot }) && OpenOutages.Contains(bot);
			bool deduped = !closesOutage && LastSent.TryGetValue(key, out long last) && (timeProvider.GetElapsedTime(last, now) < DedupeWindow);

			// The messages that must never be lost; they still take a slot when there is one
			bool exempt = notification.Kind is NotificationKind.BotStoppedByAsf or NotificationKind.BotReconnected;

			if (deduped || (!exempt && (Slots.Count >= HourlyCap))) {
				SuppressedCount++;

				return false;
			}

			// enqueue takes the queue's lock and may log a drop under ours: limiter → queue, never the reverse
			if ((SuppressedCount > 0) && enqueue(new Notification(NotificationKind.Suppressed, null, timeProvider.GetUtcNow(), Count: SuppressedCount))) {
				// Rides along the message that passes, so it may be the one message above the cap in that hour
				TakeSlot(now);
				SuppressedCount = 0;
			}

			if (!enqueue(notification)) {
				return false;
			}

			TakeSlot(now);
			LastSent[key] = now;

			if (notification.BotName is { } name) {
				switch (notification.Kind) {
					case NotificationKind.BotDisconnected:
						OpenOutages.Add(name);

						break;
					case NotificationKind.BotReconnected or NotificationKind.BotStopped or NotificationKind.BotStoppedByAsf:
						OpenOutages.Remove(name);

						break;
				}
			}

			return true;
		}
	}

	// Under Lock
	private void TakeSlot(long now) {
		if (Slots.Count < HourlyCap) {
			Slots.Enqueue(now);
		}
	}
}
