using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ASFNotifier.Tests;

#pragma warning disable CA1812 // MSTest uses this class during runtime
[TestClass]
internal sealed class AsfErrorSourceTests {
	private static readonly DateTimeOffset At = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

	private static Notification? From(string logger, string message, string? exceptionType = null, string? exceptionText = null) =>
		AsfErrorSource.ToNotification(new LoggedError(logger, message, At, exceptionType, exceptionText), TestData.Token, static name => name is "bot1" or "bot2");

	[TestMethod]
	internal void ABotErrorCarriesTheBotMethodAndFirstLine() {
		Notification? notification = From("bot1", "OnConnected() SteamLogin is invalid!");

		Assert.IsNotNull(notification);
		Assert.AreEqual(NotificationKind.AsfError, notification.Kind);
		Assert.AreEqual("bot1", notification.BotName);
		Assert.AreEqual("OnConnected", notification.Method);
		Assert.AreEqual("SteamLogin is invalid!", notification.Text);
		Assert.AreEqual(At, notification.OccurredAt);
	}

	[TestMethod]
	internal void AnAsfErrorHasNoBot() {
		Notification? notification = From("ASF", "Update() Could not check the latest version!");

		Assert.IsNotNull(notification);
		Assert.IsNull(notification.BotName);
		Assert.AreEqual("Update", notification.Method);
	}

	[TestMethod]
	[DataRow("Microsoft.AspNetCore.Server.Kestrel", "Connection id \"0HN\" bad request")]
	[DataRow("bot3", "OnConnected() SteamLogin is invalid!", DisplayName = "Not a bot of this ASF")]
	internal void OtherLoggersAreNotForwarded(string logger, string message) => Assert.IsNull(From(logger, message));

	[TestMethod]
	[DataRow("ASFNotifier() Matrix rejected the credentials or permissions (401 M_UNKNOWN_TOKEN)", DisplayName = "The plugin's log lines")]
	[DataRow("ASFNotifier()", DisplayName = "The plugin's exceptions")]
	internal void ThePluginsOwnEntriesAreNeverForwarded(string message) {
		Assert.IsNull(From("ASF", message, "InvalidOperationException"));
		Assert.IsNull(From("bot1", message, "InvalidOperationException"));
	}

	[TestMethod]
	internal void AnEntryContainingTheTokenIsNeverForwarded() {
		Assert.IsNull(From("ASF", $"Init() bad header Bearer {TestData.Token}"));
		Assert.IsNull(From("bot1", "OnConnected() failed", "HttpRequestException", $"System.Net.Http.HttpRequestException: {TestData.Token}\n   at Somewhere()"));
	}

	[TestMethod]
	[DataRow("no method prefix InvalidPassword")]
	[DataRow("two words() in front")]
	[DataRow("() nothing before")]
	internal void EntriesNotInArchiLoggersShapeAreNotForwarded(string message) => Assert.IsNull(From("bot1", message));

	[TestMethod]
	internal void OnlyTheFirstLineIsKept() {
		Notification? notification = From("bot1", "Start() first line  \r\nsecond line\nthird");

		Assert.AreEqual("first line", notification?.Text);
	}

	[TestMethod]
	internal void AnExceptionEntryNamesTheExceptionTypeButNotItsMessage() {
		Notification? notification = From("ASF", "OnBotDisconnected()", "InvalidOperationException", "System.InvalidOperationException: some detail");

		Assert.AreEqual("OnBotDisconnected", notification?.Method);
		Assert.AreEqual("InvalidOperationException", notification?.Text);
	}

	[TestMethod]
	internal void TheTextIsTrimmedToThreeHundredCharacters() {
		Notification? exact = From("bot1", "Start() " + new string('x', AsfErrorSource.MaxTextLength));
		Notification? longer = From("bot1", "Start() " + new string('x', AsfErrorSource.MaxTextLength + 50));

		Assert.AreEqual(AsfErrorSource.MaxTextLength, exact?.Text?.Length);
		Assert.AreEqual(new string('x', AsfErrorSource.MaxTextLength - 1) + "…", longer?.Text);
	}

	[TestMethod]
	internal void TrimmingNeverSplitsASurrogatePair() {
		string emoji = char.ConvertFromUtf32(0x1F7E2);
		// The emoji's first half would be the last character before the ellipsis
		Notification? notification = From("bot1", "Start() " + new string('x', AsfErrorSource.MaxTextLength - 2) + emoji + new string('y', 10));

		string text = notification?.Text ?? "";
		Assert.AreEqual(new string('x', AsfErrorSource.MaxTextLength - 2) + "…", text);
		Assert.IsFalse(char.IsHighSurrogate(text[^2]));
	}
}
#pragma warning restore CA1812 // MSTest uses this class during runtime
