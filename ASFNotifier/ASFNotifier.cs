using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ArchiSteamFarm.Core;
using ArchiSteamFarm.Plugins.Interfaces;
using ArchiSteamFarm.Steam;
using ArchiSteamFarm.Storage;
using JetBrains.Annotations;
using NLog;
using SteamKit2;

namespace ASFNotifier;

// ASF exports every IPlugin type by convention, no [Export] attribute needed
#pragma warning disable CA1812 // ASF uses this class during runtime
[UsedImplicitly]
internal sealed class ASFNotifier : IASF, IBot, IBotCardsFarmerInfo, IBotCommand2, IBotConnection, IBotModules, IDisposable, IGitHubPluginUpdates, IWatcherHost {
	private readonly BotConfigStore BotConfigs = new();
	private readonly StopHold Hold = new();
	private readonly RateLimiter Limiter = new(TimeProvider.System);

	private ITimer? HoldTimer;
	private NotifyTestCommand? NotifyTest;
	private SendQueue? Queue;
	private NotifierSettings? Settings;
	private int ShutdownRequested;
	private bool SignalsRegistered;
	private PosixSignalRegistration? SigInt;
	private PosixSignalRegistration? SigTerm;
	private GiveUpLogTap? Tap;
	private ConnectionWatcher? Watcher;

	public string Name => nameof(ASFNotifier);
	public string RepositoryName => "easly1989/ASF-Notifier";
	public Version Version => typeof(ASFNotifier).Assembly.GetName().Version ?? throw new InvalidOperationException(nameof(Version));

	private bool IsShuttingDown => Volatile.Read(ref ShutdownRequested) != 0;

