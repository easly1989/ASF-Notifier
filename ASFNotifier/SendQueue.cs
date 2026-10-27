using System;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace ASFNotifier;

// Hooks call Enqueue and return; one background worker formats and sends, so a slow homeserver delays notifications, never ASF
internal sealed class SendQueue : IDisposable {
	internal const int Capacity = 100;
	internal static readonly TimeSpan DefaultFlushTimeout = TimeSpan.FromSeconds(3);
	internal static readonly TimeSpan DropLogInterval = TimeSpan.FromMinutes(1);
	internal static readonly TimeSpan InitialBackoff = TimeSpan.FromSeconds(2);
	internal static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(5);
	internal static readonly TimeSpan MaxRetryAfter = TimeSpan.FromSeconds(60);

	private readonly CancellationTokenSource Abandon = new();
	private readonly Func<TimeSpan, CancellationToken, Task> Delay;
	private readonly TimeSpan FlushTimeout;
	private readonly Lock Lock = new();
	private readonly INotifierLog Log;
	private readonly Channel<Notification> Queue;
	private readonly CancellationTokenSource StopWaiting = new();
	private readonly INotificationChannel Target;
	private readonly TimeProvider TimeProvider;
	private readonly Task Worker;

	private int DroppedSinceLog;
	private DateTimeOffset? LastDropLog;
	private bool Outage;
	private int Pending; // accepted and not yet sent or rejected; a message abandoned at shutdown stays counted
	private int Shutdown;

	internal bool IsShuttingDown => Volatile.Read(ref Shutdown) != 0;

	// delay and flushTimeout are test seams; production passes neither
	internal SendQueue(INotificationChannel target, INotifierLog log, TimeProvider timeProvider, Func<TimeSpan, CancellationToken, Task>? delay = null, TimeSpan? flushTimeout = null) {
		Target = target;
		Log = log;
		TimeProvider = timeProvider;
		Delay = delay ?? ((span, token) => Task.Delay(span, timeProvider, token));
		FlushTimeout = flushTimeout ?? DefaultFlushTimeout;
		Queue = Channel.CreateBounded<Notification>(new BoundedChannelOptions(Capacity) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true }, OnDropped);
		Worker = Task.Run(RunAsync);
	}

	internal static TimeSpan Backoff(int failures) => TimeSpan.FromSeconds(Math.Min(InitialBackoff.TotalSeconds * Math.Pow(2, Math.Min(failures, 30) - 1), MaxBackoff.TotalSeconds));

	public void Dispose() {
		Flush();

		// A worker abandoned at the flush deadline may still hold the tokens; leave them to the finalizer then
		if (Worker.IsCompleted) {
			Abandon.Dispose();
			StopWaiting.Dispose();
		}
	}

	// Never blocks and never throws for a valid notification; false only after the queue was closed
	internal bool Enqueue(Notification notification) {
		ArgumentNullException.ThrowIfNull(notification);

		Interlocked.Increment(ref Pending);

		if (Queue.Writer.TryWrite(notification)) {
			return true;
		}

		Interlocked.Decrement(ref Pending);

		return false;
	}

	// ProcessExit handler: sends what is queued without backoff, waits at most FlushTimeout in total, never throws
	internal void Flush() {
		try {
			if (Interlocked.Exchange(ref Shutdown, 1) != 0) {
				return;
			}

			Queue.Writer.TryComplete();

			// Async so a waiting worker continues on the thread pool, not inside this call
			_ = StopWaiting.CancelAsync();

			if (!Worker.Wait(FlushTimeout)) {
				_ = Abandon.CancelAsync();
			}

			int unsent = Volatile.Read(ref Pending);

			if (unsent > 0) {
				Log.Warning($"ASF is shutting down: {unsent} queued notification(s) were not sent");
			}
		} catch (Exception e) {
			LogUnexpected(e);
		}
	}

	private void LogUnexpected(Exception e) {
		try {
			Log.Error($"Unexpected {e.GetType().Name} in the send queue");
		} catch (Exception) {
			// Nothing left to do: the process is exiting or the logger itself failed
		}
	}

	private void OnDropped(Notification dropped) {
		Interlocked.Decrement(ref Pending);

		lock (Lock) {
			DroppedSinceLog++;
			ReportDrops();
		}
	}

	// Caller holds Lock
	private void ReportDrops() {
		if (DroppedSinceLog == 0) {
			return;
		}

		DateTimeOffset now = TimeProvider.GetUtcNow();

		if ((LastDropLog != null) && ((now - LastDropLog.Value) < DropLogInterval)) {
			return;
		}

		Log.Warning($"The send queue is full: {DroppedSinceLog} oldest notification(s) dropped");
		DroppedSinceLog = 0;
		LastDropLog = now;
	}

	private async Task ProcessAsync(Notification notification) {
		FormattedMessage message = Formatter.Format(notification);
		string transactionId = Guid.NewGuid().ToString("N");
		int failures = 0;

		while (true) {
			SendResult result = await Target.SendAsync(message, transactionId, false, Abandon.Token).ConfigureAwait(false);

			if (result.Outcome == SendOutcome.Sent) {
				if (Outage) {
					Outage = false;
					Log.Info("Matrix delivery recovered, sending queued notifications");
				}

				Interlocked.Decrement(ref Pending);

				return;
			}

			if (result.Outcome == SendOutcome.Dropped) {
				Interlocked.Decrement(ref Pending);

				return;
			}

			// No backoff while flushing: this message is abandoned and stays counted as unsent
			if (IsShuttingDown) {
				return;
			}

			failures++;

			TimeSpan wait;

			if (result.Outcome == SendOutcome.RetryAfter) {
				wait = TimeSpan.FromTicks(Math.Min((result.Delay ?? Backoff(failures)).Ticks, MaxRetryAfter.Ticks));
			} else {
				if (!Outage) {
					Outage = true;
					Log.Warning($"Matrix is unreachable ({result.Error ?? "unknown error"}); notifications are queued and retried");
				}

				wait = Backoff(failures);
			}

			try {
				await Delay(wait, StopWaiting.Token).ConfigureAwait(false);
			} catch (OperationCanceledException) when (StopWaiting.IsCancellationRequested) {
				return;
			}
		}
	}

	private async Task RunAsync() {
		try {
			await foreach (Notification notification in Queue.Reader.ReadAllAsync(Abandon.Token).ConfigureAwait(false)) {
				try {
					await ProcessAsync(notification).ConfigureAwait(false);
				} catch (OperationCanceledException) when (Abandon.IsCancellationRequested) {
					return;
				} catch (Exception e) {
					// Only the type is logged: an exception message could carry request details
					LogUnexpected(e);
					Interlocked.Decrement(ref Pending);
				}

				lock (Lock) {
					ReportDrops();
				}
			}
		} catch (OperationCanceledException) {
			// Abandoned at the flush deadline
		} catch (Exception e) {
			LogUnexpected(e);
		}
	}
}
