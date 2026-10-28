using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ASFNotifier.Tests;

#pragma warning disable CA1812 // MSTest uses this class during runtime
[TestClass]
internal sealed class FarmingSourceTests {
	[TestMethod]
	internal void FinishedWithSomethingFarmedMapsToFarmingFinished() {
		Notification? notification = FarmingSource.OnFinished("bot1", true, DateTimeOffset.UnixEpoch);

		Assert.AreEqual(new Notification(NotificationKind.FarmingFinished, "bot1", DateTimeOffset.UnixEpoch), notification);
	}

	[TestMethod]
	internal void FinishedWithNothingFarmedIsIgnored() => Assert.IsNull(FarmingSource.OnFinished("bot1", false, DateTimeOffset.UnixEpoch));

	[TestMethod]
	internal void StartedMapsToFarmingStarted() =>
		Assert.AreEqual(new Notification(NotificationKind.FarmingStarted, "bot1", DateTimeOffset.UnixEpoch), FarmingSource.OnStarted("bot1", DateTimeOffset.UnixEpoch));

	[TestMethod]
	internal void StoppedMapsToFarmingStopped() =>
		Assert.AreEqual(new Notification(NotificationKind.FarmingStopped, "bot1", DateTimeOffset.UnixEpoch), FarmingSource.OnStopped("bot1", DateTimeOffset.UnixEpoch));

	[TestMethod]
	[DataRow(NotificationKind.FarmingFinished, "✅ bot1 — farming finished", "✅ <b>bot1</b> — farming finished")]
	[DataRow(NotificationKind.FarmingStarted, "▶️ bot1 — farming started", "▶️ <b>bot1</b> — farming started")]
	[DataRow(NotificationKind.FarmingStopped, "⏸️ bot1 — farming stopped", "⏸️ <b>bot1</b> — farming stopped")]
	internal void FormatterFarmingRows(NotificationKind kind, string plain, string html) {
		FormattedMessage message = Formatter.Format(new Notification(kind, "bot1", DateTimeOffset.UnixEpoch));

		Assert.AreEqual(plain, message.PlainText);
		Assert.AreEqual(html, message.Html);
	}

	[TestMethod]
	internal void FormatterEscapesTheBotNameInHtmlOnly() {
		FormattedMessage message = Formatter.Format(new Notification(NotificationKind.FarmingFinished, "a<b>", DateTimeOffset.UnixEpoch));

		Assert.AreEqual("✅ a<b> — farming finished", message.PlainText);
		Assert.AreEqual("✅ <b>a&lt;b&gt;</b> — farming finished", message.Html);
	}

	[TestMethod]
	internal void OverridesApplyToFarmingKinds() {
		NotifierSettings settings = TestData.Settings(TestData.ConfigJson(""", "Events": {"FarmingFinished": false, "FarmingStarted": true}"""), TestData.Token);

		Assert.IsFalse(Filter.Allows(new Notification(NotificationKind.FarmingFinished, "bot1", DateTimeOffset.UnixEpoch), settings));
		Assert.IsTrue(Filter.Allows(new Notification(NotificationKind.FarmingStarted, "bot1", DateTimeOffset.UnixEpoch), settings));
		Assert.IsFalse(Filter.Allows(new Notification(NotificationKind.FarmingStopped, "bot1", DateTimeOffset.UnixEpoch), settings));
	}
}
#pragma warning restore CA1812 // MSTest uses this class during runtime
