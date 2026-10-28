// Checks that every type and member a plugin references exists in a trimmed, single-file ASF build.
// ASF's OS-specific builds trim away what ASF itself never uses, so a plugin that compiles against the full
// runtime can still fail with TypeLoadException or MissingMethodException at run time (issue #23).
//
// Usage: TrimCheck <asf-dir> <plugin.dll>
//   <asf-dir>    an unzipped official OS-specific ASF release (ASF-linux-x64.zip and the like)
//   <plugin.dll> the published plugin assembly
// Exit code 0 when everything resolves, 1 when something is missing, 2 on bad input.

using Mono.Cecil;

if (args.Length != 2) {
	Console.Error.WriteLine("Usage: TrimCheck <asf-dir> <plugin.dll>");

	return 2;
}

string asfDir = args[0];
string pluginPath = args[1];
string? bundle = new[] { "ArchiSteamFarm", "ArchiSteamFarm.exe" }.Select(name => Path.Combine(asfDir, name)).FirstOrDefault(File.Exists);

if (bundle == null) {
	Console.Error.WriteLine($"No ArchiSteamFarm executable in {asfDir}");

	return 2;
}

string runtimeDir = Path.Combine(Path.GetTempPath(), "trimcheck-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(runtimeDir);

try {
	int extracted = SingleFileBundle.ExtractAssemblies(bundle, runtimeDir);
	Console.WriteLine($"Extracted {extracted} assemblies from {Path.GetFileName(bundle)}");

	using DirectoryResolver resolver = new(runtimeDir);
	using ModuleDefinition plugin = ModuleDefinition.ReadModule(pluginPath, new ReaderParameters { AssemblyResolver = resolver });

	SortedSet<string> missing = new(StringComparer.Ordinal);
	int types = 0;
	int members = 0;

	// The trimmer drops whole facade assemblies nobody references, and unused type forwarders from the rest
	foreach (AssemblyNameReference reference in plugin.AssemblyReferences) {
		if (Resolve(() => resolver.Resolve(reference)) == null) {
			missing.Add($"assembly {reference.Name}");
		}
	}

	foreach (TypeReference type in plugin.GetTypeReferences()) {
		types++;

		if (Resolve(type.Resolve) == null) {
			missing.Add($"type   {type.FullName} [{type.Scope.Name}]");
		}
	}

	foreach (MemberReference member in plugin.GetMemberReferences()) {
		members++;

		if (Resolve(member.Resolve) == null) {
			missing.Add($"member {member.FullName}");
		}
	}

	Console.WriteLine($"Checked {types} type references and {members} member references of {plugin.Assembly.Name.Name}");

	foreach (string line in missing) {
		Console.WriteLine("MISSING " + line);
	}

	Console.WriteLine(missing.Count == 0 ? "OK: everything the plugin references exists in the trimmed build" : $"FAIL: {missing.Count} references missing from the trimmed build");

	return missing.Count == 0 ? 0 : 1;
} finally {
	Directory.Delete(runtimeDir, true);
}

// A reference into an assembly the build does not ship counts as missing too
static T? Resolve<T>(Func<T> resolve) where T : class {
	try {
		return resolve();
	} catch (AssemblyResolutionException) {
		return null;
	}
}

// Resolves only from the extracted bundle, never from the SDK's own runtime
internal sealed class DirectoryResolver(string dir) : IAssemblyResolver {
	private readonly Dictionary<string, AssemblyDefinition> Cache = new(StringComparer.OrdinalIgnoreCase);

	public void Dispose() {
		foreach (AssemblyDefinition assembly in Cache.Values) {
			assembly.Dispose();
		}
	}

	public AssemblyDefinition Resolve(AssemblyNameReference name) => Resolve(name, new ReaderParameters());

	public AssemblyDefinition Resolve(AssemblyNameReference name, ReaderParameters parameters) {
		if (Cache.TryGetValue(name.Name, out AssemblyDefinition? cached)) {
			return cached;
		}

		string path = Path.Combine(dir, name.Name + ".dll");

		if (!File.Exists(path)) {
			throw new AssemblyResolutionException(name);
		}

		AssemblyDefinition assembly = AssemblyDefinition.ReadAssembly(path, new ReaderParameters { AssemblyResolver = this });
		Cache[name.Name] = assembly;

		return assembly;
	}
}
