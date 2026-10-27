using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SteamKit2;

namespace ASFNotifier.Tests;

#pragma warning disable CA1812 // MSTest uses this class during runtime
[TestClass]
internal sealed class StopClassifierTests {
	private static readonly DateTimeOffset T = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

	internal static IEnumerable<object[]> FailureResults => Enum.GetValues<EResult>().Where(StopClassifier.IsReason).Distinct().Select(static result => new object[] { result });

	// Columns: shutdown, required input, hook reason, give-up error (offset s, method, reason), reason entry (offset s, method, reason) → kind, reason, login failed
	[TestMethod]
	[DataRow(true, null, EResult.OK, 0, "HandleLoginResult", "InvalidPassword", null, null, null, null, null, false, DisplayName = "1. shutdown: nothing, even with an ASF error")]
	[DataRow(true, "TwoFactorAuthentication", EResult.OK, null, null, null, null, null, null, null, null, false, DisplayName = "1. shutdown: nothing, even when input was needed")]
	[DataRow(false, "TwoFactorAuthentication", EResult.OK, null, null, null, null, null, null, "BotStoppedByAsf", null, false, DisplayName = "3. needed input")]
	[DataRow(false, "SteamGuard", EResult.InvalidPassword, 0, "RequestInput", null, null, null, null, "BotStoppedByAsf", null, false, DisplayName = "3. needed input wins over an error and a hook reason")]
	[DataRow(false, null, EResult.AccountDisabled, 0, "HandleLoginResult", "InvalidPassword", null, null, null, "BotStoppedByAsf", "AccountDisabled", true, DisplayName = "4. reason from the hook first")]
	[DataRow(false, null, EResult.OK, 0, "HandleLoginResult", "InvalidPassword", -10, "HandleLoginResult", "TwoFactorCodeMismatch", "BotStoppedByAsf", "InvalidPassword", true, DisplayName = "4. then from the error entry")]
	[DataRow(false, null, EResult.Invalid, 0, "OnLoggedOff", null, -1, "OnLoggedOff", "LogonSessionReplaced", "BotStoppedByAsf", "LogonSessionReplaced", false, DisplayName = "4. then from the latest reason entry (logged off)")]
	[DataRow(false, null, EResult.OK, 0, "HandleLoginResult", null, -30, "HandleLoginResult", "InvalidPassword", "BotStoppedByAsf", "InvalidPassword", true, DisplayName = "4. then from the latest reason entry (login)")]
	[DataRow(false, null, EResult.OK, 0, "HandleLoginResult", null, -61, "HandleLoginResult", "InvalidPassword", "BotStoppedByAsf", null, false, DisplayName = "4. a reason entry outside the window is ignored")]
	[DataRow(false, null, EResult.OK, 0, "OnConnected", null, null, null, null, "BotStoppedByAsf", null, false, DisplayName = "4. no reason anywhere")]
	[DataRow(false, null, EResult.OK, -59, "RequestInput", null, null, null, null, "BotStoppedByAsf", null, false, DisplayName = "4. error 59 s before the disconnect")]
	[DataRow(false, null, EResult.OK, 59, "OnLoggedOn", null, null, null, null, "BotStoppedByAsf", null, false, DisplayName = "4. error 59 s after the disconnect")]
	[DataRow(false, null, EResult.OK, 61, "HandleLoginResult", "InvalidPassword", null, null, null, "BotStopped", null, false, DisplayName = "5. error 61 s after the disconnect")]
	[DataRow(false, null, EResult.OK, -61, "HandleLoginResult", "InvalidPassword", null, null, null, "BotStopped", null, false, DisplayName = "5. error from before the disconnect window")]
	[DataRow(false, null, EResult.OK, null, null, null, -5, "HandleLoginResult", "InvalidPassword", "BotStopped", null, false, DisplayName = "5. a warning alone is not an ASF error")]
	[DataRow(false, null, EResult.OK, null, null, null, null, null, null, "BotStopped", null, false, DisplayName = "5. manual stop, config edit, ShutdownOnFarmingFinished")]
	internal void Classification(bool shuttingDown, string? requiredInput, EResult hookReason, int? errorOffset, string? errorMethod, string? errorReason, int? reasonOffset, string? reasonMethod, string? reasonReason, string? expectedKind, string? expectedReason, bool expectedLoginFailed) {
		TapEntry? error = Entry(errorOffset, errorMethod, errorReason);
		TapEntry? reason = Entry(reasonOffset, reasonMethod, reasonReason);

		Notification? result = StopClassifier.Classify("bot1", T, shuttingDown, requiredInput, hookReason, error, reason);

		if (expectedKind == null) {
			Assert.IsNull(result);

			return;
		}

		Assert.IsNotNull(result);
		Assert.AreEqual(Enum.Parse<NotificationKind>(expectedKind), result.Kind);
		Assert.AreEqual("bot1", result.BotName);
		Assert.AreEqual(T, result.OccurredAt);
		Assert.AreEqual(requiredInput, result.RequiredInput);
		Assert.AreEqual(expectedReason, result.Reason);
		Assert.AreEqual(expectedLoginFailed, result.LoginFailed);

		// Evidence that ASF gave up: a give-up error inside the window, whatever step decided
		Assert.AreEqual(errorOffset is { } offset && (Math.Abs(offset) <= 60), result.AsfGaveUp);
	}

