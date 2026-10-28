using System;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ASFNotifier.Tests;

#pragma warning disable CA1812 // MSTest uses this class during runtime
[TestClass]
internal sealed class JsonElementExtensionsTests {
	[TestMethod]
	internal void FindsAPropertyByExactName() {
		using JsonDocument document = JsonDocument.Parse("""{"a": 1, "B": 2}""");

		Assert.IsTrue(document.RootElement.TryGetMember("a", out JsonElement value));
		Assert.AreEqual(1, value.GetInt32());
		Assert.IsFalse(document.RootElement.TryGetMember("b", out _));
		Assert.IsFalse(document.RootElement.TryGetMember("c", out _));
	}

	// Same as JsonElement.TryGetProperty
	[TestMethod]
	internal void LastDuplicateWins() {
		using JsonDocument document = JsonDocument.Parse("""{"a": 1, "a": 2}""");

		Assert.IsTrue(document.RootElement.TryGetMember("a", out JsonElement value));
		Assert.AreEqual(2, value.GetInt32());
	}

	[TestMethod]
	internal void ThrowsOnANonObject() {
		using JsonDocument document = JsonDocument.Parse("[1]");

		Assert.ThrowsExactly<InvalidOperationException>(() => document.RootElement.TryGetMember("a", out _));
	}
}
#pragma warning restore CA1812 // MSTest uses this class during runtime
