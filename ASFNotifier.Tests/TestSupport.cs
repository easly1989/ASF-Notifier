using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ASFNotifier.Tests;

internal static class TestData {
	internal const string Homeserver = "https://matrix.example.org";
	internal const string RoomId = "!room:example.org";
	internal const string Token = "syt_FAKE_TOKEN_0000";

	internal static string ConfigJson(string extra = "") => "{\"ASFNotifier\": {\"Matrix\": {\"HomeserverUrl\": \"" + Homeserver + "\", \"RoomId\": \"" + RoomId + "\"}" + extra + "}}";

	internal static NotifierSettings Settings(string json, string? environmentToken = null, FakeLog? log = null) {
		Dictionary<string, JsonElement> properties = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json) ?? throw new InvalidOperationException(nameof(json));

		return NotifierSettings.Parse(properties, environmentToken, log ?? new FakeLog());
	}
}

internal sealed class FakeLog : INotifierLog {
	internal IEnumerable<string> All => Infos.Concat(Warnings).Concat(Errors);
	internal List<string> Errors { get; } = [];
	internal List<string> Infos { get; } = [];
	internal List<string> Warnings { get; } = [];

	public void Error(string message) => Errors.Add(message);
	public void Info(string message) => Infos.Add(message);
	public void Warning(string message) => Warnings.Add(message);
}

internal sealed class FakeTimeProvider : TimeProvider {
	// A test may dispose a timer from another thread while Advance runs
	private readonly Lock Lock = new();

	private readonly List<FakeTimer> Timers = [];

	internal int ArmedTimers {
		get {
			lock (Lock) {
				return Timers.Count(static timer => timer.Due != null);
			}
		}
	}

	internal int CreatedTimers { get; private set; }

	// Wall clock: setting it is a clock jump and leaves the monotonic timestamp alone; Advance moves both
	internal DateTimeOffset Now { get; set; } = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

	public override long TimestampFrequency => TimeSpan.TicksPerSecond;

	private long Elapsed;

	public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) {
		FakeTimer timer = new(this, callback, state);

		lock (Lock) {
			CreatedTimers++;
			Timers.Add(timer);
		}

		timer.Change(dueTime, period);

		return timer;
	}

	public override long GetTimestamp() => Elapsed;

	public override DateTimeOffset GetUtcNow() => Now;

	// Moves the clock, firing each timer that falls due on the way at its due time, on the calling thread
	internal void Advance(TimeSpan span) {
		DateTimeOffset end = Now + span;

		while (NextDue(end) is { Due: { } due } next) {
			MoveTo(due);
			next.Fire();
		}

		MoveTo(end);
	}

	private void MoveTo(DateTimeOffset time) {
		Elapsed += Math.Max(0, (time - Now).Ticks);
		Now = time;
	}

	private FakeTimer? NextDue(DateTimeOffset end) {
		lock (Lock) {
			return Timers.Where(timer => timer.Due <= end).OrderBy(static timer => timer.Due).FirstOrDefault();
		}
	}

	private void Remove(FakeTimer timer) {
		lock (Lock) {
			Timers.Remove(timer);
		}
	}

	private sealed class FakeTimer(FakeTimeProvider owner, TimerCallback callback, object? state) : ITimer {
		internal DateTimeOffset? Due { get; private set; }

		private TimeSpan Period = Timeout.InfiniteTimeSpan;

		public bool Change(TimeSpan dueTime, TimeSpan period) {
			Due = dueTime == Timeout.InfiniteTimeSpan ? null : owner.Now + dueTime;
			Period = period;

			return true;
		}

		public void Dispose() {
			Due = null;
			owner.Remove(this);
		}

		public ValueTask DisposeAsync() {
			Dispose();

			return ValueTask.CompletedTask;
		}

		internal void Fire() {
			Due = Period == Timeout.InfiniteTimeSpan ? null : Due + Period;
			callback(state);
		}
	}
}

internal sealed record RecordedRequest(HttpMethod Method, string Uri, string? Authorization, string? ContentType, string Body);

internal sealed class FakeHandler : HttpMessageHandler {
	private readonly Queue<Func<HttpResponseMessage>> Responses = new();

	internal List<RecordedRequest> Requests { get; } = [];

	internal void Enqueue(HttpStatusCode status, string body = "", Action<HttpResponseMessage>? configure = null) =>
		Responses.Enqueue(() => {
			HttpResponseMessage response = new(status) { Content = new StringContent(body) };
			configure?.Invoke(response);

			return response;
		});

	internal void EnqueueThrow(Exception exception) => Responses.Enqueue(() => throw exception);

	protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) {
		string body = request.Content == null ? "" : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

		Requests.Add(new RecordedRequest(request.Method, request.RequestUri?.AbsoluteUri ?? "", request.Headers.Authorization?.ToString(), request.Content?.Headers.ContentType?.MediaType, body));

		return Responses.Dequeue().Invoke();
	}
}

internal sealed class FakeChannel(Func<CancellationToken, Task<SendResult>> send) : INotificationChannel {
	internal List<(FormattedMessage Message, bool Bypass)> Calls { get; } = [];

	public Task<SendResult> SendAsync(FormattedMessage message, string transactionId, bool bypassDegraded, CancellationToken cancellationToken) {
		Calls.Add((message, bypassDegraded));

		return send(cancellationToken);
	}
}
