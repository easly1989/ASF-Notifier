using System;
using System.Threading.Tasks;
using ArchiSteamFarm.Core;
using ArchiSteamFarm.Plugins.Interfaces;
using JetBrains.Annotations;

namespace ASFNotifier;

// ASF exports every IPlugin type by convention, no [Export] attribute needed
#pragma warning disable CA1812 // ASF uses this class during runtime
[UsedImplicitly]
internal sealed class ASFNotifier : IGitHubPluginUpdates {
	public string Name => nameof(ASFNotifier);
	public string RepositoryName => "easly1989/ASF-Notifier";
	public Version Version => typeof(ASFNotifier).Assembly.GetName().Version ?? throw new InvalidOperationException(nameof(Version));

	public Task OnLoaded() {
		ASF.ArchiLogger.LogGenericInfo($"{Name} {Version} loaded");

		return Task.CompletedTask;
	}
}
#pragma warning restore CA1812 // ASF uses this class during runtime
