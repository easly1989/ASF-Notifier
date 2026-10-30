using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ASFNotifier.Tests;

#pragma warning disable CA1812 // MSTest uses this class during runtime
[TestClass]
internal sealed class AsfErrorSourceTests {
	private static readonly DateTimeOffset At = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

	private static Notification? From(string logger, string message, Exception? exception = null, bool stopReported = false) =>
		AsfErrorSource.ToNotification(new LoggedError(logger, message, At, exception), TestData.Token, static name => name is "bot1" or "bot2", _ => stopReported);

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
		Assert.IsNull(From("ASF", message, new InvalidOperationException()));
		Assert.IsNull(From("bot1", message, new InvalidOperationException()));
	}

	[TestMethod]
	internal void AnEntryContainingTheTokenIsNeverForwarded() {
		Assert.IsNull(From("ASF", $"Init() bad header Bearer {TestData.Token}"));
		Assert.IsNull(From("bot1", "OnConnected() failed", new InvalidOperationException("outer", new InvalidOperationException($"inner {TestData.Token}"))));
	}

	// Its text cannot be checked for the token, so the entry stays out; reading it never throws
	[TestMethod]
	internal void AnExceptionWhoseTextCannotBeReadIsNotForwarded() => Assert.IsNull(From("bot1", "Start()", new UnprintableException()));

	[TestMethod]
	[DataRow("HandleLoginResult")]
	[DataRow("OnLoggedOff")]
	[DataRow("OnLoggedOn")]
	[DataRow("OnConnected")]
	[DataRow("RequestInput")]
	internal void GiveUpErrorsAreSkippedWhileTheStopIsReported(string method) {
		Assert.IsNull(From("bot1", $"{method}() gave up", stopReported: true));
		Assert.IsNotNull(From("bot1", $"{method}() gave up", stopReported: false));

		// Other methods of that bot, and ASF's own logger, are not give-up sites
		Assert.IsNotNull(From("bot1", "Start() failed", stopReported: true));
		Assert.IsNotNull(From("ASF", $"{method}() failed", stopReported: true));
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
		Notification? notification = From("ASF", "OnBotDisconnected()", new InvalidOperationException("some detail"));

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

#pragma warning disable CA1064 // Only these tests throw and catch it
	private sealed class UnprintableException : Exception {
		public UnprintableException() { }
		public UnprintableException(string message) : base(message) { }
		public UnprintableException(string message, Exception innerException) : base(message, innerException) { }

		public override string ToString() => throw new InvalidOperationException("no text");
	}
#pragma warning restore CA1064 // Only these tests throw and catch it
}
#pragma warning restore CA1812 // MSTest uses this class during runtime
