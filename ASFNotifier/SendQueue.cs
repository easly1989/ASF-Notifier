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
	internal static readonly TimeSpan MinRetryAfter = TimeSpan.FromSeconds(1);
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

	private long FlushDeadline; // Environment.TickCount64 value, set when shutdown starts
	private int DroppedSinceLog;
	private DateTimeOffset? LastDropLog;
	private bool Outage;
	private int Pending; // accepted and not yet sent or rejected; a message abandoned at shutdown stays counted
	private int Shutdown;

	internal bool IsShuttingDown => Volatile.Read(ref Shutdown) != 0;
	internal bool IsStopped => Worker.IsCompleted;

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
			bool first;
			long deadline;

			lock (Lock) {
				first = Shutdown == 0;

				if (first) {
					FlushDeadline = Environment.TickCount64 + (long) FlushTimeout.TotalMilliseconds;
					Volatile.Write(ref Shutdown, 1);
				}

				deadline = FlushDeadline;
			}

			if (!first) {
				// A concurrent or later call only waits out what is left of the first call's budget
				long left = deadline - Environment.TickCount64;

				if (left > 0) {
					Worker.Wait(TimeSpan.FromMilliseconds(left));
				}

				return;
			}

			Queue.Writer.TryComplete();

			// Async so a waiting worker continues on the thread pool, not inside this call
			_ = StopWaiting.CancelAsync();

			if (!Worker.Wait(FlushTimeout)) {
				_ = Abandon.CancelAsync();
			}

			int unsent = Volatile.Read(ref Pending);
			int dropped;

			lock (Lock) {
				dropped = DroppedSinceLog;
			}

			if ((unsent > 0) || (dropped > 0)) {
				Log.Warning($"ASF is shutting down: {unsent} queued notification(s) were not sent" + (dropped > 0 ? $", and {dropped} more were dropped earlier because the queue was full" : ""));
			}
		} catch (Exception e) {
			LogUnexpected(e);
		}
	}

	// Type and first stack frame only: an exception message could carry request details, so it is never logged
	private void LogUnexpected(Exception e) {
		try {
			string? frame = e.StackTrace?.Split((char) 10)[0].Trim();

			Log.Error($"Unexpected {e.GetType().Name} in the send queue{(string.IsNullOrEmpty(frame) ? "" : $" {frame}")}");
		} catch (Exception) {
			// Nothing left to do: the process is exiting or the logger itself failed
		}
	}

	private void OnDropped(Notification dropped) {
		Interlocked.Decrement(ref Pending);

		lock (Lock) {
			DroppedSinceLog++;
		}

		ReportDrops();
	}

	// Builds the line under the lock and logs outside it; a failing logger must never reach Enqueue or kill the worker
	private void ReportDrops() {
		string? line = null;

		lock (Lock) {
			if (DroppedSinceLog == 0) {
				return;
			}

			DateTimeOffset now = TimeProvider.GetUtcNow();

			if ((LastDropLog == null) || ((now - LastDropLog.Value) >= DropLogInterval)) {
				line = $"The send queue is full: {DroppedSinceLog} oldest notification(s) dropped";
				DroppedSinceLog = 0;
				LastDropLog = now;
			}
		}

		if (line == null) {
			return;
		}

		try {
			Log.Warning(line);
		} catch (Exception) {
			// The count is already reset; the logger failing is not ours to fix
		}
	}

	private void EndOutage() {
		if (Outage) {
			Outage = false;
			Log.Info("Matrix delivery recovered, sending queued notifications");
		}
	}

	private async Task ProcessAsync(Notification notification) {
		FormattedMessage message = Formatter.Format(notification);
		string transactionId = Guid.NewGuid().ToString("N");
		int failures = 0;

		while (true) {
			SendResult result = await Target.SendAsync(message, transactionId, false, Abandon.Token).ConfigureAwait(false);

			if (result.Outcome == SendOutcome.Sent) {
				EndOutage();
				Interlocked.Decrement(ref Pending);

				return;
			}

			if (result.Outcome == SendOutcome.Dropped) {
				EndOutage();
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
				// At least a second, so a zero retry_after cannot spin
				wait = TimeSpan.FromTicks(Math.Clamp((result.Delay ?? Backoff(failures)).Ticks, MinRetryAfter.Ticks, MaxRetryAfter.Ticks));
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
		// Restarts the read loop after an unexpected error; ends once the queue is closed and drained or abandoned
		while (!Queue.Reader.Completion.IsCompleted) {
			try {
				await foreach (Notification notification in Queue.Reader.ReadAllAsync(Abandon.Token).ConfigureAwait(false)) {
					try {
						await ProcessAsync(notification).ConfigureAwait(false);
						ReportDrops();
					} catch (OperationCanceledException) when (Abandon.IsCancellationRequested) {
						return;
					} catch (Exception e) {
						LogUnexpected(e);
						Interlocked.Decrement(ref Pending);
					}
				}
			} catch (OperationCanceledException) {
				// Abandoned at the flush deadline
				return;
			} catch (Exception e) {
				LogUnexpected(e);
			}
		}
	}
}
