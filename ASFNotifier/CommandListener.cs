using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ASFNotifier;

// BotFound false: ASF has no bot to run the command on
internal readonly record struct CommandRun(bool BotFound, string? Response);

// What the listener needs of ASF; the adapter implements it, tests fake it
internal interface ICommandHost {
	// ASF's CommandPrefix; null or empty: only the mention form is a command
	string? CommandPrefix { get; }

	string? LoadSyncToken();

	// Through the filter, the rate limiter and the send queue; false when it was not queued
	bool Reply(Notification reply);

	// Runs the command through ASF's own Commands.Response, so ASF's access checks apply
	Task<CommandRun> RunAsync(CommandAccess access, string command);

	void SaveSyncToken(string token);
}

// Spec section 9.1: long-polls the command room on a background task and runs the commands of allowed senders.
// Nothing here runs inside an ASF hook
internal sealed class CommandListener : IDisposable {
	internal const int MaxReplyLength = 4000;
	internal const int RefusedSenderLimit = 16;
	internal const int SenderLimit = 5;
	internal static readonly TimeSpan CommandTimeout = TimeSpan.FromMinutes(2);
	internal static readonly TimeSpan MaxClockSkew = TimeSpan.FromMinutes(1);
	internal static readonly TimeSpan MaxCommandAge = TimeSpan.FromMinutes(5);
	internal static readonly TimeSpan MinRetryAfter = TimeSpan.FromSeconds(1);
	internal static readonly TimeSpan PollTimeout = TimeSpan.FromSeconds(30);
	internal static readonly TimeSpan RefusedLogWindow = TimeSpan.FromMinutes(10);
	internal static readonly TimeSpan SenderWindow = TimeSpan.FromMinutes(1);
	internal static readonly TimeSpan UnauthorizedRetry = TimeSpan.FromMinutes(10);

	// ASF registers its bots after OnASFInit; a command replayed at start-up should find them
	internal static readonly TimeSpan StartDelay = TimeSpan.FromSeconds(10);

	private const int MaxLoggedLength = 64;
	private const string NoAnswerHint = "No answer from ASF: check the bot name, or your access level may be too low for this command.";
	private const string Redacted = "[redacted]";

	private readonly string AccessToken;
	private readonly MatrixSyncClient Client;
	private readonly Clock Clock;
	private readonly Func<TimeSpan, CancellationToken, Task> Delay;
	private readonly INotifierLog Log;
	private readonly ICommandHost Host;
	private readonly Dictionary<string, long> RefusedLogged = new(StringComparer.Ordinal);
	private readonly string RoomId;
	private readonly Dictionary<string, Queue<long>> SenderSlots = new(StringComparer.Ordinal);
	private readonly CancellationTokenSource Stop = new();
	private readonly IReadOnlyDictionary<string, CommandAccess> Users;

	private bool AuthFailed;
	private int Failures;
	private bool Outage;
	private string? Since;
	private Task? Worker;

	internal string? BotUserId { get; private set; }

	// accessToken only redacts: an answer or a log line that would contain it is withheld. delay is a test seam
	internal CommandListener(MatrixSyncClient client, string roomId, IReadOnlyDictionary<string, CommandAccess> users, string accessToken, ICommandHost host, Clock clock, INotifierLog log, Func<TimeSpan, CancellationToken, Task>? delay = null) {
		Client = client;
		RoomId = roomId;
		Users = users;
		AccessToken = accessToken;
		Host = host;
		Clock = clock;
		Log = log;
		Delay = delay ?? Task.Delay;
	}

	// Never waits: a long poll or a running command is abandoned, and the process may be exiting
	public void Dispose() {
		if (!Stop.IsCancellationRequested) {
			_ = Stop.CancelAsync();
		}

		// Cancelled first, so the loop treats the aborted poll as the stop it is
		Client.Dispose();

		if (Worker?.IsCompleted != false) {
			Stop.Dispose();
		}
	}

