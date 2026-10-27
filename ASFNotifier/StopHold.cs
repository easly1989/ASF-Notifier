using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace ASFNotifier;

// Spec section 5.3: ASF's exit, restart, update and IPC exit stop every bot with Stop(true) before the process
// exits, and raise no signal. Stop notifications wait here briefly and are dropped when the process goes down
// meanwhile, or when every bot stopped together, unless a give-up error backs them (a shutdown logs none).
internal sealed class StopHold {
	// ASF's restart waits about 6 s between stopping the bots and exiting
	internal static readonly TimeSpan HoldTime = TimeSpan.FromSeconds(10);

	private readonly List<Notification> Held = [];
	private readonly Lock Lock = new();

	internal DateTimeOffset? NextDue {
		get {
			lock (Lock) {
				return Held.Count > 0 ? Held.Min(static notification => notification.OccurredAt) + HoldTime : null;
			}
		}
	}

	internal void Add(Notification notification) {
		ArgumentNullException.ThrowIfNull(notification);

		lock (Lock) {
			Held.Add(notification);
		}
	}

	// Returns what to send now; runningBots is how many bots ASF still keeps running
	internal List<Notification> Release(DateTimeOffset now, bool shuttingDown, int runningBots) {
		lock (Lock) {
			List<Notification> due = Held.Where(notification => (now - notification.OccurredAt) >= HoldTime).ToList();

			if (due.Count == 0) {
				return due;
			}

			if (shuttingDown) {
				Held.Clear();

				return [];
			}

			// One bot stopping is never a mass stop, even on a single-bot instance
			if ((runningBots == 0) && (Held.Select(static notification => notification.BotName).Distinct(StringComparer.Ordinal).Count() > 1)) {
				Held.RemoveAll(static notification => !notification.AsfGaveUp);
				due.RemoveAll(static notification => !notification.AsfGaveUp);
			}

			Held.RemoveAll(due.Contains);

			return due;
		}
	}
}
