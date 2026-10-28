using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ASFNotifier.Tests;

#pragma warning disable CA1812 // MSTest uses this class during runtime
[TestClass]
internal sealed class MatrixChannelTests {
	private static readonly FormattedMessage Message = new("plain <text>", "<b>html</b>");

	private sealed class Rig : IDisposable {
		private readonly HttpClient Client;

		internal MatrixChannel Channel { get; }
		internal FakeHandler Handler { get; } = new();
		internal FakeLog Log { get; } = new();
		internal FakeClock Time { get; } = new();

		internal Rig() {
			Client = MatrixChannel.CreateHttpClient(Handler);
			Channel = new MatrixChannel(Client, TestData.Homeserver, TestData.RoomId, TestData.Token, Time, Log);
		}

		public void Deconstruct(out MatrixChannel channel, out FakeHandler handler, out FakeLog log, out FakeClock time) {
			channel = Channel;
			handler = Handler;
			log = Log;
			time = Time;
		}

		public void Dispose() {
			Client.Dispose();
			Handler.Dispose();
		}
	}

	private static Task<SendResult> Send(MatrixChannel channel, bool bypass = false, CancellationToken cancellationToken = default) => channel.SendAsync(Message, "txn1", bypass, cancellationToken);

	[TestMethod]
	internal void HttpClientTimeoutIsTenSeconds() {
		using HttpClient client = MatrixChannel.CreateHttpClient();

		Assert.AreEqual(TimeSpan.FromSeconds(10), client.Timeout);
	}

	[TestMethod]
	[DataRow(64 * 1024, SendOutcome.Sent)]
	[DataRow((64 * 1024) + 1, SendOutcome.Transient)]
	internal async Task ResponseBodyIsCappedAt64KiB(int bodyLength, SendOutcome expected) {
		using Rig rig = new();
		(MatrixChannel channel, FakeHandler handler, _, _) = rig;
		handler.Enqueue(HttpStatusCode.OK, new string('x', bodyLength));

		SendResult result = await Send(channel).ConfigureAwait(false);

		Assert.AreEqual(expected, result.Outcome);
	}

	[TestMethod]
	internal async Task RequestShape() {
		using Rig rig = new();
		(MatrixChannel channel, FakeHandler handler, _, _) = rig;
		handler.Enqueue(HttpStatusCode.OK, """{"event_id": "$e"}""");

		SendResult result = await Send(channel).ConfigureAwait(false);

		Assert.AreEqual(SendOutcome.Sent, result.Outcome);
		RecordedRequest request = handler.Requests.Single();
		Assert.AreEqual(HttpMethod.Put, request.Method);
		Assert.AreEqual("https://matrix.example.org/_matrix/client/v3/rooms/%21room%3Aexample.org/send/m.room.message/txn1", request.Uri);
		Assert.AreEqual("Bearer " + TestData.Token, request.Authorization);
		Assert.AreEqual("application/json", request.ContentType);

		using JsonDocument body = JsonDocument.Parse(request.Body);
		Assert.AreEqual("m.notice", body.RootElement.GetProperty("msgtype").GetString());
		Assert.AreEqual("plain <text>", body.RootElement.GetProperty("body").GetString());
		Assert.AreEqual("org.matrix.custom.html", body.RootElement.GetProperty("format").GetString());
		Assert.AreEqual("<b>html</b>", body.RootElement.GetProperty("formatted_body").GetString());
	}

	[TestMethod]
	internal async Task TransactionIdIsUrlEncoded() {
		using Rig rig = new();
		(MatrixChannel channel, FakeHandler handler, _, _) = rig;
		handler.Enqueue(HttpStatusCode.OK);

		await channel.SendAsync(Message, "a/b", false, CancellationToken.None).ConfigureAwait(false);

		StringAssert.EndsWith(handler.Requests.Single().Uri, "/send/m.room.message/a%2Fb", StringComparison.Ordinal);
	}

	[TestMethod]
	[DataRow("""{"errcode": "M_LIMIT_EXCEEDED", "retry_after_ms": 2000}""", null, 2000)]
	[DataRow("""{"errcode": "M_LIMIT_EXCEEDED", "retry_after_ms": 120000}""", null, 60000)]
	[DataRow("""{"errcode": "M_LIMIT_EXCEEDED"}""", "7", 7000)]
	[DataRow("not json", "300", 60000)]
	[DataRow("""{"retry_after_ms": 2000}""", "7", 2000)]
	[DataRow("""{"errcode": "M_LIMIT_EXCEEDED"}""", null, -1)]
	[DataRow("""{"retry_after_ms": 1e20}""", null, 60000)]
	[DataRow("""{"retry_after_ms": -1e20}""", null, 0)]
	[DataRow("""{"retry_after_ms": 1e400}""", null, 60000)]
	[DataRow("""{"retry_after_ms": 1e400}""", "7", 60000)]
	internal async Task RateLimitMapsToRetryAfterCappedAtSixtySeconds(string body, string? retryAfterHeader, int expectedMs) {
		using Rig rig = new();
		(MatrixChannel channel, FakeHandler handler, _, _) = rig;
		handler.Enqueue((HttpStatusCode) 429, body, response => {
			if (retryAfterHeader != null) {
				response.Headers.TryAddWithoutValidation("Retry-After", retryAfterHeader);
			}
		});

		SendResult result = await Send(channel).ConfigureAwait(false);

		Assert.AreEqual(SendOutcome.RetryAfter, result.Outcome);
		Assert.AreEqual(expectedMs < 0 ? null : TimeSpan.FromMilliseconds(expectedMs), result.Delay);
	}

