using System;
using ArchiSteamFarm.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ASFNotifier.Tests;

#pragma warning disable CA1812 // MSTest uses this class during runtime
[TestClass]
internal sealed class AsfVersionSourceTests {
	private static readonly Version Current = new(6, 3, 10, 6);

	[TestMethod]
	internal void NothingStoredStoresWithoutMessage() =>
		Assert.AreEqual(new AsfVersionDecision(AsfVersionAction.Store), AsfVersionSource.Decide(null, Current));

	[TestMethod]
	[DataRow("")]
	[DataRow("not a version")]
	[DataRow("6")]
	[DataRow("6.3.10.6-beta")]
	internal void UnparsableStoresWithoutMessage(string stored) =>
		Assert.AreEqual(new AsfVersionDecision(AsfVersionAction.Store), AsfVersionSource.Decide(stored, Current));

	[TestMethod]
	internal void OlderStoredReportsAnUpdate() =>
		Assert.AreEqual(new AsfVersionDecision(AsfVersionAction.Updated, new Version(6, 3, 9, 0)), AsfVersionSource.Decide("6.3.9.0", Current));

	[TestMethod]
	internal void EqualDoesNothing() =>
		Assert.AreEqual(new AsfVersionDecision(AsfVersionAction.None), AsfVersionSource.Decide("6.3.10.6", Current));

	[TestMethod]
	internal void NewerStoredIsADowngrade() =>
		Assert.AreEqual(new AsfVersionDecision(AsfVersionAction.Downgrade, new Version(6, 3, 11, 7)), AsfVersionSource.Decide("6.3.11.7", Current));

	[TestMethod]
	internal void ThreePartStoredEqualsTheSameFourPartVersion() =>
		Assert.AreEqual(new AsfVersionDecision(AsfVersionAction.None), AsfVersionSource.Decide("6.3.10", new Version(6, 3, 10, 0)));

	[TestMethod]
	internal void NotificationCarriesBothVersions() =>
		Assert.AreEqual(new Notification(NotificationKind.AsfUpdated, null, DateTimeOffset.UnixEpoch, FromVersion: "6.3.9.0", ToVersion: "6.3.10.6"), AsfVersionSource.Updated(new Version(6, 3, 9, 0), Current, DateTimeOffset.UnixEpoch));

	[TestMethod]
	internal void RunningAsfVersionIsTheAsfAssemblyNotThePlugin() {
		Version? asf = typeof(ASF).Assembly.GetName().Version;

		Assert.IsNotNull(asf);
		Assert.AreEqual(asf, AsfVersionSource.RunningVersion);
		Assert.AreNotEqual(typeof(ASFNotifier).Assembly.GetName().Version, AsfVersionSource.RunningVersion);
	}

	[TestMethod]
	internal void FormatterAsfUpdatedRow() {
		FormattedMessage message = Formatter.Format(new Notification(NotificationKind.AsfUpdated, null, DateTimeOffset.UnixEpoch, FromVersion: "6.3.10.6", ToVersion: "6.3.11.7"));

		Assert.AreEqual("⬆️ ASF updated 6.3.10.6 → 6.3.11.7", message.PlainText);
		Assert.AreEqual("⬆️ ASF updated <code>6.3.10.6</code> → <code>6.3.11.7</code>", message.Html);
	}

	[TestMethod]
	internal void EventsMapCanDisableAsfUpdated() {
		Notification notification = new(NotificationKind.AsfUpdated, null, DateTimeOffset.UnixEpoch, FromVersion: "6.3.9.0", ToVersion: "6.3.10.6");

		Assert.IsTrue(Filter.Allows(notification, TestData.Settings(TestData.ConfigJson(""), TestData.Token)));
		Assert.IsFalse(Filter.Allows(notification, TestData.Settings(TestData.ConfigJson(""", "Events": {"AsfUpdated": false}"""), TestData.Token)));
	}
}
#pragma warning restore CA1812 // MSTest uses this class during runtime
