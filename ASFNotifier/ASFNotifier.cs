using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ArchiSteamFarm.Core;
using ArchiSteamFarm.Plugins.Interfaces;
using ArchiSteamFarm.Steam;
using JetBrains.Annotations;

namespace ASFNotifier;

// ASF exports every IPlugin type by convention, no [Export] attribute needed
#pragma warning disable CA1812 // ASF uses this class during runtime
[UsedImplicitly]
internal sealed class ASFNotifier : IASF, IBotCommand2, IDisposable, IGitHubPluginUpdates {
	private NotifyTestCommand? NotifyTest;
	private SendQueue? Queue;

	public string Name => nameof(ASFNotifier);
	public string RepositoryName => "easly1989/ASF-Notifier";
	public Version Version => typeof(ASFNotifier).Assembly.GetName().Version ?? throw new InvalidOperationException(nameof(Version));

	public Task OnASFInit(IReadOnlyDictionary<string, JsonElement>? additionalConfigProperties = null) {
		try {
			// A repeated init must not leave the previous queue and its handler behind
			AppDomain.CurrentDomain.ProcessExit -= OnProcessExit;
			Queue?.Dispose();
			Queue = null;

			NotifierSettings settings = NotifierSettings.Parse(additionalConfigProperties, Environment.GetEnvironmentVariable(NotifierSettings.EnvironmentTokenVariable), new AsfLog());
			MatrixChannel? channel = null;

			if (settings is { IsConfigured: true, HomeserverUrl: { } homeserver, RoomId: { } roomId, AccessToken: { } token }) {
				channel = new MatrixChannel(MatrixChannel.CreateHttpClient(), homeserver, roomId, token, TimeProvider.System, new AsfLog());
				Queue = new SendQueue(channel, new AsfLog(), TimeProvider.System);

				// ASF has no shutdown hook; it exits through Environment.Exit, which raises ProcessExit
				AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
			}

			NotifyTest = new NotifyTestCommand(settings, channel, TimeProvider.System, null);
		} catch (Exception e) {
			ASF.ArchiLogger.LogGenericException(e);
		}

		return Task.CompletedTask;
	}

	public void Dispose() {
		AppDomain.CurrentDomain.ProcessExit -= OnProcessExit;
		Queue?.Dispose();
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

	public Task OnLoaded() {
		ASF.ArchiLogger.LogGenericInfo($"{Name} {Version} loaded");

		return Task.CompletedTask;
	}

	private void OnProcessExit(object? sender, EventArgs e) => Queue?.Flush();

	internal static bool IsMasterOrHigher(EAccess access) => access >= EAccess.Master;

	private sealed class AsfLog : INotifierLog {
		public void Error(string message) => ASF.ArchiLogger.LogGenericError(message, nameof(ASFNotifier));
		public void Info(string message) => ASF.ArchiLogger.LogGenericInfo(message, nameof(ASFNotifier));
		public void Warning(string message) => ASF.ArchiLogger.LogGenericWarning(message, nameof(ASFNotifier));
	}
}
#pragma warning restore CA1812 // ASF uses this class during runtime
