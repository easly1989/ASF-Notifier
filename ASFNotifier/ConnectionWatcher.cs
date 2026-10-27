using System;
using System.Collections.Generic;
using System.Threading;
using SteamKit2;

namespace ASFNotifier;

// Spec section 5.1 and 5.3: per-bot connection state, fed by the IBotConnection hooks. Returns what to send; never sends.
internal sealed class ConnectionWatcher {
	private readonly Func<bool> IsShuttingDown;
	private readonly Lock Lock = new();
	private readonly Dictionary<string, BotState> States = new(StringComparer.Ordinal);
	private readonly Func<string, (TapEntry? GiveUpError, TapEntry? Reason)> Tap;
	private readonly TimeProvider TimeProvider;

	internal ConnectionWatcher(Func<string, (TapEntry? GiveUpError, TapEntry? Reason)> tap, Func<bool> isShuttingDown, TimeProvider timeProvider) {
		Tap = tap;
		IsShuttingDown = isShuttingDown;
		TimeProvider = timeProvider;
	}

	internal bool IsTracked(string botName) {
		lock (Lock) {
			return States.ContainsKey(botName);
		}
	}

	internal Notification? OnDisconnected(string botName, EResult reason, bool keepRunning, string? requiredInput) {
		DateTimeOffset now = TimeProvider.GetUtcNow();

		lock (Lock) {
			// A stopped bot only disconnects again after it was started again, so a reported stop closes the outage
			if (!States.TryGetValue(botName, out BotState? state) || state.StopNotified) {
				state = new BotState();
				States[botName] = state;
			}

			if (keepRunning) {
				return null;
			}

			(TapEntry? giveUpError, TapEntry? reasonEntry) = Tap(botName);
			Notification? notification = StopClassifier.Classify(botName, now, IsShuttingDown(), state.StopNotified, requiredInput, reason, giveUpError, reasonEntry);
			state.StopNotified = true;

			return notification;
		}
	}

	internal void OnDestroy(string botName) {
		lock (Lock) {
			States.Remove(botName);
		}
	}

	internal void OnLoggedOn(string botName) {
		lock (Lock) {
			States.Remove(botName);
		}
	}

	private sealed class BotState {
		// Ticket #4 adds the disconnect time, reason and timer of the outage here
		internal bool StopNotified { get; set; }
	}
}