	public Task OnASFInit(IReadOnlyDictionary<string, JsonElement>? additionalConfigProperties = null) {
		try {
			// A repeated init must not leave the previous queue, tap and handlers behind
			AppDomain.CurrentDomain.ProcessExit -= OnProcessExit;
			Queue?.Dispose();
			Queue = null;
			Tap?.Dispose();
			Tap = null;
			Watcher?.Dispose();
			Watcher = null;
			HoldTimer?.Dispose();
			HoldTimer = null;

			NotifierSettings settings = NotifierSettings.Parse(additionalConfigProperties, Environment.GetEnvironmentVariable(NotifierSettings.EnvironmentTokenVariable), new AsfLog());
			MatrixChannel? channel = null;

			if (settings is { IsConfigured: true, HomeserverUrl: { } homeserver, RoomId: { } roomId, AccessToken: { } token }) {
				channel = new MatrixChannel(MatrixChannel.CreateHttpClient(), homeserver, roomId, token, TimeProvider.System, new AsfLog());
				Queue = new SendQueue(channel, new AsfLog(), TimeProvider.System);
				Tap = new GiveUpLogTap(LogManager.LogFactory, TimeProvider.System);
				Watcher = new ConnectionWatcher(Tap, () => IsShuttingDown, TimeProvider.System, TimeSpan.FromMinutes(settings.DisconnectedNotifyAfterMinutes), this);
				HoldTimer = TimeProvider.System.CreateTimer(_ => ReleaseHeldStops(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
				RegisterShutdownSignals();

				// ASF has no shutdown hook; it exits through Environment.Exit, which raises ProcessExit
				AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
			}

			Settings = settings;
			NotifyTest = new NotifyTestCommand(settings, channel, TimeProvider.System, null);

			// Last: Settings and Queue are ready for the Emit
			CheckAsfVersion();
		} catch (Exception e) {
			ASF.ArchiLogger.LogGenericException(e);
		}

		return Task.CompletedTask;
	}

	public void Dispose() {
		AppDomain.CurrentDomain.ProcessExit -= OnProcessExit;
		Console.CancelKeyPress -= OnCancelKeyPress;

		SigInt?.Dispose();
		SigTerm?.Dispose();
		HoldTimer?.Dispose();
		Watcher?.Dispose();
		Queue?.Dispose();
		Tap?.Dispose();
	}

	public Task OnBotFarmingFinished(Bot bot, bool farmedSomething) {
		ArgumentNullException.ThrowIfNull(bot);

		return EmitFarming(FarmingSource.OnFinished(bot.BotName, farmedSomething, TimeProvider.System.GetUtcNow()));
	}

	public Task OnBotFarmingStarted(Bot bot) {
		ArgumentNullException.ThrowIfNull(bot);

		return EmitFarming(FarmingSource.OnStarted(bot.BotName, TimeProvider.System.GetUtcNow()));
	}

	public Task OnBotFarmingStopped(Bot bot) {
		ArgumentNullException.ThrowIfNull(bot);

		return EmitFarming(FarmingSource.OnStopped(bot.BotName, TimeProvider.System.GetUtcNow()));
	}

	public async Task<string?> OnBotCommand(Bot bot, EAccess access, string message, string[] args, ulong steamID = 0) {
		ArgumentNullException.ThrowIfNull(bot);
		ArgumentNullException.ThrowIfNull(args);

		if (NotifyTest == null) {
			return null;
		}

		try {
			return await NotifyTest.HandleAsync(bot.BotName, args.Length > 0 ? args[0] : null, IsMasterOrHigher(access), CancellationToken.None).ConfigureAwait(false);
		} catch (Exception e) {
			ASF.ArchiLogger.LogGenericException(e);

			return null;
		}
	}

	public Task OnBotDestroy(Bot bot) {
		ArgumentNullException.ThrowIfNull(bot);

		try {
			BotConfigs.Remove(bot.BotName);
			Watcher?.OnDestroy(bot.BotName);
		} catch (Exception e) {
			ASF.ArchiLogger.LogGenericException(e);
		}

		return Task.CompletedTask;
	}

	// Runs before ASF decides whether to reconnect: classify, enqueue, return
	public Task OnBotDisconnected(Bot bot, EResult reason) {
		ArgumentNullException.ThrowIfNull(bot);

		try {
			ConnectionWatcher? watcher = Watcher;

			if (watcher == null) {
				return Task.CompletedTask;
			}

			bool keepRunning = bot.KeepRunning;
			string? requiredInput = RequiredInputOf(bot);
			Notification? notification = watcher.OnDisconnected(bot.BotName, reason, keepRunning, requiredInput);

			ASF.ArchiLogger.LogGenericDebug($"{bot.BotName} disconnected: reason {reason}, KeepRunning {keepRunning}, RequiredInput {requiredInput ?? "None"}, shutting down {IsShuttingDown} → {notification?.Kind.ToString() ?? "nothing"}{(notification?.Reason != null ? $" ({notification.Reason})" : "")}", nameof(ASFNotifier));

			Emit(notification);
		} catch (Exception e) {
			ASF.ArchiLogger.LogGenericException(e);
		}

		return Task.CompletedTask;
	}

	public Task OnBotInit(Bot bot) => Task.CompletedTask;

	// ASF calls this at bot creation and whenever the bot config file changes
	public Task OnBotInitModules(Bot bot, IReadOnlyDictionary<string, JsonElement>? additionalConfigProperties = null) {
		ArgumentNullException.ThrowIfNull(bot);

		try {
			BotConfigs.Replace(bot.BotName, additionalConfigProperties, new AsfLog());
		} catch (Exception e) {
			ASF.ArchiLogger.LogGenericException(e);
		}

		return Task.CompletedTask;
	}

	public Task OnBotLoggedOn(Bot bot) {
		ArgumentNullException.ThrowIfNull(bot);

		try {
			Emit(Watcher?.OnLoggedOn(bot.BotName));
		} catch (Exception e) {
			ASF.ArchiLogger.LogGenericException(e);
		}

		return Task.CompletedTask;
	}

	public Task OnLoaded() {
		ASF.ArchiLogger.LogGenericInfo($"{Name} {Version} loaded");

		return Task.CompletedTask;
	}

	internal static bool IsMasterOrHigher(EAccess access) => access >= EAccess.Master;

	// Disconnect timer, on a timer thread
	bool IWatcherHost.Emit(Notification notification) {
		bool accepted = Emit(notification);

		ASF.ArchiLogger.LogGenericDebug($"{notification.BotName} disconnect timer → {notification.Kind}{(notification.Reason != null ? $" ({notification.Reason})" : "")}{(accepted ? "" : ", not sent")}", nameof(ASFNotifier));

		return accepted;
	}

	BotStatus? IWatcherHost.GetStatus(string botName) => Bot.BotsReadOnly?.GetValueOrDefault(botName) is { } bot ? new BotStatus(bot.KeepRunning, bot.IsConnectedAndLoggedOn, RequiredInputOf(bot)) : null;

	void IWatcherHost.OnError(Exception exception) => ASF.ArchiLogger.LogGenericException(exception);

	// Spec section 5.5: the adapter around AsfVersionSource; the global database is loaded before plugins init
	private void CheckAsfVersion() {
		GlobalDatabase? database = ASF.GlobalDatabase;

		if (database == null) {
			return;
		}

		Version current = AsfVersionSource.RunningVersion;
		JsonElement storedElement = database.LoadFromJsonStorage(AsfVersionSource.StorageKey);

		// An undefined element means nothing stored; a non-string value is garbage and is treated as unparsable
		string? stored = storedElement.ValueKind switch {
			JsonValueKind.Undefined => null,
			JsonValueKind.String => storedElement.GetString(),
			_ => storedElement.GetRawText()
		};

		AsfVersionDecision decision = AsfVersionSource.Decide(stored, current);

		switch (decision.Action) {
			case AsfVersionAction.None:
				return;
			case AsfVersionAction.Updated when decision.Stored != null:
				Emit(AsfVersionSource.Updated(decision.Stored, current, TimeProvider.System.GetUtcNow()));

				break;
			case AsfVersionAction.Downgrade:
				ASF.ArchiLogger.LogGenericInfo($"ASF is older than the last seen version ({decision.Stored} → {current}), no message sent", nameof(ASFNotifier));

				break;
		}

		database.SaveToJsonStorage(AsfVersionSource.StorageKey, current.ToString());
	}

	// Hooks only enqueue; Emit never awaits network I/O
	private Task EmitFarming(Notification? notification) {
		try {
			Emit(notification);
		} catch (Exception e) {
			ASF.ArchiLogger.LogGenericException(e);
		}

		return Task.CompletedTask;
	}

	private static string? RequiredInputOf(Bot bot) => bot.RequiredInput == ASF.EUserInputType.None ? null : bot.RequiredInput.ToString();

	// true when accepted for sending (IWatcherHost.Emit). Stops are rate-checked when the hold releases them, so a stop
	// the hold drops never uses a dedupe window or a slot, and a released one is checked once
	private bool Emit(Notification? notification) {
		if ((notification == null) || (Settings == null) || !Filter.Allows(notification, Settings, BotConfigs.Get(notification.BotName))) {
			return false;
		}

		if (notification.Kind is NotificationKind.BotStopped or NotificationKind.BotStoppedByAsf) {
			Hold.Add(notification);
			HoldTimer?.Change(StopHold.HoldTime, Timeout.InfiniteTimeSpan);

			return true;
		}

		return Pass(notification);
	}

	private bool Pass(Notification notification) => Queue is { } queue && Limiter.Pass(notification, queue.Enqueue);

	// Timer callback: sends the held stops that are due unless ASF is going down, then re-arms for the rest
	private void ReleaseHeldStops() {
		try {
			DateTimeOffset now = TimeProvider.System.GetUtcNow();
			int runningBots = 0;

			foreach (Bot bot in Bot.BotsReadOnly?.Values ?? []) {
				if (bot.KeepRunning) {
					runningBots++;
				}
			}

			foreach (Notification notification in Hold.Release(now, IsShuttingDown, runningBots)) {
				Pass(notification);
			}

			if (Hold.NextDue is { } due) {
				HoldTimer?.Change(due > now ? due - now : TimeSpan.Zero, Timeout.InfiniteTimeSpan);
			}
		} catch (Exception e) {
			ASF.ArchiLogger.LogGenericException(e);
		}
	}

	// Neither cancels the signal: ASF's own handlers still shut it down
	private void OnCancelKeyPress(object? sender, ConsoleCancelEventArgs e) => Volatile.Write(ref ShutdownRequested, 1);

	private void OnPosixSignal(PosixSignalContext context) => Volatile.Write(ref ShutdownRequested, 1);

	private void OnProcessExit(object? sender, EventArgs e) {
		Volatile.Write(ref ShutdownRequested, 1);
		Queue?.Flush();
	}

	// docker stop and Ctrl+C stop every bot; the flag keeps those stops from becoming one message per bot
	private void RegisterShutdownSignals() {
		if (SignalsRegistered) {
			return;
		}

		SignalsRegistered = true;

		// The platforms ASF registers these signals on
		if (OperatingSystem.IsFreeBSD() || OperatingSystem.IsLinux() || OperatingSystem.IsMacOS()) {
			SigTerm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, OnPosixSignal);
			SigInt = PosixSignalRegistration.Create(PosixSignal.SIGINT, OnPosixSignal);
		}

		Console.CancelKeyPress += OnCancelKeyPress;
	}

	private sealed class AsfLog : INotifierLog {
		public void Error(string message) => ASF.ArchiLogger.LogGenericError(message, nameof(ASFNotifier));
		public void Info(string message) => ASF.ArchiLogger.LogGenericInfo(message, nameof(ASFNotifier));
		public void Warning(string message) => ASF.ArchiLogger.LogGenericWarning(message, nameof(ASFNotifier));
	}
}
#pragma warning restore CA1812 // ASF uses this class during runtime
