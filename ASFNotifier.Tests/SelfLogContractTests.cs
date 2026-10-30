using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ASFNotifier.Tests;

// Spec section 5.6: AsfError never forwards the plugin's own errors because every Error the plugin writes carries the
// method name ASFNotifier. A new call that lets ASF fill in [CallerMemberName] would loop back into the room
#pragma warning disable CA1812 // MSTest uses this class during runtime
[TestClass]
internal sealed partial class SelfLogContractTests {
	[TestMethod]
	internal void EveryErrorThePluginLogsCarriesItsOwnMethodName() {
		string sources = Path.Combine(RepositoryRoot(), "ASFNotifier");
		List<string> calls = [];
		List<string> offenders = [];

		foreach (string file in Directory.EnumerateFiles(sources, "*.cs", SearchOption.AllDirectories)) {
			string[] lines = File.ReadAllLines(file);

			for (int i = 0; i < lines.Length; i++) {
				if (!ErrorCall().IsMatch(lines[i])) {
					continue;
				}

				string where = $"{Path.GetFileName(file)}:{i + 1}";
				calls.Add(where);

				if (!lines[i].Contains("nameof(ASFNotifier)", StringComparison.Ordinal)) {
					offenders.Add($"{where}: {lines[i].Trim()}");
				}
			}
		}

		// The scan itself must see the calls it guards
		Assert.IsGreaterThanOrEqualTo(5, calls.Count, string.Join(", ", calls));
		Assert.IsEmpty(offenders, string.Join(Environment.NewLine, offenders));
	}

	// Error and Fatal level calls of ArchiLogger
	[GeneratedRegex(@"\.Log(GenericError|GenericException|NullError|FatalError|FatalException)\(", RegexOptions.CultureInvariant)]
	private static partial Regex ErrorCall();

	private static string RepositoryRoot() {
		for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory != null; directory = directory.Parent) {
			if (File.Exists(Path.Combine(directory.FullName, "ASFNotifier.slnx"))) {
				return directory.FullName;
			}
		}

		throw new InvalidOperationException("ASFNotifier.slnx not found above " + AppContext.BaseDirectory);
	}
}
#pragma warning restore CA1812 // MSTest uses this class during runtime
