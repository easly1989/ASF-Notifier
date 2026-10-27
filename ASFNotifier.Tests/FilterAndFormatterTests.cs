using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ASFNotifier.Tests;

#pragma warning disable CA1812 // MSTest uses this class during runtime
[TestClass]
internal sealed class FilterAndFormatterTests {
	private static Notification Of(NotificationKind kind, string? bot = "bot1") => new(kind, bot, DateTimeOffset.UnixEpoch);

	private static NotifierSettings Settings(string extra = "") => TestData.Settings(TestData.ConfigJson(extra), TestData.Token);

	[TestMethod]
	[DataRow(NotificationKind.BotStoppedByAsf, true)]
	[DataRow(NotificationKind.BotDisconnected, true)]
	[DataRow(NotificationKind.BotReconnected, true)]
	[DataRow(NotificationKind.FarmingFinished, true)]
	[DataRow(NotificationKind.AsfUpdated, true)]
	[DataRow(NotificationKind.BotStopped, false)]
	[DataRow(NotificationKind.FarmingStarted, false)]
	[DataRow(NotificationKind.FarmingStopped, false)]
	internal void BuiltInDefaults(NotificationKind kind, bool expected) =>
		Assert.AreEqual(expected, Filter.Allows(Of(kind), Settings()));

	[TestMethod]
	internal void FormatterEscapesTheBotNameInHtmlOnly() {
		FormattedMessage message = Formatter.Format(Of(NotificationKind.Test, "a<b>&\"c"));

		Assert.AreEqual("🧪 ASF-Notifier test from a<b>&\"c — delivery works", message.PlainText);
		Assert.AreEqual("🧪 <b>ASF-Notifier</b> test from <b>a&lt;b&gt;&amp;&quot;c</b> — delivery works", message.Html);
	}

	[TestMethod]
	internal void FormatterTestRow() {
		FormattedMessage message = Formatter.Format(Of(NotificationKind.Test));

		Assert.AreEqual("🧪 ASF-Notifier test from bot1 — delivery works", message.PlainText);
		Assert.AreEqual("🧪 <b>ASF-Notifier</b> test from <b>bot1</b> — delivery works", message.Html);
	}

	[TestMethod]
	internal void GlobalDisabledDropsEverything() {
		NotifierSettings disabled = TestData.Settings("""{"ASFNotifier": {"Enabled": false}}""");

		foreach (NotificationKind kind in Enum.GetValues<NotificationKind>()) {
			Assert.IsFalse(Filter.Allows(Of(kind), disabled), kind.ToString());
		}
	}

	[TestMethod]
	internal void GlobalMapOverridesDefaultsBothWays() {
		NotifierSettings settings = Settings(""", "Events": {"BotStopped": true, "FarmingFinished": false}""");

		Assert.IsTrue(Filter.Allows(Of(NotificationKind.BotStopped), settings));
		Assert.IsFalse(Filter.Allows(Of(NotificationKind.FarmingFinished), settings));
		Assert.IsTrue(Filter.Allows(Of(NotificationKind.BotStoppedByAsf), settings));
		Assert.IsFalse(Filter.Allows(Of(NotificationKind.FarmingStarted), settings));
	}

	[TestMethod]
	[DataRow(NotificationKind.Suppressed)]
	[DataRow(NotificationKind.Test)]
	internal void NonConfigurableKindsPassWhenEnabled(NotificationKind kind) =>
		Assert.IsTrue(Filter.Allows(Of(kind), Settings()));
}
#pragma warning restore CA1812 // MSTest uses this class during runtime
