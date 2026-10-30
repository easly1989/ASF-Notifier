using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NLog;
using NLog.Config;
using NLog.Targets;
using SteamKit2;

namespace ASFNotifier;

internal interface IGiveUpLog {
	void Forget(string botName);
	(TapEntry? GiveUpError, TapEntry? Reason) Get(string botName);
}

// Spec section 5.3: records, per bot logger, the latest ASF give-up error and the latest login or logged-off reason.
// It keeps time, method and parsed EResult only; log text is never stored or logged again. With onError, it also hands
// Error and Fatal entries to the AsfError forwarder (section 5.6), off the logging thread and only while wantErrors says
// AsfError can be on.
internal sealed class GiveUpLogTap : IDisposable, IGiveUpLog {
	internal const string RuleName = "ASFNotifierTap";

	private const string OnLoggedOff = nameof(OnLoggedOff);

	// ASF's Stop() call sites that log an Error first (spec fact 3)
	internal static readonly FrozenSet<string> GiveUpMethods = new[] { StopClassifier.HandleLoginResult, OnLoggedOff, "OnLoggedOn", "OnConnected", "RequestInput" }.ToFrozenSet(StringComparer.Ordinal);

	private readonly Action<Action> Defer;
	private readonly Dictionary<string, (TapEntry? GiveUpError, TapEntry? Reason)> Entries = new(StringComparer.Ordinal);
	private readonly LogFactory Factory;
	private readonly Lock Lock = new();

	// Taken only outside NLog's own lock, and always before it: NLog raises ConfigurationChanged while holding it
	private readonly Lock RegistrationLock = new();

	private readonly Clock Clock;
	private readonly Action<LoggedError>? OnError;
	private readonly Func<bool>? WantErrors;

	private TapTarget? CurrentTarget;
	private bool Disposed;

	// defer is a test seam; production hands the re-registration and onError to the thread pool. wantErrors runs on the
	// logging thread for each Error and Fatal entry, so it must be cheap; null means always
	internal GiveUpLogTap(LogFactory factory, Clock clock, Action<Action>? defer = null, Action<LoggedError>? onError = null, Func<bool>? wantErrors = null) {
		ArgumentNullException.ThrowIfNull(factory);

		Factory = factory;
		Clock = clock;
		OnError = onError;
		WantErrors = wantErrors;
		// Task.Run, not ThreadPool: the trimmed ASF has no System.Threading.ThreadPool facade (issue #23)
		Defer = defer ?? (static work => Task.Run(work));

		// ASF or the user can replace the NLog configuration at runtime; the tap follows it
		Factory.ConfigurationChanged += OnConfigurationChanged;
		Register(Factory.Configuration);
	}

	public void Dispose() {
		Factory.ConfigurationChanged -= OnConfigurationChanged;

		lock (RegistrationLock) {
			lock (Lock) {
				if (Disposed) {
					return;
				}

				Disposed = true;
				Entries.Clear();
			}

			LoggingConfiguration? configuration = Factory.Configuration;

			if ((configuration != null) && (RemoveRules(configuration) > 0)) {
				Factory.ReconfigExistingLoggers();
			}

			CurrentTarget?.Dispose();
			CurrentTarget = null;
		}
	}

	public void Forget(string botName) {
		lock (Lock) {
			Entries.Remove(botName);
		}
	}

	public (TapEntry? GiveUpError, TapEntry? Reason) Get(string botName) {
		lock (Lock) {
			return Entries.GetValueOrDefault(botName);
		}
	}

	// Called by NLog for every Info-or-above entry of every logger, on the logging thread: stays cheap and never throws
	private void Record(string? loggerName, LogLevel level, string? message, Exception? exception) {
		if ((loggerName == null) || (message == null)) {
			return;
		}

		if ((OnError is { } onError) && ((level == LogLevel.Error) || (level == LogLevel.Fatal))) {
			HandOff(onError, loggerName, message, exception);
		}

		int end = message.IndexOf("() ", StringComparison.Ordinal);

		if (end <= 0) {
			return;
		}

		string method = message[..end];
		bool giveUp = (level == LogLevel.Error) && GiveUpMethods.Contains(method);
		bool reasonSource = method is StopClassifier.HandleLoginResult or OnLoggedOff;

		if (!giveUp && !reasonSource) {
			return;
		}

		EResult? reason = StopClassifier.ParseReason(message[(end + 3)..]);
		TapEntry entry = new(Clock.GetUtcNow(), method, reason);

		lock (Lock) {
			if (Disposed) {
				return;
			}

			(TapEntry? GiveUpError, TapEntry? Reason) current = Entries.GetValueOrDefault(loggerName);

			Entries[loggerName] = (giveUp ? entry : current.GiveUpError, reasonSource && (reason != null) ? entry : current.Reason);
		}
	}

	// NLog raises this inside its configuration lock (LogFactory.Configuration's setter in 6.2.1): touching NLog
	// from here could deadlock against another thread, so only hand the work off
	private void OnConfigurationChanged(object? sender, LoggingConfigurationChangedEventArgs e) {
		LoggingConfiguration? activated = e.ActivatedConfiguration;

		if (activated != null) {
			Defer(() => Register(activated));
		}
	}

	private void Register(LoggingConfiguration? configuration) {
		if (configuration == null) {
			return;
		}

		lock (RegistrationLock) {
			lock (Lock) {
				if (Disposed) {
					return;
				}
			}

			// Deferred work can arrive after yet another reload; that reload registers on its own
			if (!ReferenceEquals(Factory.Configuration, configuration)) {
				return;
			}

			// A fresh target each time: the configuration being replaced closes the targets it held
			TapTarget? previous = CurrentTarget;
			CurrentTarget = new TapTarget(this);
			RemoveRules(configuration);
			configuration.LoggingRules.Insert(0, new LoggingRule("*", LogLevel.Info, LogLevel.Fatal, CurrentTarget) { RuleName = RuleName });
			Factory.ReconfigExistingLoggers();
			previous?.Dispose();
		}
	}

	// Only references are captured here; the exception's text is read later, off the logging thread. Deferred, because
	// emitting can log (a full queue) and logging from inside NLog's Write must not happen. A failure here is swallowed:
	// it must never cost the give-up record the stop classification needs
	private void HandOff(Action<LoggedError> onError, string loggerName, string message, Exception? exception) {
		try {
			if ((WantErrors?.Invoke() == false) || IsDisposed()) {
				return;
			}

			LoggedError error = new(loggerName, message, Clock.GetUtcNow(), exception);

			Defer(() => {
				if (!IsDisposed()) {
					onError(error);
				}
			});
		} catch (Exception) {
			// The target must not throw into NLog
		}
	}

	private bool IsDisposed() {
		lock (Lock) {
			return Disposed;
		}
	}

	private static int RemoveRules(LoggingConfiguration configuration) {
		int removed = 0;

		for (int i = configuration.LoggingRules.Count - 1; i >= 0; i--) {
			if (configuration.LoggingRules[i].RuleName == RuleName) {
				configuration.LoggingRules.RemoveAt(i);
				removed++;
			}
		}

		return removed;
	}

	private sealed class TapTarget : Target {
		private readonly GiveUpLogTap Tap;

		internal TapTarget(GiveUpLogTap tap) {
			Tap = tap;
			Name = RuleName;
		}

		protected override void Write(LogEventInfo logEvent) => Tap.Record(logEvent.LoggerName, logEvent.Level, logEvent.FormattedMessage, logEvent.Exception);
	}
}
