using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ASFNotifier.Tests;

#pragma warning disable CA1812 // MSTest uses this class during runtime
[TestClass]
internal sealed class SendQueueTests {
	private static readonly string[] ExpectedOrder = ["a", "b", "c"];
	private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

	[TestMethod]
	internal void BackoffDoublesFromTwoSecondsAndCapsAtFiveMinutes() {
		Assert.AreEqual(TimeSpan.FromSeconds(2), SendQueue.Backoff(1));
		Assert.AreEqual(TimeSpan.FromSeconds(4), SendQueue.Backoff(2));
		Assert.AreEqual(TimeSpan.FromSeconds(256), SendQueue.Backoff(8));
		Assert.AreEqual(TimeSpan.FromMinutes(5), SendQueue.Backoff(9));
		Assert.AreEqual(TimeSpan.FromMinutes(5), SendQueue.Backoff(1000));
	}

	[TestMethod]
	internal void DroppedSkipsToTheNextWithoutRetryOrDelay() {
		ScriptedChannel channel = new(_ => Task.FromResult(SendResult.Dropped("403 M_FORBIDDEN")));
		RecordingDelay delay = new();
		FakeLog log = new();

		using SendQueue queue = new(channel, log, new FakeTimeProvider(), delay.Wait);

		queue.Enqueue(Note("a"));
		queue.Enqueue(Note("b"));
		queue.Enqueue(Note("c"));
		channel.WaitForCalls(3);
		queue.Flush();

		Assert.AreEqual(3, channel.Calls.Count);
		Assert.AreEqual(0, delay.Delays.Count);
		Assert.AreEqual(0, log.All.Count());
	}

	[TestMethod]
	internal async Task DropOldestAtOneHundredAndOneAndLogsOncePerMinute() {
		TaskCompletionSource gate = new();
		ScriptedChannel channel = new(async _ => {
			await gate.Task.ConfigureAwait(false);

			return SendResult.Sent();
		});
		FakeTimeProvider time = new();
		FakeLog log = new();

		using SendQueue queue = new(channel, log, time, new RecordingDelay().Wait);

		// The worker takes the first item and blocks in the send; the queue then holds up to 100
		queue.Enqueue(Note("n0"));
		channel.WaitForCalls(1);

		for (int i = 1; i <= 101; i++) {
			queue.Enqueue(Note($"n{i}"));
		}

		Assert.AreEqual(1, log.Warnings.Count);

		// More drops inside the same minute stay silent, then one line after it
		queue.Enqueue(Note("n102"));
		queue.Enqueue(Note("n103"));
		Assert.AreEqual(1, log.Warnings.Count);

		time.Advance(SendQueue.DropLogInterval);
		queue.Enqueue(Note("n104"));
		Assert.AreEqual(2, log.Warnings.Count);
		StringAssert.Contains(log.Warnings[1], "3 ", StringComparison.Ordinal);

		gate.SetResult();
		queue.Flush();

		// n0 plus the 100 newest; n1..n4 were dropped oldest-first
		Assert.AreEqual(101, channel.Calls.Count);
		Assert.AreEqual("n0", channel.Calls[0].Text);
		Assert.AreEqual("n5", channel.Calls[1].Text);
		Assert.AreEqual("n104", channel.Calls[^1].Text);
	}

	[TestMethod]
	internal void ExceptionsFromTheChannelOrFormatterNeverEscapeAndNeverLogTheToken() {
		int call = 0;
		ScriptedChannel channel = new(_ => ++call == 1 ? throw new InvalidOperationException($"boom {TestData.Token}") : Task.FromResult(SendResult.Sent()));
		FakeLog log = new();

		using SendQueue queue = new(channel, log, new FakeTimeProvider(), new RecordingDelay().Wait);

		// An undefined kind makes the formatter throw; the channel throws on the first real send
		queue.Enqueue(new Notification((NotificationKind) 200, "bot1", DateTimeOffset.UnixEpoch));
		queue.Enqueue(Note("a"));
		queue.Enqueue(Note("b"));
		channel.WaitForCalls(2);
		queue.Flush();

		Assert.AreEqual(2, channel.Calls.Count);
		Assert.AreEqual(2, log.Errors.Count);
		Assert.IsFalse(log.All.Any(static line => line.Contains(TestData.Token, StringComparison.Ordinal)));
	}

