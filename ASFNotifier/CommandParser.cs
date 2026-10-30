using System;

namespace ASFNotifier;

// Text: what is passed to ASF. Verb: its first word as ASF's own dispatch sees it (args[0], upper-cased invariantly).
// Mentioned: the message named the bot (user ID, localpart or pill), not only the prefix other bots share
internal readonly record struct ParsedCommand(string Text, string Verb, bool Mentioned);

// Spec section 9.1: turns a room message into an ASF command, without ASF types
internal static class CommandParser {
	private const string PillLink = "https://matrix.to/#/";

	// Where the user ID in a pill's href ends: the closing quote, or a query such as ?via=
	private static readonly char[] PillLinkEnd = ['"', '\'', '?'];

	// Refused from Matrix whatever the sender's access level. Matched on the verb exactly as Commands.Response
	// dispatches it, so any spelling ASF would run (case, spacing, mention, Unicode case folding) is caught too
	internal static bool IsDenied(string verb) {
		ArgumentNullException.ThrowIfNull(verb);

		return verb.StartsWith("2FA", StringComparison.Ordinal) || verb.StartsWith("TRANSFER", StringComparison.Ordinal) || verb is "INPUT" or "ENCRYPT" or "PRIVACY";
	}

	// null: not addressed to ASF. Accepted: "<prefix>status bot1", "@bot status bot1", "@bot <prefix>status bot1", where
	// @bot is the bot's localpart or full user ID, or the display name of a pill that links to the bot in formattedBody,
	// optionally followed by ':' or ','. The command text always comes from body
	internal static ParsedCommand? Parse(string body, string? prefix, string botUserId, string? formattedBody = null) {
		ArgumentNullException.ThrowIfNull(body);
		ArgumentException.ThrowIfNullOrEmpty(botUserId);

		string text = body.TrimStart();
		bool mentioned = false;
		bool addressed = false;

		int colon = botUserId.IndexOf(':', StringComparison.Ordinal);
		string? pill = PillText(formattedBody, botUserId);

		if ((AfterMention(text, botUserId) ?? (colon > 0 ? AfterMention(text, botUserId[..colon]) : null) ?? (string.IsNullOrEmpty(pill) ? null : AfterMention(text, pill))) is { } rest) {
			text = rest.TrimStart();
			mentioned = true;
			addressed = true;
		}

		if (!string.IsNullOrEmpty(prefix) && text.StartsWith(prefix, StringComparison.Ordinal)) {
			text = text[prefix.Length..];
			addressed = true;
		}

		if (!addressed) {
			return null;
		}

		text = text.Trim();

		// ASF's own split (Commands.Response): any Unicode white space separates arguments
		string[] args = text.Split(Array.Empty<char>(), StringSplitOptions.RemoveEmptyEntries);

		return args.Length == 0 ? null : new ParsedCommand(text, args[0].ToUpperInvariant(), mentioned);
	}

	// The text of the first pill (<a href="https://matrix.to/#/@bot:server">Name</a>) that links to the bot; clients put
	// that display name in body. null: no such pill
	internal static string? PillText(string? formattedBody, string botUserId) {
		if (string.IsNullOrEmpty(formattedBody)) {
			return null;
		}

		for (int at = formattedBody.IndexOf(PillLink, StringComparison.OrdinalIgnoreCase); at >= 0; at = formattedBody.IndexOf(PillLink, at + 1, StringComparison.OrdinalIgnoreCase)) {
			int start = at + PillLink.Length;
			int end = formattedBody.IndexOfAny(PillLinkEnd, start);

			if ((end < 0) || !string.Equals(Uri.UnescapeDataString(formattedBody[start..end]), botUserId, StringComparison.OrdinalIgnoreCase)) {
				continue;
			}

			int open = formattedBody.IndexOf('>', end);
			int close = open < 0 ? -1 : formattedBody.IndexOf("</a>", open, StringComparison.OrdinalIgnoreCase);

			return close < 0 ? null : HtmlText(formattedBody[(open + 1)..close]).Trim();
		}

		return null;
	}

	// The entities a client writes for text in an HTML body; by hand, the trimmed ASF has no WebUtility (issue #23). An
	// entity left over only makes the name not match body, so the message is not taken as a mention
	private static string HtmlText(string html) => html.Replace("&lt;", "<", StringComparison.Ordinal).Replace("&gt;", ">", StringComparison.Ordinal).Replace("&quot;", "\"", StringComparison.Ordinal).Replace("&#39;", "'", StringComparison.Ordinal).Replace("&#039;", "'", StringComparison.Ordinal).Replace("&#x27;", "'", StringComparison.OrdinalIgnoreCase).Replace("&amp;", "&", StringComparison.Ordinal);

	private static string? AfterMention(string text, string mention) {
		if (!text.StartsWith(mention, StringComparison.OrdinalIgnoreCase)) {
			return null;
		}

		string rest = text[mention.Length..];

		if (rest.StartsWith(':') || rest.StartsWith(',')) {
			rest = rest[1..];
		}

		return (rest.Length == 0) || char.IsWhiteSpace(rest[0]) ? rest : null;
	}
}
