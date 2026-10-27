using System;
using System.Collections.Generic;
using System.Threading;
using SteamKit2;

namespace ASFNotifier;

// Spec section 5.1 and 5.3: per-bot connection state, fed by the IBotConnection hooks. Returns what to send; never sends.
internal sealed class ConnectionWatcher {
	private readonly Func<bool> IsShuttingDown;
	private readonly Lock Lock = new();

	// Bots in an outage; ticket #4 adds the disconnect time, reason and timer of each
	private readonly HashSet<string> Outages = new(StringComparer.Ordinal);

	private readonly IGiveUpLog Tap;
	private readonly TimeProvider TimeProvider;

	internal ConnectionWatcher(IGiveUpLog tap, Func<bool> isShuttingDown, TimeProvider timeProvider) {
		Tap = tap;
		IsShuttingDown = isShuttingDown;
		TimeProvider = timeProvider;
	}

	internal bool IsTracked(string botName) {
		lock (Lock) {
			return Outages.Contains(botName);
		}
	}

	internal Notification? OnDisconnected(string botName, EResult reason, bool keepRunning, string? requiredInput) {
		DateTimeOffset now = TimeProvider.GetUtcNow();

		lock (Lock) {
			if (keepRunning) {
				Outages.Add(botName);

				return null;
			}

			(TapEntry? giveUpError, TapEntry? reasonEntry) = Tap.Get(botName);
			Notification? notification = StopClassifier.Classify(botName, now, IsShuttingDown(), requiredInput, reason, giveUpError, reasonEntry);

			// Report once: Stop() disconnects at most once per connection, so a classified stop ends the outage and
			// uses up its evidence; the next stop disconnect can only come after the bot was started again
			Outages.Remove(botName);
			Tap.Forget(botName);

			return notification;
		}
	}

	internal void OnDestroy(string botName) => Clear(botName);

	// The bot runs again: what ASF logged before must not explain a later stop
	internal void OnLoggedOn(string botName) => Clear(botName);

	private void Clear(string botName) {
		lock (Lock) {
			Outages.Remove(botName);
			Tap.Forget(botName);
		}
	}
}