	// One message of the room, read at the received timestamp; never throws for a bad message, only when cancelled
	internal async Task HandleAsync(RoomMessage message, long received, CancellationToken cancellationToken) {
		ArgumentNullException.ThrowIfNull(message);

		// The bot's own notices and replies, and anything that is not plain text (emotes, files, notices of other bots)
		if ((BotUserId == null) || (message.Sender == BotUserId) || (message.MsgType != "m.text")) {
			return;
		}

		if (CommandParser.Parse(message.Body, Host.CommandPrefix, BotUserId, message.FormattedBody) is not { } command) {
			return;
		}

		if (!Users.TryGetValue(message.Sender, out CommandAccess access)) {
			if (TakeRefusedLogSlot(message.Sender)) {
				Audit(message, command, $"refused, sender not in the allow-list (not logged again for {RefusedLogWindow.TotalMinutes:0} minutes)", true);
			}

			return;
		}

		// After an outage, a restart or a slow command before it: what was asked minutes ago may no longer be wanted.
		// Nothing awaits between this check and the run, so it is the age the command runs at
		if (AgeOf(message, received) is not { } age) {
			Audit(message, command, "dropped, no valid age (unknown or dated in the future)");

			return;
		}

		if (age > MaxCommandAge) {
			Audit(message, command, $"dropped, older than {MaxCommandAge.TotalMinutes:0} minutes");

			return;
		}

		if (!TakeSenderSlot(message.Sender)) {
			Audit(message, command, "dropped, sender rate limit", true);

			return;
		}

		if (CommandParser.IsDenied(command.Verb)) {
			bool sent = Reply(message, "This command cannot be run from Matrix.");
			Audit(message, command, "refused, not allowed from Matrix" + (sent ? "" : ", reply not sent"), true);

			return;
		}

		// On the thread pool, so a command that blocks before its first await is bounded by the timeout too
		Task<CommandRun> running = Task.Run(() => Host.RunAsync(access, command.Text), CancellationToken.None);

		Task finished;

		using (CancellationTokenSource timer = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)) {
#pragma warning disable CA2025 // The delay only listens to the token, and it is cancelled before the source is disposed
			finished = await Task.WhenAny(running, Delay(CommandTimeout, timer.Token)).ConfigureAwait(false);
#pragma warning restore CA2025 // The delay only listens to the token, and it is cancelled before the source is disposed
			await timer.CancelAsync().ConfigureAwait(false);
		}

		if (finished != running) {
			// ASF's work is not cancelled; its answer is dropped when it comes, also when the listener is stopping
#pragma warning disable CA2025 // The command uses none of this method's disposables
			_ = Abandon(running);
#pragma warning restore CA2025 // The command uses none of this method's disposables
			cancellationToken.ThrowIfCancellationRequested();

			// The next command goes on
			bool sent = Reply(message, "Still running, the answer will not be posted.");
			Audit(message, command, $"still running after {CommandTimeout.TotalMinutes:0} minutes, answer not posted" + (sent ? "" : ", reply not sent"), true);

			return;
		}

		CommandRun run;

		try {
			run = await running.ConfigureAwait(false);
		} catch (Exception e) {
			bool sent = Reply(message, "The command failed, see the ASF log.");
			Audit(message, command, $"failed ({e.GetType().Name})" + (sent ? "" : ", reply not sent"), true);

			return;
		}

		if (!run.BotFound) {
			bool sent = Reply(message, "ASF has no bot to run the command on.");
			Audit(message, command, "not run, ASF has no bot" + (sent ? "" : ", reply not sent"));

			return;
		}

		if (string.IsNullOrWhiteSpace(run.Response)) {
			// ASF answers nothing for an unknown bot name or a level too low for the command, and Matrix users never see its reason
			bool hinted = Reply(message, NoAnswerHint);
			Audit(message, command, "ran, no answer" + (hinted ? " (hint sent)" : ", reply not sent"));

			return;
		}