	[TestMethod]
	internal void FatalAndDegradedOutcomesDropWithoutRetryOrDelay() {
		SendResult[] outcomes = [SendResult.Dropped("401 M_UNKNOWN_TOKEN"), SendResult.Dropped("degraded"), SendResult.Dropped("degraded")];
		int call = 0;
		ScriptedChannel channel = new(_ => Task.FromResult(outcomes[call++]));
		RecordingDelay delay = new();

		using SendQueue queue = new(channel, new FakeLog(), new FakeTimeProvider(), delay.Wait);

		for (int i = 0; i < 3; i++) {
			queue.Enqueue(Note($"n{i}"));
		}

		channel.WaitForCalls(3);
		queue.Flush();

		Assert.AreEqual(3, channel.Calls.Count);
		Assert.AreEqual(3, channel.Calls.Select(static c => c.TransactionId).Distinct().Count());
		Assert.AreEqual(0, delay.Delays.Count);
	}

	[TestMethod]
	internal void FlushOnAnEmptyQueueReturnsAtOnce() {
		ScriptedChannel channel = new(_ => Task.FromResult(SendResult.Sent()));
		FakeLog log = new();

		using SendQueue queue = new(channel, log, new FakeTimeProvider());

		Stopwatch watch = Stopwatch.StartNew();
		queue.Flush();
		queue.Flush();

		Assert.IsLessThan(SendQueue.DefaultFlushTimeout, watch.Elapsed);
		Assert.AreEqual(0, channel.Calls.Count);
		Assert.AreEqual(0, log.All.Count());
		Assert.IsFalse(queue.Enqueue(Note("late")));
	}

	[TestMethod]
	internal async Task FlushSendsQueuedItemsWithoutBackoffAndReportsTheOnesItCouldNotSend() {
		TaskCompletionSource<SendResult> gate = new();
		int call = 0;
		ScriptedChannel channel = new(_ => ++call == 1 ? gate.Task : Task.FromResult(SendResult.Sent()));
		RecordingDelay delay = new();
		FakeLog log = new();

		using SendQueue queue = new(channel, log, new FakeTimeProvider(), delay.Wait);

		queue.Enqueue(Note("a"));
		queue.Enqueue(Note("b"));
		queue.Enqueue(Note("c"));
		channel.WaitForCalls(1);

		Task flush = Task.Run(queue.Flush);
		Assert.IsTrue(SpinWait.SpinUntil(() => queue.IsShuttingDown, Wait));

		// "a" fails transiently while flushing, so it is abandoned without a wait
		gate.SetResult(SendResult.Transient("500"));
		await flush.WaitAsync(Wait).ConfigureAwait(false);

		CollectionAssert.AreEqual(ExpectedOrder, channel.Calls.Select(static c => c.Text).ToArray());
		Assert.AreEqual(0, delay.Delays.Count);
		Assert.AreEqual(1, log.Warnings.Count);
		StringAssert.Contains(log.Warnings[0], "1 queued", StringComparison.Ordinal);
	}

	[TestMethod]
	internal async Task FlushGivesUpAtTheCapAndLogsOneLine() {
		CancellationToken seen = default;
		ScriptedChannel channel = new(async token => {
			seen = token;
			await Task.Delay(Timeout.Infinite, token).ConfigureAwait(false);

			return SendResult.Sent();
		});
		FakeLog log = new();

		using SendQueue queue = new(channel, log, new FakeTimeProvider(), new RecordingDelay().Wait, TimeSpan.FromMilliseconds(50));

		queue.Enqueue(Note("a"));
		queue.Enqueue(Note("b"));
		queue.Enqueue(Note("c"));
		channel.WaitForCalls(1);

		Stopwatch watch = Stopwatch.StartNew();
		queue.Flush();

		Assert.IsLessThan(Wait, watch.Elapsed);
		Assert.AreEqual(1, channel.Calls.Count);
		Assert.AreEqual(1, log.All.Count());
		StringAssert.Contains(log.Warnings[0], "3 queued", StringComparison.Ordinal);

		// The in-flight send was cancelled and the worker ended
		Assert.IsTrue(SpinWait.SpinUntil(() => seen.IsCancellationRequested && queue.IsStopped, Wait));
	}

	[TestMethod]
	internal void FlushCutsAnOutstandingBackoffShort() {
		ScriptedChannel channel = new(_ => Task.FromResult(SendResult.Transient("500")));
		RecordingDelay delay = new() { Block = true };
		FakeLog log = new();

		using SendQueue queue = new(channel, log, new FakeTimeProvider(), delay.Wait);

		queue.Enqueue(Note("a"));
		channel.WaitForCalls(1);
		delay.WaitForDelays(1);

		queue.Flush();

		Assert.AreEqual(1, channel.Calls.Count);
		StringAssert.Contains(log.Warnings.Last(), "1 queued", StringComparison.Ordinal);
	}

