using System.IO.Compression;
using System.Text;

// The .NET single-file bundle layout (dotnet/runtime src/installer/managed/Microsoft.NET.HostModel/Bundle)
internal static class SingleFileBundle {
	private const byte AssemblyEntry = 1;

	private static readonly byte[] Signature = [
		0x8b, 0x12, 0x02, 0xb9, 0x6a, 0x61, 0x20, 0x38, 0x72, 0x7b, 0x93, 0x02, 0x14, 0xd7, 0xa0, 0x32,
		0x13, 0xf5, 0xb9, 0xe6, 0xef, 0xae, 0x33, 0x18, 0xee, 0x3b, 0x2d, 0xce, 0x24, 0xb3, 0x6a, 0xae
	];

	internal static int ExtractAssemblies(string bundlePath, string outputDir) {
		byte[] data = File.ReadAllBytes(bundlePath);
		int marker = data.AsSpan().IndexOf(Signature);

		if (marker < 8) {
			throw new InvalidDataException($"{bundlePath} is not a single-file bundle");
		}

		long headerOffset = BitConverter.ToInt64(data, marker - 8);

		using MemoryStream stream = new(data);
		using BinaryReader reader = new(stream, Encoding.UTF8);
		stream.Position = headerOffset;

		uint major = reader.ReadUInt32();
		reader.ReadUInt32();
		int count = reader.ReadInt32();
		reader.ReadString();

		if (major >= 2) {
			// deps.json and runtimeconfig.json offsets and sizes, then the flags
			stream.Position += (4 * sizeof(long)) + sizeof(ulong);
		}

		int extracted = 0;

		for (int i = 0; i < count; i++) {
			long offset = reader.ReadInt64();
			long size = reader.ReadInt64();
			long compressedSize = major >= 6 ? reader.ReadInt64() : 0;
			byte type = reader.ReadByte();
			string path = reader.ReadString();

			if ((type != AssemblyEntry) || !path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) {
				continue;
			}

			using MemoryStream raw = new(data, (int) offset, (int) (compressedSize > 0 ? compressedSize : size), false);
			using FileStream file = File.Create(Path.Combine(outputDir, Path.GetFileName(path)));

			if (compressedSize > 0) {
				using DeflateStream inflate = new(raw, CompressionMode.Decompress);
				inflate.CopyTo(file);
			} else {
				raw.CopyTo(file);
			}

			extracted++;
		}

		return extracted;
	}
}