		bool replied = Reply(message, run.Response);
		Audit(message, command, "ran" + (replied ? "" : ", reply not sent"));
	}

	// One round: the delay before the next one
	internal async Task<TimeSpan> PollOnceAsync(CancellationToken cancellationToken) {
		if (BotUserId == null) {
			SyncResult whoAmI = await Client.WhoAmIAsync(cancellationToken).ConfigureAwait(false);

			if (whoAmI.Outcome != SyncOutcome.Ok) {
				return Failed(whoAmI);
			}

			BotUserId = whoAmI.UserId;
			Since = Host.LoadSyncToken();
		}

		if (BotUserId == null) {
			return TimeSpan.Zero;
		}

		bool initial = Since == null;
		SyncResult sync = await Client.SyncAsync(RoomId, BotUserId, Since, initial ? TimeSpan.Zero : PollTimeout, cancellationToken).ConfigureAwait(false);

		if ((sync.Outcome == SyncOutcome.Rejected) && !initial) {
			Log.Warning($"The homeserver rejected the saved sync position ({sync.Error}); commands start from now");
			Since = null;

			return TimeSpan.Zero;
		}

		if ((sync.Outcome != SyncOutcome.Ok) || (sync.NextBatch == null)) {
			return Failed(sync);
		}

		long received = Clock.GetTimestamp();
		Recovered();
		Since = sync.NextBatch;

		// Saved before anything runs, so a crash in a command replays nothing. An empty batch changes nothing worth a write
		if (initial || (sync.EventCount > 0)) {
			Host.SaveSyncToken(sync.NextBatch);
		}

		// The first sync only finds the position: commands start from now
		if (!initial) {
			foreach (RoomMessage message in sync.Messages) {
				cancellationToken.ThrowIfCancellationRequested();

				try {
					await HandleAsync(message, received, cancellationToken).ConfigureAwait(false);
				} catch (Exception e) when (!cancellationToken.IsCancellationRequested) {
					// One bad message must not cost the rest of the batch, whose position is already saved
					LogUnexpected(e);
				}
			}
		}

		return TimeSpan.Zero;
	}

	internal void Start() => Worker ??= Task.Run(RunAsync);

	// Cut at a whole character, with a marker; '\r' dropped, so a Windows ASF answer has plain line breaks
	private static string Cap(string text) {
		StringBuilder capped = new(Math.Min(text.Length, MaxReplyLength));

		foreach (char c in text.Trim()) {
			if (c == '\r') {
				continue;
			}

			if ((capped.Length >= MaxReplyLength) || ((capped.Length == (MaxReplyLength - 1)) && char.IsHighSurrogate(c))) {
				return capped.Append("\n… (truncated)").ToString();
			}

			capped.Append(c);
		}

		return capped.ToString();
	}

	// Control characters out and length capped, so a crafted sender or verb cannot forge or flood log lines
	private static string Clean(string text) {
		StringBuilder clean = new(Math.Min(text.Length, MaxLoggedLength));

		foreach (char c in text) {
			if (clean.Length >= MaxLoggedLength) {
				return clean.Append('…').ToString();
			}

			clean.Append(char.IsControl(c) ? '?' : c);
		}

		return clean.ToString();
	}

	// Not awaited, so ASF's work goes on; a late failure must not surface as an unobserved task exception
	private static async Task Abandon(Task<CommandRun> running) {
		try {
			await running.ConfigureAwait(false);
		} catch (Exception) {
			// Dropped with its answer
		}
	}

	// unsigned.age is the homeserver's own count at the sync, immune to clock skew, plus the time since; origin_server_ts
	// is the fallback. null, so dropped: unknown, or dated more than MaxClockSkew in the future, which would otherwise
	// pass the age limit for as long as the date lies ahead
	private TimeSpan? AgeOf(RoomMessage message, long received) {
		TimeSpan? age = message.Age is { } reported ? reported + Clock.GetElapsedTime(received, Clock.GetTimestamp()) : OriginAge(message.OriginServerTs);

		return age < -MaxClockSkew ? null : age;
	}

	private TimeSpan? OriginAge(double? sent) {
		if (sent is not { } milliseconds) {
			return null;
		}

		double seconds = Clock.GetUtcNow().ToUnixTimeSeconds() - Math.Floor(milliseconds / 1000);

		// A timestamp TimeSpan cannot hold is nonsense
		return Math.Abs(seconds) < 1e9 ? TimeSpan.FromSeconds(seconds) : null;
	}

	// Sender, verb and outcome only: never the arguments, never the token
	private void Audit(RoomMessage message, ParsedCommand command, string outcome, bool warning = false) {
		string verb = command.Verb.Contains(AccessToken, StringComparison.OrdinalIgnoreCase) ? Redacted : Clean(command.Verb);
		string sender = message.Sender.Contains(AccessToken, StringComparison.Ordinal) ? Redacted : Clean(message.Sender);
		string line = $"Matrix command from {sender}: {verb} → {outcome}";

		if (warning) {
			Log.Warning(line);
		} else {
			Log.Info(line);
		}
	}

	private TimeSpan Failed(SyncResult result) {
		string error = result.Error ?? "unknown error";

		switch (result.Outcome) {
			case SyncOutcome.Unauthorized:
				if (!AuthFailed) {
					AuthFailed = true;
					Log.Error($"Matrix rejected the access token or the room for commands ({error}); retrying every {UnauthorizedRetry.TotalMinutes:0} minutes");
				}

				return UnauthorizedRetry;
			case SyncOutcome.RetryAfter:
				return TimeSpan.FromTicks(Math.Clamp((result.Delay ?? SendQueue.Backoff(Failures + 1)).Ticks, MinRetryAfter.Ticks, MatrixChannel.MaxRetryAfter.Ticks));
			default:
				Failures++;

				if (!Outage) {
					Outage = true;
					Log.Warning($"Reading the command room failed ({error}); retrying with backoff");
				}

				return SendQueue.Backoff(Failures);
		}
	}

	// Type and first stack frame only, as in SendQueue: an exception message could carry request details
	private void LogUnexpected(Exception e) {
		try {
			string? frame = e.StackTrace?.Split((char) 10)[0].Trim();

			Log.Error($"Unexpected {e.GetType().Name} in the command listener{(string.IsNullOrEmpty(frame) ? "" : $" {frame}")}");
		} catch (Exception) {
			// The logger itself failed; nothing left to do
		}
	}

	private void Recovered() {
		if (Outage || AuthFailed) {
			Log.Info("Reading the command room works again");
		}

		Outage = false;
		AuthFailed = false;
		Failures = 0;
	}

	private bool Reply(RoomMessage message, string text) {
		// No ASF command prints the token, but an answer that would is withheld, never sent
		string reply = text.Contains(AccessToken, StringComparison.Ordinal) ? "The answer was withheld: it contained the Matrix access token." : Cap(text);

		return Host.Reply(new Notification(NotificationKind.CommandReply, null, Clock.GetUtcNow(), Text: reply, InReplyTo: message.EventId, RoomId: RoomId));
	}

	private async Task RunAsync() {
		CancellationToken token = Stop.Token;

		try {
			await Delay(StartDelay, token).ConfigureAwait(false);
		} catch (OperationCanceledException) {
			return;
		}

		while (!token.IsCancellationRequested) {
			TimeSpan wait;

			try {
				wait = await PollOnceAsync(token).ConfigureAwait(false);
			} catch (Exception) when (token.IsCancellationRequested) {
				// Stopping: the aborted poll may also end with the disposed HttpClient's exception
				return;
			} catch (Exception e) {
				LogUnexpected(e);
				wait = SendQueue.Backoff(++Failures);
			}

			if (wait <= TimeSpan.Zero) {
				continue;
			}

			try {
				await Delay(wait, token).ConfigureAwait(false);
			} catch (OperationCanceledException) {
				return;
			}
		}
	}

	// A refused sender is logged once per RefusedLogWindow, and at most RefusedSenderLimit senders per window, so
	// strangers cannot flood ASF's log
	private bool TakeRefusedLogSlot(string sender) {
		long now = Clock.GetTimestamp();

		foreach (KeyValuePair<string, long> logged in RefusedLogged) {
			if (Clock.GetElapsedTime(logged.Value, now) >= RefusedLogWindow) {
				RefusedLogged.Remove(logged.Key);
			}
		}

		if (RefusedLogged.ContainsKey(sender) || (RefusedLogged.Count >= RefusedSenderLimit)) {
			return false;
		}

		RefusedLogged[sender] = now;

		return true;
	}

	// Per sender, at most SenderLimit commands in any rolling SenderWindow
	private bool TakeSenderSlot(string sender) {
		long now = Clock.GetTimestamp();

		if (!SenderSlots.TryGetValue(sender, out Queue<long>? slots)) {
			slots = new Queue<long>();
			SenderSlots[sender] = slots;
		}

		while ((slots.Count > 0) && (Clock.GetElapsedTime(slots.Peek(), now) >= SenderWindow)) {
			slots.Dequeue();
		}

		if (slots.Count >= SenderLimit) {
			return false;
		}

		slots.Enqueue(now);

		return true;
	}
}