	[TestMethod]
	internal void OutageLogsOneWarningAndOneInfoNoMatterHowManyRetries() {
		SendResult[] outcomes = [SendResult.Transient("500"), SendResult.Transient("network error"), SendResult.Transient("timeout"), SendResult.Sent(), SendResult.Sent()];
		int call = 0;
		ScriptedChannel channel = new(_ => Task.FromResult(outcomes[call++]));
		FakeLog log = new();

		using SendQueue queue = new(channel, log, new FakeTimeProvider(), new RecordingDelay().Wait);

		queue.Enqueue(Note("a"));
		queue.Enqueue(Note("b"));
		channel.WaitForCalls(5);
		queue.Flush();

		Assert.AreEqual(1, log.Warnings.Count);
		Assert.AreEqual(1, log.Infos.Count);
		Assert.AreEqual(0, log.Errors.Count);
	}

	[TestMethod]
	internal void QueueKeepsFifoOrderAndPassesNoBypass() {
		ScriptedChannel channel = new(_ => Task.FromResult(SendResult.Sent()));

		using SendQueue queue = new(channel, new FakeLog(), new FakeTimeProvider());

		string[] names = Enumerable.Range(0, 20).Select(static i => $"n{i}").ToArray();

		foreach (string name in names) {
			queue.Enqueue(Note(name));
		}

		channel.WaitForCalls(20);
		queue.Flush();

		CollectionAssert.AreEqual(names, channel.Calls.Select(static c => c.Text).ToArray());
		Assert.IsTrue(channel.Calls.All(static c => !c.Bypass));
	}

	[TestMethod]
	internal void RetryAfterWaitsTheGivenDelayCappedAtOneMinuteAndKeepsTheTransactionId() {
		SendResult[] outcomes = [SendResult.RetryAfter(TimeSpan.FromSeconds(7), "429"), SendResult.RetryAfter(TimeSpan.FromMinutes(10), "429"), SendResult.RetryAfter(null, "429"), SendResult.Sent()];
		int call = 0;
		ScriptedChannel channel = new(_ => Task.FromResult(outcomes[call++]));
		RecordingDelay delay = new();

		using SendQueue queue = new(channel, new FakeLog(), new FakeTimeProvider(), delay.Wait);

		queue.Enqueue(Note("a"));
		channel.WaitForCalls(4);
		queue.Flush();

		CollectionAssert.AreEqual(new[] { TimeSpan.FromSeconds(7), TimeSpan.FromSeconds(60), SendQueue.Backoff(3) }, delay.Delays.ToArray());
		Assert.AreEqual(1, channel.Calls.Select(static c => c.TransactionId).Distinct().Count());
	}

	[TestMethod]
	internal void TransientRetriesTheSameTransactionIdWithDoublingBackoffCappedAtFiveMinutes() {
		int call = 0;
		ScriptedChannel channel = new(_ => Task.FromResult(++call <= 11 ? SendResult.Transient("500") : SendResult.Sent()));
		RecordingDelay delay = new();

		using SendQueue queue = new(channel, new FakeLog(), new FakeTimeProvider(), delay.Wait);

		queue.Enqueue(Note("a"));
		queue.Enqueue(Note("b"));
		channel.WaitForCalls(13);
		queue.Flush();

		TimeSpan[] expected = [.. Enumerable.Range(1, 11).Select(SendQueue.Backoff)];

		CollectionAssert.AreEqual(expected, delay.Delays.ToArray());
		Assert.AreEqual(TimeSpan.FromSeconds(2), expected[0]);
		Assert.AreEqual(TimeSpan.FromSeconds(4), expected[1]);
		Assert.AreEqual(TimeSpan.FromMinutes(5), expected[^1]);

		// 11 failures and a success for "a" share one id; "b" gets its own
		Assert.AreEqual(1, channel.Calls.Take(12).Select(static c => c.TransactionId).Distinct().Count());
		Assert.AreNotEqual(channel.Calls[0].TransactionId, channel.Calls[12].TransactionId);
	}

	[TestMethod]
	internal void ThrowingLoggerNeverReachesEnqueueAndTheWorkerKeepsSending() {
		ScriptedChannel channel = new(_ => Task.FromResult(SendResult.Sent()));
		ThrowingLog log = new();

		using SendQueue queue = new(channel, log, new FakeTimeProvider(), new RecordingDelay().Wait);

		// Overflow with a logger that throws on every call
		TaskCompletionSource gate = new();
		ScriptedChannel blocked = new(async _ => {
			await gate.Task.ConfigureAwait(false);

			return SendResult.Sent();
		});

		using SendQueue overflow = new(blocked, log, new FakeTimeProvider(), new RecordingDelay().Wait);

		overflow.Enqueue(Note("n0"));
		blocked.WaitForCalls(1);

		for (int i = 1; i <= 105; i++) {
			Assert.IsTrue(overflow.Enqueue(Note($"n{i}")));
		}

		gate.SetResult();
		overflow.Flush();
		Assert.AreEqual(101, blocked.Calls.Count);

		// And the worker survives a logger that throws while it reports
		queue.Enqueue(new Notification((NotificationKind) 200, "bot1", DateTimeOffset.UnixEpoch));
		queue.Enqueue(Note("a"));
		channel.WaitForCalls(1);
		queue.Flush();

		Assert.AreEqual(1, channel.Calls.Count);
	}

