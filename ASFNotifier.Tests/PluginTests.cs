using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ASFNotifier.Tests;

#pragma warning disable CA1812 // MSTest uses this class during runtime
[TestClass]
internal sealed class PluginTests {
	[TestMethod]
	internal void PluginIdentifiesItself() {
		using ASFNotifier plugin = new();

		Assert.AreEqual("ASFNotifier", plugin.Name);
		Assert.AreEqual("easly1989/ASF-Notifier", plugin.RepositoryName);
		Assert.IsGreaterThan(0, plugin.Version.Major);
	}
}
#pragma warning restore CA1812 // MSTest uses this class during runtime
