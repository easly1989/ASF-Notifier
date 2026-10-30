using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ASFNotifier.Tests;

#pragma warning disable CA1812 // MSTest uses this class during runtime
[TestClass]
internal sealed class CommandParserTests {
	internal const string BotUserId = "@asf:example.org";

	[TestMethod]
	[DataRow("!status bot1", "status bot1", "STATUS", DisplayName = "Prefix")]
	[DataRow("@asf status bot1", "status bot1", "STATUS", DisplayName = "Mention")]
	[DataRow("@asf !status bot1", "status bot1", "STATUS", DisplayName = "Mention and prefix")]
	[DataRow("@asf:example.org status", "status", "STATUS", DisplayName = "Full user ID")]
	[DataRow("@asf:example.org: status", "status", "STATUS", DisplayName = "Full user ID and colon")]
	[DataRow("@asf: status", "status", "STATUS", DisplayName = "Mention and colon")]
	[DataRow("@asf, !Pause bot1", "Pause bot1", "PAUSE", DisplayName = "Mention, comma and prefix")]
	[DataRow("@ASF STATUS", "STATUS", "STATUS", DisplayName = "Upper case mention")]
	[DataRow("  !status  ", "status", "STATUS", DisplayName = "Surrounding spaces")]
	[DataRow("! status", "status", "STATUS", DisplayName = "Space after the prefix")]
	[DataRow("!redeem bot1 AAAAA-BBBBB-CCCCC", "redeem bot1 AAAAA-BBBBB-CCCCC", "REDEEM", DisplayName = "Arguments kept")]
	internal void AcceptedForms(string body, string text, string verb) {
		ParsedCommand? command = CommandParser.Parse(body, "!", BotUserId);

		Assert.IsNotNull(command);
		Assert.AreEqual(text, command.Value.Text);
		Assert.AreEqual(verb, command.Value.Verb);
	}

	[TestMethod]
	[DataRow("status bot1", DisplayName = "Plain chat")]
	[DataRow("hello @asf status", DisplayName = "Mention not first")]
	[DataRow("@asfx status", DisplayName = "Another user")]
	[DataRow("@asf:other.example.org status", DisplayName = "Same localpart, other server")]
	[DataRow("!", DisplayName = "Prefix alone")]
	[DataRow("@asf", DisplayName = "Mention alone")]
	[DataRow("@asf !", DisplayName = "Mention and prefix alone")]
	[DataRow("> <@alice:example.org> !status\n\nthanks", DisplayName = "Reply fallback quoting a command")]
	[DataRow("", DisplayName = "Empty")]
	internal void NotACommand(string body) => Assert.IsNull(CommandParser.Parse(body, "!", BotUserId));

	private const string Pill = "<a href=\"https://matrix.to/#/@asf:example.org\">ASF Bot</a>";

	// A client turns a mention into a pill and puts its display name in body; the name comes from the pill, the command
	// text from body
	[TestMethod]
	[DataRow("ASF Bot: status bot1", Pill + ": status bot1", "status bot1", DisplayName = "Pill and colon")]
	[DataRow("asf bot !pause bot1", Pill + " !pause bot1", "pause bot1", DisplayName = "Pill and prefix, any case")]
	[DataRow("ASF Bot status", Pill + " <b>status</b>", "status", DisplayName = "Markup after the pill")]
	internal void APillToTheBotIsAMention(string body, string formattedBody, string text) => Assert.AreEqual(text, CommandParser.Parse(body, "!", BotUserId, formattedBody)?.Text);

	[TestMethod]
	[DataRow("ASF Bot: status", "<a href=\"https://matrix.to/#/@alice:example.org\">ASF Bot</a>: status", DisplayName = "Pill to another user")]
	[DataRow("Hi ASF Bot: status", "Hi " + Pill + ": status", DisplayName = "Pill not first")]
	[DataRow("ASF Botty status", Pill + "ty status", DisplayName = "Name followed by more letters")]
	[DataRow("ASF Bot: status", null, DisplayName = "No formatted body")]
	internal void NotAPillMention(string body, string? formattedBody) => Assert.IsNull(CommandParser.Parse(body, "!", BotUserId, formattedBody));

