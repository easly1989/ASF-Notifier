using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ASFNotifier.Tests;

#pragma warning disable CA1812 // MSTest uses this class during runtime
[TestClass]
internal sealed class CommandAllowListTests {
	[TestMethod]
	[DataRow(null)]
	[DataRow("")]
	[DataRow("   ")]
	[DataRow(" , ,")]
	internal void EmptyMeansOffWithoutAnyLogLine(string? value) {
		FakeLog log = new();

		Assert.IsEmpty(CommandAllowList.Parse(value, log));
		Assert.IsEmpty(log.All);
	}

	[TestMethod]
	internal void EntriesMapUsersToLevels() {
		FakeLog log = new();

		IReadOnlyDictionary<string, CommandAccess> users = CommandAllowList.Parse(" @alice:example.org=Master, @bob:example.org = operator ,@carol:example.org=FamilySharing", log);

		Assert.HasCount(3, users);
		Assert.AreEqual(CommandAccess.Master, users["@alice:example.org"]);
		Assert.AreEqual(CommandAccess.Operator, users["@bob:example.org"]);
		Assert.AreEqual(CommandAccess.FamilySharing, users["@carol:example.org"]);
		Assert.IsEmpty(log.Errors);
		Assert.HasCount(1, log.Infos);
		StringAssert.Contains(log.Infos[0], "@alice:example.org (Master)", StringComparison.Ordinal);
	}

	[TestMethod]
	internal void AnEqualsSignInTheLocalpartIsKept() {
		IReadOnlyDictionary<string, CommandAccess> users = CommandAllowList.Parse("@a=b:example.org=Master", new FakeLog());

		Assert.AreEqual(CommandAccess.Master, users["@a=b:example.org"]);
	}

	[TestMethod]
	[DataRow("Owner")]
	[DataRow("owner")]
	[DataRow("OWNER")]
	internal void OwnerIsRefusedWithAClearError(string level) {
		FakeLog log = new();

		IReadOnlyDictionary<string, CommandAccess> users = CommandAllowList.Parse($"@alice:example.org={level},@bob:example.org=Master", log);

		Assert.HasCount(1, users);
		Assert.IsFalse(users.ContainsKey("@alice:example.org"));
		Assert.HasCount(1, log.Errors);
		StringAssert.Contains(log.Errors[0], "Owner cannot be granted from Matrix", StringComparison.Ordinal);
		StringAssert.Contains(log.Errors[0], "entry 1", StringComparison.Ordinal);
	}

	[TestMethod]
	[DataRow("@alice:example.org", DisplayName = "No level")]
	[DataRow("@alice:example.org=", DisplayName = "Empty level")]
	[DataRow("@alice:example.org=Admin", DisplayName = "Unknown level")]
	[DataRow("@alice:example.org=None", DisplayName = "None grants nothing")]
	[DataRow("@alice:example.org=100", DisplayName = "Numeric level")]
	[DataRow("alice:example.org=Master", DisplayName = "No @")]
	[DataRow("@alice=Master", DisplayName = "No server")]
	[DataRow("@:example.org=Master", DisplayName = "No localpart")]
	[DataRow("@alice:=Master", DisplayName = "Empty server")]
	[DataRow("@al ice:example.org=Master", DisplayName = "White space")]
	[DataRow("=Master", DisplayName = "No user")]
	internal void MalformedEntriesAreRefusedAndTheRestApplies(string entry) {
		FakeLog log = new();

		IReadOnlyDictionary<string, CommandAccess> users = CommandAllowList.Parse($"@bob:example.org=Operator,{entry}", log);

		Assert.HasCount(1, users);
		Assert.AreEqual(CommandAccess.Operator, users["@bob:example.org"]);
		Assert.HasCount(1, log.Errors);
		StringAssert.Contains(log.Errors[0], "entry 2", StringComparison.Ordinal);
	}

	[TestMethod]
	internal void ARepeatedUserKeepsTheFirstEntry() {
		FakeLog log = new();

		IReadOnlyDictionary<string, CommandAccess> users = CommandAllowList.Parse("@alice:example.org=Operator,@alice:example.org=Master", log);

		Assert.AreEqual(CommandAccess.Operator, users["@alice:example.org"]);
		Assert.HasCount(1, log.Errors);
	}

	[TestMethod]
	internal void UserIdsAreCaseSensitive() {
		IReadOnlyDictionary<string, CommandAccess> users = CommandAllowList.Parse("@alice:example.org=Master", new FakeLog());

		Assert.IsFalse(users.ContainsKey("@Alice:example.org"));
	}

	// A token pasted into the wrong variable must not end up in the log
	[TestMethod]
	internal void AMalformedEntryIsNeverEchoed() {
		FakeLog log = new();

		CommandAllowList.Parse(TestData.Token + "," + TestData.Token + "=Master", log);

		Assert.HasCount(2, log.Errors);
		Assert.IsFalse(log.All.Any(static line => line.Contains(TestData.Token, StringComparison.Ordinal)));
	}
}
#pragma warning restore CA1812 // MSTest uses this class during runtime
