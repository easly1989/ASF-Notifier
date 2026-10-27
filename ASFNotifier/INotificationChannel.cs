using System;
using System.Threading;
using System.Threading.Tasks;

namespace ASFNotifier;

internal enum SendOutcome : byte {
	Sent,
	RetryAfter,
	Transient,
	Dropped
}

internal sealed record SendResult(SendOutcome Outcome, TimeSpan? Delay = null, string? Error = null) {
	internal static SendResult Dropped(string error) => new(SendOutcome.Dropped, null, error);
	internal static SendResult RetryAfter(TimeSpan? delay, string error) => new(SendOutcome.RetryAfter, delay, error);
	internal static SendResult Sent() => new(SendOutcome.Sent);
	internal static SendResult Transient(string error) => new(SendOutcome.Transient, null, error);
}

// Test seam for MatrixChannel; the transaction ID is made once per notification and reused on retries
internal interface INotificationChannel {
	Task<SendResult> SendAsync(FormattedMessage message, string transactionId, bool bypassDegraded, CancellationToken cancellationToken);
}