	[TestMethod]
	[DynamicData(nameof(FailureResults))]
	internal void LoginFailedWithEachResult(EResult failure) {
		// Through the hook, and through ASF's own log line, which is how it usually arrives
		Notification? fromHook = StopClassifier.Classify("bot1", T, false, null, failure, new TapEntry(T, "HandleLoginResult", null), null);
		Notification? fromLog = StopClassifier.Classify("bot1", T, false, null, EResult.OK, new TapEntry(T, "HandleLoginResult", StopClassifier.ParseReason($"Unable to login to Steam: {failure}/{EResult.Invalid}")), null);

		foreach (Notification? result in new[] { fromHook, fromLog }) {
			Assert.IsNotNull(result);
			Assert.AreEqual(NotificationKind.BotStoppedByAsf, result.Kind);
			Assert.AreEqual(failure.ToString(), result.Reason);
			Assert.IsTrue(result.LoginFailed);
		}
	}

	[TestMethod]
	[DataRow("Unable to login to Steam: InvalidPassword/Invalid", EResult.InvalidPassword, DisplayName = "English, two candidates: the first wins")]
	[DataRow("Received TwoFactorCodeMismatch error code 3 times in a row. Either your 2FA credentials are no longer valid, or your clock is out of sync, aborting!", EResult.TwoFactorCodeMismatch, DisplayName = "English, number after the reason")]
	[DataRow("Logged off of Steam: LogonSessionReplaced", EResult.LogonSessionReplaced, DisplayName = "English, logged off")]
	[DataRow("Nie można zalogować się do Steam: AccountDisabled/Invalid", EResult.AccountDisabled, DisplayName = "Polish")]
	[DataRow("Не удалось войти в Steam: RateLimitExceeded/Invalid", EResult.RateLimitExceeded, DisplayName = "Russian")]
	[DataRow("无法登录 Steam：TwoFactorCodeMismatch/Invalid", EResult.TwoFactorCodeMismatch, DisplayName = "Chinese, full-width colon")]
	[DataRow("Unable to login to Steam: 5/Invalid", null, DisplayName = "A numeric token is not a reason")]
	[DataRow("Unable to login to Steam: OK/OK", null, DisplayName = "OK is not a reason")]
	[DataRow("Unable to login to Steam: Invalid/InvalidPassword", EResult.InvalidPassword, DisplayName = "Invalid is skipped, the next candidate wins")]
	[DataRow("Unable to login to Steam: invalidpassword/Invalid", null, DisplayName = "Case-sensitive")]
	[DataRow("input is invalid!", null, DisplayName = "No reason at all")]
	[DataRow("", null, DisplayName = "Empty")]
	internal void ReasonParsing(string message, EResult? expected) => Assert.AreEqual(expected, StopClassifier.ParseReason(message));

	private static TapEntry? Entry(int? offsetSeconds, string? method, string? reason) =>
		(offsetSeconds == null) || (method == null) ? null : new TapEntry(T.AddSeconds(offsetSeconds.Value), method, reason == null ? null : Enum.Parse<EResult>(reason));
}
#pragma warning restore CA1812 // MSTest uses this class during runtime