	// The fake clock reads 2026-01-01 00:00:00 UTC
	[TestMethod]
	[DataRow("Thu, 01 Jan 2026 00:00:30 GMT", 30000)]
	[DataRow("Thu, 01 Jan 2026 01:00:00 GMT", 60000)]
	[DataRow("Wed, 31 Dec 2025 23:59:00 GMT", 0)]
	[DataRow("soon", -1)]
	internal async Task RetryAfterHttpDateCountsFromNow(string retryAfterHeader, int expectedMs) {
		using Rig rig = new();
		(MatrixChannel channel, FakeHandler handler, _, _) = rig;
		handler.Enqueue((HttpStatusCode) 429, """{"errcode": "M_LIMIT_EXCEEDED"}""", response => response.Headers.TryAddWithoutValidation("Retry-After", retryAfterHeader));

		SendResult result = await Send(channel).ConfigureAwait(false);

		Assert.AreEqual(SendOutcome.RetryAfter, result.Outcome);
		Assert.AreEqual(expectedMs < 0 ? null : TimeSpan.FromMilliseconds(expectedMs), result.Delay);
	}

	[TestMethod]
	internal async Task ServerErrorIsTransient() {
		using Rig rig = new();
		(MatrixChannel channel, FakeHandler handler, _, _) = rig;
		handler.Enqueue(HttpStatusCode.InternalServerError, """{"errcode": "M_UNKNOWN"}""");

		SendResult result = await Send(channel).ConfigureAwait(false);

		Assert.AreEqual(SendOutcome.Transient, result.Outcome);
		Assert.AreEqual("500 M_UNKNOWN", result.Error);
	}

	[TestMethod]
	internal async Task NetworkErrorIsTransient() {
		using Rig rig = new();
		(MatrixChannel channel, FakeHandler handler, _, _) = rig;
		handler.EnqueueThrow(new HttpRequestException("connection refused to https://matrix.example.org"));

		SendResult result = await Send(channel).ConfigureAwait(false);

		Assert.AreEqual(SendOutcome.Transient, result.Outcome);
		Assert.AreEqual("network error", result.Error);
	}

	[TestMethod]
	internal async Task TimeoutIsTransient() {
		using Rig rig = new();
		(MatrixChannel channel, FakeHandler handler, _, _) = rig;
		handler.EnqueueThrow(new TaskCanceledException("timed out", new TimeoutException()));

		SendResult result = await Send(channel).ConfigureAwait(false);

		Assert.AreEqual(SendOutcome.Transient, result.Outcome);
		Assert.AreEqual("timeout", result.Error);
	}

	[TestMethod]
	internal async Task CallerCancellationPropagates() {
		using Rig rig = new();
		(MatrixChannel channel, FakeHandler handler, _, _) = rig;
		handler.EnqueueThrow(new TaskCanceledException());
		using CancellationTokenSource cts = new();
		await cts.CancelAsync().ConfigureAwait(false);

		await Assert.ThrowsAsync<TaskCanceledException>(() => Send(channel, false, cts.Token)).ConfigureAwait(false);
	}

	[TestMethod]
	[DataRow(HttpStatusCode.Unauthorized, "M_UNKNOWN_TOKEN", "401 M_UNKNOWN_TOKEN")]
	[DataRow(HttpStatusCode.Forbidden, "M_FORBIDDEN", "403 M_FORBIDDEN")]
	internal async Task AuthFailureDropsAndDegradesWithOneErrorLine(HttpStatusCode status, string errcode, string expectedError) {
		using Rig rig = new();
		(MatrixChannel channel, FakeHandler handler, FakeLog log, _) = rig;
		handler.Enqueue(status, $$"""{"errcode": "{{errcode}}", "error": "nope"}""");

		SendResult result = await Send(channel).ConfigureAwait(false);

		Assert.AreEqual(SendOutcome.Dropped, result.Outcome);
		Assert.AreEqual(expectedError, result.Error);
		Assert.HasCount(1, log.Errors);
		StringAssert.Contains(log.Errors[0], expectedError, StringComparison.Ordinal);

		if (status == HttpStatusCode.Forbidden) {
			StringAssert.Contains(log.Errors[0], TestData.RoomId, StringComparison.Ordinal);
		}
	}

