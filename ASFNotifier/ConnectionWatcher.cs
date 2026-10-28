using System;
using System.Collections.Generic;
using System.Threading;
using SteamKit2;

namespace ASFNotifier;

// What the disconnect timer reads of a bot when it fires
internal readonly record struct BotStatus(bool KeepRunning, bool IsLoggedOn, string? RequiredInput);

// The plugin side of the disconnect timer, called on a timer thread
internal interface IWatcherHost {
	// true when the notification was accepted for sending: it passed the filter, dedupe and the hourly cap and was
	// queued, or it is a stop the stop hold took (rate-checked on release). BotReconnected follows only an accepted
	// BotDisconnected.
	bool Emit(Notification notification);

	// null when ASF no longer has the bot
	BotStatus? GetStatus(string botName);

	void OnError(Exception exception);
}

// Spec sections 5.1 to 5.3: per-bot connection state, fed by the IBotConnection hooks. Hooks get back what to send; the
// disconnect timer hands its result to the host. Never sends.
internal sealed class ConnectionWatcher : IDisposable {
	private readonly IWatcherHost Host;
	private readonly Func<bool> IsShuttingDown;

	// Lock order: Lock may be held while taking the tap's lock and, through Host.Emit, the filter, the stop hold, the rate
	// limiter and the send queue. None of them calls back into the watcher, so they never take Lock while holding their own.
	private readonly Lock Lock = new();
	private readonly TimeSpan NotifyAfter;

	// Bots in an outage
	private readonly Dictionary<string, Outage> Outages = new(StringComparer.Ordinal);

	private readonly IGiveUpLog Tap;
	private readonly TimeProvider TimeProvider;

	internal ConnectionWatcher(IGiveUpLog tap, Func<bool> isShuttingDown, TimeProvider timeProvider, TimeSpan notifyAfter, IWatcherHost host) {
		Tap = tap;
		IsShuttingDown = isShuttingDown;
		TimeProvider = timeProvider;
		NotifyAfter = notifyAfter;
		Host = host;
	}

	public void Dispose() {
		lock (Lock) {
			foreach (Outage outage in Outages.Values) {
				outage.Timer?.Dispose();
			}

			Outages.Clear();
		}
	}

	internal bool IsTracked(string botName) {
		lock (Lock) {
			return Outages.ContainsKey(botName);
		}
	}

	internal void OnDestroy(string botName) {
		lock (Lock) {
			EndOutage(botName);
		}
	}

	internal Notification? OnDisconnected(string botName, EResult reason, bool keepRunning, string? requiredInput) {
		DateTimeOffset now = TimeProvider.GetUtcNow();

		lock (Lock) {
			if (keepRunning) {
				if (!Outages.TryGetValue(botName, out Outage? outage)) {
					// One timer per outage: the clock runs from its first disconnect, and an outage is reported once
					Outage started = new(now);
					started.Timer = TimeProvider.CreateTimer(_ => OnTimer(botName, started), null, NotifyAfter, Timeout.InfiniteTimeSpan);
					Outages[botName] = outage = started;
				}

				outage.LastDisconnectAt = now;

				if (StopClassifier.IsReason(reason)) {
					outage.Reason = reason;
				}

				return null;
			}

			// Report once: Stop() disconnects at most once per connection, so a classified stop ends the outage and
			// uses up its evidence; the next stop disconnect can only come after the bot was started again
			return ClassifyStop(botName, now, requiredInput, reason);
		}
	}

	// The bot runs again: what ASF logged before must not explain a later stop
	internal Notification? OnLoggedOn(string botName) {
		DateTimeOffset now = TimeProvider.GetUtcNow();

		lock (Lock) {
			Outages.TryGetValue(botName, out Outage? outage);
			EndOutage(botName);

			return outage is { Notified: true } ? new Notification(NotificationKind.BotReconnected, botName, now, Duration: now - outage.DisconnectedAt) : null;
		}
	}

	// Under Lock
	private Notification? ClassifyStop(string botName, DateTimeOffset t, string? requiredInput, EResult hookReason) {
		(TapEntry? giveUpError, TapEntry? reasonEntry) = Tap.Get(botName);
		Notification? notification = StopClassifier.Classify(botName, t, IsShuttingDown(), requiredInput, hookReason, giveUpError, reasonEntry);
		EndOutage(botName);

		return notification;
	}

	// Under Lock
	private void EndOutage(string botName) {
		if (Outages.Remove(botName, out Outage? outage)) {
			outage.Timer?.Dispose();
		}

		Tap.Forget(botName);
	}

	// Spec section 5.2, on a timer thread
	private void OnTimer(string botName, Outage outage) {
		try {
			DateTimeOffset now = TimeProvider.GetUtcNow();

			// Read outside the lock; an outage that ends meanwhile is caught below
			BotStatus? status = Host.GetStatus(botName);

			// Emits under the lock: a log-on racing this callback waits, so its BotReconnected is queued after the
			// BotDisconnected and sees whether that one was accepted
			lock (Lock) {
				// Logged on, stopped or destroyed while this callback was on its way
				if (!Outages.TryGetValue(botName, out Outage? current) || (current != outage)) {
					return;
				}

				switch (status) {
					case { KeepRunning: false } stopped:
						// Stopped meanwhile without a hook: no hook reason, the tap supplies it. T is the latest disconnect,
						// where ASF's give-up error sits, not the first one of the outage
						if (ClassifyStop(botName, outage.LastDisconnectAt, stopped.RequiredInput, EResult.OK) is { } stop) {
							Host.Emit(stop);
						}

						break;
					case { IsLoggedOn: false }:
						outage.Notified = Host.Emit(new Notification(NotificationKind.BotDisconnected, botName, now, outage.Reason?.ToString(), Duration: now - outage.DisconnectedAt));

						break;
					default:
						// ASF no longer has the bot, or a log-on callback was missed
						EndOutage(botName);

						break;
				}
			}
		} catch (Exception e) {
			// An exception escaping a timer callback would take ASF down
			Host.OnError(e);
		}
	}

	private sealed class Outage(DateTimeOffset disconnectedAt) {
		internal DateTimeOffset DisconnectedAt { get; } = disconnectedAt;
		internal DateTimeOffset LastDisconnectAt { get; set; } = disconnectedAt;
		internal bool Notified { get; set; }

		// Last reason that is not OK or Invalid
		internal EResult? Reason { get; set; }

		// Spent once it fired: a later disconnect of the same outage starts no other
		internal ITimer? Timer { get; set; }
	}
}