	[TestMethod]
	[DataRow(Pill, "ASF Bot", DisplayName = "Plain")]
	[DataRow("<a href='https://matrix.to/#/%40asf%3Aexample.org?via=example.org'>ASF Bot</a>", "ASF Bot", DisplayName = "Encoded, single quotes, via")]
	[DataRow("<a href=\"https://matrix.to/#/@alice:example.org\">Alice</a> and <a href=\"https://matrix.to/#/@ASF:example.org\"> A &amp; B </a>", "A & B", DisplayName = "Second pill, entity, case")]
	[DataRow("<a href=\"https://matrix.to/#/@alice:example.org\">Alice</a>", null, DisplayName = "Another user")]
	[DataRow("<a href=\"https://matrix.to/#/@asf:example.org", null, DisplayName = "Cut off")]
	[DataRow("plain text", null, DisplayName = "No pill")]
	internal void PillTextIsTheBotsDisplayName(string formattedBody, string? text) => Assert.AreEqual(text, CommandParser.PillText(formattedBody, BotUserId));

	[TestMethod]
	internal void WithoutAPrefixOnlyTheMentionCounts() {
		Assert.IsNull(CommandParser.Parse("!status", null, BotUserId));
		Assert.IsNull(CommandParser.Parse("status", "", BotUserId));
		Assert.AreEqual("status", CommandParser.Parse("@asf status", null, BotUserId)?.Text);
	}

	[TestMethod]
	internal void ACustomPrefixIsHonoured() {
		Assert.AreEqual("status", CommandParser.Parse("!!status", "!!", BotUserId)?.Text);
		Assert.AreEqual("status", CommandParser.Parse("@asf !!status", "!!", BotUserId)?.Text);
		Assert.IsNull(CommandParser.Parse("!status", "!!", BotUserId));
	}

	[TestMethod]
	[DataRow("2FA", true)]
	[DataRow("2FANO", true)]
	[DataRow("2FAOK", true)]
	[DataRow("2FAINIT", true)]
	[DataRow("2FAFINALIZE", true)]
	[DataRow("INPUT", true)]
	[DataRow("ENCRYPT", true)]
	[DataRow("PRIVACY", true)]
	[DataRow("TRANSFER", true)]
	[DataRow("TRANSFER^", true)]
	[DataRow("TRANSFER&", true)]
	[DataRow("TRANSFER@", true)]
	[DataRow("TRANSFER%", true)]
	[DataRow("REDEEM", false)]
	[DataRow("STATUS", false)]
	[DataRow("NOTIFYTEST", false)]
	[DataRow("HASH", false)]
	[DataRow("EXIT", false)]
	internal void DenyList(string verb, bool denied) => Assert.AreEqual(denied, CommandParser.IsDenied(verb));

	// Every way a sender might sneak a denied command past the check; each must parse to a denied verb, exactly as
	// ASF's own dispatch would see it
	[TestMethod]
	[DataRow("!2fa")]
	[DataRow("!2FA bot1")]
	[DataRow("!2Fa bot1")]
	[DataRow("!   2fa bot1")]
	[DataRow("   !2fa")]
	[DataRow("!\t2fa")]
	[DataRow("!\u00A02fa", DisplayName = "No-break space, white space to ASF's split")]
	[DataRow("!2faok bot1")]
	[DataRow("!2FANO")]
	[DataRow("@asf 2fa")]
	[DataRow("@asf     2fa bot1")]
	[DataRow("@ASF !2FA")]
	[DataRow("@asf:example.org: 2fa")]
	[DataRow("@asf, !2fa")]
	[DataRow("@asf !  2fa")]
	[DataRow("!input bot1 SteamGuard ABCDE")]
	[DataRow("!INPUT bot1 Password hunter2")]
	[DataRow("!encrypt AES secret")]
	[DataRow("!privacy bot1 1")]
	[DataRow("!transfer bot2")]
	[DataRow("!tran\u017Ffer bot2", DisplayName = "Long s, upper-cased to S by ASF")]
	[DataRow("!transfer^ bot1 730 6 bot2")]
	[DataRow("@asf transfer@ bot1 730 bot2")]
	internal void BypassAttemptsAreStillDenied(string body) {
		ParsedCommand? command = CommandParser.Parse(body, "!", BotUserId);

		Assert.IsNotNull(command);
		Assert.IsTrue(CommandParser.IsDenied(command.Value.Verb), command.Value.Verb);
	}

	// The verb is folded with the same call as ASF's dispatch (ToUpperInvariant), so the deny-list and ASF agree on every
	// look-alike: a dotless i (U+0131) stays lower case, which ASF does not run as input either
	[TestMethod]
	internal void LookAlikeLettersFoldAsInAsf() {
		Assert.AreEqual("\u0131nput".ToUpperInvariant(), CommandParser.Parse("!\u0131nput bot1 Password x", "!", BotUserId)?.Verb);
		Assert.AreNotEqual("INPUT", "\u0131nput".ToUpperInvariant());
	}
}
#pragma warning restore CA1812 // MSTest uses this class during runtime
