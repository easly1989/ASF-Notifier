using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ASFNotifier.Tests;

[TestClass]
public sealed class PluginTests {
	[TestMethod]
	public void Plugin_Identifies_Itself() {
		ASFNotifier plugin = new();

		Assert.AreEqual("ASFNotifier", plugin.Name);
		Assert.AreEqual("easly1989/ASF-Notifier", plugin.RepositoryName);
		Assert.IsGreaterThan(0, plugin.Version.Major);
	}
}