	[TestMethod]
	internal async Task DegradedGateRecoveryAndLogOnlyOnStateChange() {
		using Rig rig = new();
		(MatrixChannel channel, FakeHandler handler, FakeLog log, FakeClock time) = rig;
		handler.Enqueue(HttpStatusCode.Unauthorized, """{"errcode": "M_UNKNOWN_TOKEN"}""");
		await Send(channel).ConfigureAwait(false);

		time.Advance(TimeSpan.FromMinutes(9) + TimeSpan.FromSeconds(59));
		SendResult gated = await Send(channel).ConfigureAwait(false);

		Assert.AreEqual(SendOutcome.Dropped, gated.Outcome);
		Assert.HasCount(1, handler.Requests);

		time.Advance(TimeSpan.FromSeconds(1));
		handler.Enqueue(HttpStatusCode.Unauthorized, """{"errcode": "M_UNKNOWN_TOKEN"}""");
		await Send(channel).ConfigureAwait(false);

		Assert.HasCount(2, handler.Requests);
		Assert.HasCount(1, log.Errors);

		time.Advance(TimeSpan.FromMinutes(5));
		await Send(channel).ConfigureAwait(false);
		Assert.HasCount(2, handler.Requests);

		time.Advance(TimeSpan.FromMinutes(5));
		handler.Enqueue(HttpStatusCode.OK);
		SendResult recovered = await Send(channel).ConfigureAwait(false);

		Assert.AreEqual(SendOutcome.Sent, recovered.Outcome);
		Assert.HasCount(3, handler.Requests);
		Assert.HasCount(1, log.Errors);
		Assert.HasCount(1, log.Infos);

		handler.Enqueue(HttpStatusCode.OK);
		await Send(channel).ConfigureAwait(false);
		Assert.HasCount(1, log.Infos);
	}

	[TestMethod]
	internal async Task BypassAlwaysMakesTheRequestWhileDegraded() {
		using Rig rig = new();
		(MatrixChannel channel, FakeHandler handler, _, _) = rig;
		handler.Enqueue(HttpStatusCode.Forbidden, """{"errcode": "M_FORBIDDEN"}""");
		await Send(channel).ConfigureAwait(false);
		handler.Enqueue(HttpStatusCode.OK);

		SendResult result = await Send(channel, true).ConfigureAwait(false);

		Assert.AreEqual(SendOutcome.Sent, result.Outcome);
		Assert.HasCount(2, handler.Requests);
	}

	[TestMethod]
	internal async Task OtherClientErrorIsDroppedWithWarningAndDoesNotDegrade() {
		using Rig rig = new();
		(MatrixChannel channel, FakeHandler handler, FakeLog log, _) = rig;
		handler.Enqueue(HttpStatusCode.BadRequest, """{"errcode": "M_BAD_JSON"}""");
		handler.Enqueue(HttpStatusCode.OK);

		SendResult result = await Send(channel).ConfigureAwait(false);
		SendResult next = await Send(channel).ConfigureAwait(false);

		Assert.AreEqual(SendOutcome.Dropped, result.Outcome);
		Assert.AreEqual("400 M_BAD_JSON", result.Error);
		Assert.HasCount(1, log.Warnings);
		Assert.IsEmpty(log.Errors);
		Assert.AreEqual(SendOutcome.Sent, next.Outcome);
	}

	[TestMethod]
	internal async Task TokenNeverAppearsInLogsOrResultsOnAnyErrorPath() {
		using Rig rig = new();
		(MatrixChannel channel, FakeHandler handler, FakeLog log, FakeClock time) = rig;
		string reflected = $$"""{"errcode": "{{TestData.Token}}", "error": "bad token {{TestData.Token}}", "retry_after_ms": 10}""";
		HttpStatusCode[] statuses = [HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden, (HttpStatusCode) 429, HttpStatusCode.InternalServerError, HttpStatusCode.BadRequest];
		SendResult[] results = new SendResult[statuses.Length + 2];

		for (int i = 0; i < statuses.Length; i++) {
			handler.Enqueue(statuses[i], reflected);
			results[i] = await Send(channel, true).ConfigureAwait(false);
		}

		handler.EnqueueThrow(new HttpRequestException($"failed with {TestData.Token}"));
		results[statuses.Length] = await Send(channel, true).ConfigureAwait(false);
		handler.EnqueueThrow(new TaskCanceledException($"{TestData.Token}", new TimeoutException()));
		results[statuses.Length + 1] = await Send(channel, true).ConfigureAwait(false);
		time.Advance(TimeSpan.FromHours(1));
		handler.Enqueue(HttpStatusCode.OK);
		await Send(channel).ConfigureAwait(false);

		Assert.IsNotEmpty(log.All);
		Assert.IsFalse(log.All.Any(static line => line.Contains(TestData.Token, StringComparison.Ordinal)));
		Assert.IsFalse(results.Any(static result => result.Error?.Contains(TestData.Token, StringComparison.Ordinal) == true));
	}
}
#pragma warning restore CA1812 // MSTest uses this class during runtime
