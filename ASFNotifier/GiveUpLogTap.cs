using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Threading;
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
// It keeps time, method and parsed EResult only; log text is never stored, forwarded or logged again.
internal sealed class GiveUpLogTap : IDisposable, IGiveUpLog {
	internal const string RuleName = "ASFNotifierTap";

	private const string OnLoggedOff = nameof(OnLoggedOff);

	// ASF's Stop() call sites that log an Error first (spec fact 3)
	private static readonly FrozenSet<string> GiveUpMethods = new[] { StopClassifier.HandleLoginResult, OnLoggedOff, "OnLoggedOn", "OnConnected", "RequestInput" }.ToFrozenSet(StringComparer.Ordinal);

	private readonly Action<Action> Defer;
	private readonly Dictionary<string, (TapEntry? GiveUpError, TapEntry? Reason)> Entries = new(StringComparer.Ordinal);
	private readonly LogFactory Factory;
	private readonly Lock Lock = new();

	// Taken only outside NLog's own lock, and always before it: NLog raises ConfigurationChanged while holding it
	private readonly Lock RegistrationLock = new();

	private readonly TimeProvider TimeProvider;

	private TapTarget? CurrentTarget;
	private bool Disposed;

	// defer is a test seam; production hands the re-registration to the thread pool
	internal GiveUpLogTap(LogFactory factory, TimeProvider timeProvider, Action<Action>? defer = null) {
		ArgumentNullException.ThrowIfNull(factory);

		Factory = factory;
		TimeProvider = timeProvider;
		Defer = defer ?? (static work => ThreadPool.QueueUserWorkItem(static state => state(), work, false));

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
	private void Record(string? loggerName, LogLevel level, string? message) {
		if ((loggerName == null) || (message == null)) {
			return;
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
		TapEntry entry = new(TimeProvider.GetUtcNow(), method, reason);

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

		protected override void Write(LogEventInfo logEvent) => Tap.Record(logEvent.LoggerName, logEvent.Level, logEvent.FormattedMessage);
	}
}