	[TestMethod]
	internal void ZeroRetryAfterWaitsAtLeastOneSecond() {
		SendResult[] outcomes = [SendResult.RetryAfter(TimeSpan.Zero, "429"), SendResult.Sent()];
		int call = 0;
		ScriptedChannel channel = new(_ => Task.FromResult(outcomes[call++]));
		RecordingDelay delay = new();

		using SendQueue queue = new(channel, new FakeLog(), new FakeTimeProvider(), delay.Wait);

		queue.Enqueue(Note("a"));
		channel.WaitForCalls(2);
		queue.Flush();

		CollectionAssert.AreEqual(new[] { SendQueue.MinRetryAfter }, delay.Delays.ToArray());
	}

	[TestMethod]
	internal void DroppedAfterAnOutageEndsIt() {
		SendResult[] outcomes = [SendResult.Transient("500"), SendResult.Dropped("403 M_FORBIDDEN")];
		int call = 0;
		ScriptedChannel channel = new(_ => Task.FromResult(outcomes[call++]));
		FakeLog log = new();

		using SendQueue queue = new(channel, log, new FakeTimeProvider(), new RecordingDelay().Wait);

		queue.Enqueue(Note("a"));
		channel.WaitForCalls(2);
		queue.Flush();

		Assert.AreEqual(1, log.Warnings.Count);
		Assert.AreEqual(1, log.Infos.Count);
	}

	[TestMethod]
	internal void SecondFlushWaitsOnlyForTheRemainingBudget() {
		ScriptedChannel channel = new(static async token => {
			await Task.Delay(Timeout.Infinite, token).ConfigureAwait(false);

			return SendResult.Sent();
		});

		using SendQueue queue = new(channel, new FakeLog(), new FakeTimeProvider(), new RecordingDelay().Wait, TimeSpan.FromMilliseconds(200));

		queue.Enqueue(Note("a"));
		channel.WaitForCalls(1);

		Stopwatch watch = Stopwatch.StartNew();
		Task first = Task.Run(queue.Flush);
		Assert.IsTrue(SpinWait.SpinUntil(() => queue.IsShuttingDown, Wait));
		queue.Flush();
		first.Wait(Wait);

		// Both calls together stay within one budget, not two
		Assert.IsLessThan(TimeSpan.FromMilliseconds(1500), watch.Elapsed);
	}

	private static Notification Note(string bot) => new(NotificationKind.Test, bot, DateTimeOffset.UnixEpoch);
}

#pragma warning restore CA1812 // MSTest uses this class during runtime

internal sealed class ScriptedChannel(Func<CancellationToken, Task<SendResult>> handler) : INotificationChannel {
	private readonly List<(string Text, string TransactionId, bool Bypass)> Recorded = [];
	internal List<(string Text, string TransactionId, bool Bypass)> Calls {
		get {
			lock (Recorded) {
				return [.. Recorded];
			}
		}
	}

	public Task<SendResult> SendAsync(FormattedMessage message, string transactionId, bool bypassDegraded, CancellationToken cancellationToken) {
		lock (Recorded) {
			// The bot name sits between "from " and " —" in the test message
			Recorded.Add((message.PlainText.Split(' ')[4], transactionId, bypassDegraded));
		}

		return handler(cancellationToken);
	}

	internal void WaitForCalls(int count) {
		if (!SpinWait.SpinUntil(() => Calls.Count >= count, TimeSpan.FromSeconds(10))) {
			throw new TimeoutException($"Fewer than {count} sends happened");
		}
	}
}

internal sealed class RecordingDelay {
	private readonly List<TimeSpan> Recorded = [];

	internal bool Block { get; init; }

	internal List<TimeSpan> Delays {
		get {
			lock (Recorded) {
				return [.. Recorded];
			}
		}
	}

	internal Task Wait(TimeSpan span, CancellationToken cancellationToken) {
		lock (Recorded) {
			Recorded.Add(span);
		}

		return Block ? Task.Delay(Timeout.Infinite, cancellationToken) : Task.CompletedTask;
	}

	internal void WaitForDelays(int count) {
		if (!SpinWait.SpinUntil(() => Delays.Count >= count, TimeSpan.FromSeconds(10))) {
			throw new TimeoutException($"Fewer than {count} delays happened");
		}
	}
}

internal sealed class ThrowingLog : INotifierLog {
	public void Error(string message) => throw new InvalidOperationException(message);
	public void Info(string message) => throw new InvalidOperationException(message);
	public void Warning(string message) => throw new InvalidOperationException(message);
}
