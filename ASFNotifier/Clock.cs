using System;
using System.Diagnostics;
using System.Threading;

namespace ASFNotifier;

// The plugin's time source, faked in tests. Not System.TimeProvider: ASF's OS-specific builds are trimmed and drop
// TimeProvider and ITimer, which ASF itself never uses (issue #23). Built only on types ASF keeps.
internal abstract class Clock {
	internal static Clock System { get; } = new SystemClock();

	// Ticks per second of GetTimestamp
	internal abstract long TimestampFrequency { get; }

	internal abstract IClockTimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period);

	internal TimeSpan GetElapsedTime(long startingTimestamp, long endingTimestamp) => new((long) ((endingTimestamp - startingTimestamp) * ((double) TimeSpan.TicksPerSecond / TimestampFrequency)));

	// Monotonic: a wall clock stepping back does not move it
	internal abstract long GetTimestamp();

	internal abstract DateTimeOffset GetUtcNow();

	private sealed class SystemClock : Clock {
		internal override long TimestampFrequency => Stopwatch.Frequency;

		internal override IClockTimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) => new SystemTimer(callback, state, dueTime, period);

		internal override long GetTimestamp() => Stopwatch.GetTimestamp();

		internal override DateTimeOffset GetUtcNow() => DateTimeOffset.UtcNow;
	}

	private sealed class SystemTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) : IClockTimer {
		private readonly Timer Timer = new(callback, state, dueTime, period);

		public bool Change(TimeSpan dueTime, TimeSpan period) => Timer.Change(dueTime, period);

		public void Dispose() => Timer.Dispose();
	}
}

// What the plugin needs of a timer
internal interface IClockTimer : IDisposable {
	bool Change(TimeSpan dueTime, TimeSpan period);
}
