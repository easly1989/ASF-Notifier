using System;

namespace ASFNotifier;

// Text: what is passed to ASF. Verb: its first word as ASF's own dispatch sees it (args[0], upper-cased invariantly)
internal readonly record struct ParsedCommand(string Text, string Verb);

// Spec section 9.1: turns a room message into an ASF command, without ASF types
internal static class CommandParser {
	// Refused from Matrix whatever the sender's access level. Matched on the verb exactly as Commands.Response
	// dispatches it, so any spelling ASF would run (case, spacing, mention, Unicode case folding) is caught too
	internal static bool IsDenied(string verb) {
		ArgumentNullException.ThrowIfNull(verb);

		return verb.StartsWith("2FA", StringComparison.Ordinal) || verb.StartsWith("TRANSFER", StringComparison.Ordinal) || verb is "INPUT" or "ENCRYPT" or "PRIVACY";
	}

	// null: not addressed to ASF. Accepted: "<prefix>status bot1", "@bot status bot1", "@bot <prefix>status bot1", where
	// @bot is the bot's localpart or full user ID, optionally followed by ':' or ','
	internal static ParsedCommand? Parse(string body, string? prefix, string botUserId) {
		ArgumentNullException.ThrowIfNull(body);
		ArgumentException.ThrowIfNullOrEmpty(botUserId);

		string text = body.TrimStart();
		bool addressed = false;

		int colon = botUserId.IndexOf(':', StringComparison.Ordinal);

		if ((AfterMention(text, botUserId) ?? (colon > 0 ? AfterMention(text, botUserId[..colon]) : null)) is { } rest) {
			text = rest.TrimStart();
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

		return args.Length == 0 ? null : new ParsedCommand(text, args[0].ToUpperInvariant());
	}

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
