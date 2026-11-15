#region Copyright & License Information
/*
 * Copyright (c) The OpenRA Developers and Contributors
 * This file is part of OpenRA, which is free software. It is made
 * available to you under the terms of the GNU General Public License
 * as published by the Free Software Foundation, either version 3 of
 * the License, or (at your option) any later version. For more
 * information, see COPYING.
 */
#endregion

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace OpenRA.Snapshot
{
	/// <summary>
	/// A snapshot as bytes: a hash, a header with the sync hash, and each section as a compressed node tree on its own.
	/// </summary>
	public sealed class SnapshotFile
	{
		/// <summary>The number of this layout of bytes. A reader takes its own number only.</summary>
		public const byte Format = 1;

		const int HashLength = 20;

		// A person can change a file by hand, so this checks each count before it uses it.
		const int MaxSectionBytes = 256 << 20;
		const int MaxDepth = 64;

		static readonly byte[] Magic = "ORASNAP"u8.ToArray();

		public readonly string Mod;
		public readonly string Map;
		public readonly int Tick;
		public readonly int SyncHash;

		readonly byte[] data;
		readonly Dictionary<string, (int Offset, int Length)> sections;

		SnapshotFile(byte[] data, string mod, string map, int tick, int syncHash, Dictionary<string, (int Offset, int Length)> sections)
		{
			this.data = data;
			Mod = mod;
			Map = map;
			Tick = tick;
			SyncHash = syncHash;
			this.sections = sections;
		}

		public static byte[] Write(List<MiniYamlNode> snapshot, World world)
		{
			return Write(snapshot, Game.ModData.Manifest.Id, world.Map.Uid, world.WorldTick, world.SyncHash());
		}

		public static byte[] Write(List<MiniYamlNode> snapshot, string mod, string map, int tick, int syncHash)
		{
			// The file keeps the nodes of a section and not its value, so a value would be lost.
			var withValue = snapshot.FirstOrDefault(section => !string.IsNullOrEmpty(section.Value.Value));
			if (withValue != null)
				throw new ArgumentException($"The section {withValue.Key} has a value. A section holds nodes only.", nameof(snapshot));

			var blobs = snapshot.ConvertAll(section => (section.Key, Bytes: Compress(section.Value.Nodes)));

			using var body = new MemoryStream();
			using (var writer = new BinaryWriter(body, Encoding.UTF8, true))
			{
				writer.Write(mod);
				writer.Write(map);
				writer.Write(tick);
				writer.Write(syncHash);
				writer.Write7BitEncodedInt(blobs.Count);
				foreach (var (key, bytes) in blobs)
				{
					writer.Write(key);
					writer.Write7BitEncodedInt(bytes.Length);
				}

				foreach (var (_, bytes) in blobs)
					writer.Write(bytes);
			}

			var content = body.ToArray();
			using var file = new MemoryStream(Magic.Length + 1 + HashLength + content.Length);
			file.Write(Magic);
			file.WriteByte(Format);
			file.Write(SHA1.HashData(content));
			file.Write(content);
			return file.ToArray();
		}

		/// <summary>Checks <paramref name="data"/> and reads its header and its table of sections.</summary>
		public static SnapshotFile Read(byte[] data)
		{
			var start = Magic.Length + 1 + HashLength;
			if (data.Length < start || !data.AsSpan(0, Magic.Length).SequenceEqual(Magic))
				throw new SnapshotLoadException("The save holds no snapshot.");

			if (data[Magic.Length] != Format)
				throw new SnapshotLoadException($"The snapshot has the file format {data[Magic.Length]}, and this engine reads {Format}.");

			if (!SHA1.HashData(data.AsSpan(start)).AsSpan().SequenceEqual(data.AsSpan(Magic.Length + 1, HashLength)))
				throw new SnapshotLoadException("The snapshot does not match its hash. The file is damaged or was changed.");

			try
			{
				using var reader = new BinaryReader(new MemoryStream(data, start, data.Length - start), Encoding.UTF8);
				var mod = reader.ReadString();
				var map = reader.ReadString();
				var tick = reader.ReadInt32();
				var syncHash = reader.ReadInt32();
				var count = reader.Read7BitEncodedInt();
				if (count < 0 || count > 64)
					throw new SnapshotLoadException($"The snapshot names {count} sections.");

				var table = new List<(string Key, int Length)>();
				for (var i = 0; i < count; i++)
					table.Add((reader.ReadString(), reader.Read7BitEncodedInt()));

				var offset = start + (int)reader.BaseStream.Position;
				var sections = new Dictionary<string, (int Offset, int Length)>();
				foreach (var (key, length) in table)
				{
					if (length < 0 || length > data.Length - offset || !sections.TryAdd(key, (offset, length)))
						throw new SnapshotLoadException($"The section {key} of the snapshot is outside the file or named twice.");

					offset += length;
				}

				return new SnapshotFile(data, mod, map, tick, syncHash, sections);
			}
			catch (EndOfStreamException e)
			{
				throw new SnapshotLoadException("The snapshot ends before its table of sections does.", e);
			}
			catch (Exception e) when (e is IOException or FormatException)
			{
				throw new SnapshotLoadException($"The table of sections of the snapshot cannot be read: {e.Message}", e);
			}
		}

		public IEnumerable<string> SectionNames => sections.OrderBy(s => s.Value.Offset).Select(s => s.Key);

		public MiniYaml Section(string name)
		{
			if (!sections.TryGetValue(name, out var section))
				throw new SnapshotLoadException($"The snapshot has no {name} section.");

			return new MiniYaml(null, Decompress(data.AsSpan(section.Offset, section.Length).ToArray(), name));
		}

		/// <summary>
		/// Opens each section into a counting stream, and stops after <paramref name="limit"/> bytes. A small file can
		/// open to gigabytes, so this checks a file from another machine first.
		/// </summary>
		public void CheckOpenedSize(long limit)
		{
			var buffer = new byte[1 << 16];
			long total = 0;
			foreach (var (offset, length) in sections.Values)
			{
				try
				{
					using var brotli = new BrotliStream(new MemoryStream(data, offset, length, false), CompressionMode.Decompress);
					int read;
					while ((read = brotli.Read(buffer)) > 0)
					{
						total += read;
						if (total > limit)
							throw new SnapshotLoadException($"The sections of the snapshot open to more than {limit} bytes.");
					}
				}
				catch (Exception e) when (e is InvalidDataException or IOException)
				{
					throw new SnapshotLoadException($"A section of the snapshot cannot be read: {e.Message}", e);
				}
			}
		}

		public List<MiniYamlNode> Nodes()
		{
			return SectionNames.Select(name => new MiniYamlNode(name, Section(name))).ToList();
		}

		// A section is a table of strings, then its nodes. A node names its key, its value (0 for none) and its child count.
		static byte[] Compress(IReadOnlyList<MiniYamlNode> nodes)
		{
			var table = new StringTable();
			using var nodeBytes = new MemoryStream();
			using (var writer = new BinaryWriter(nodeBytes, Encoding.UTF8, true))
				WriteNodes(writer, nodes, table);

			using var body = new MemoryStream((int)nodeBytes.Length * 2);
			using (var writer = new BinaryWriter(body, Encoding.UTF8, true))
			{
				writer.Write7BitEncodedInt(table.Strings.Count);
				foreach (var s in table.Strings)
					writer.Write(s);
			}

			nodeBytes.WriteTo(body);

			// PERF: quality 5 of 11 gives close to the smallest output.
			using var output = new MemoryStream();
			using (var brotli = new BrotliStream(output, new BrotliCompressionOptions { Quality = 5 }, true))
				body.WriteTo(brotli);

			return output.ToArray();
		}

		static void WriteNodes(BinaryWriter writer, IReadOnlyList<MiniYamlNode> nodes, StringTable table)
		{
			writer.Write7BitEncodedInt(nodes.Count);
			foreach (var node in nodes)
			{
				writer.Write7BitEncodedInt(table.Id(node.Key));
				writer.Write7BitEncodedInt(table.Id(node.Value.Value));
				WriteNodes(writer, node.Value.Nodes, table);
			}
		}

		sealed class StringTable
		{
			public readonly List<string> Strings = [""];
			readonly Dictionary<string, int> ids = new() { [""] = 0 };

			public int Id(string s)
			{
				ref var id = ref CollectionsMarshal.GetValueRefOrAddDefault(ids, s ?? "", out var exists);
				if (!exists)
				{
					id = Strings.Count;
					Strings.Add(s);
				}

				return id;
			}
		}

		static List<MiniYamlNode> Decompress(byte[] bytes, string name)
		{
			byte[] body;
			try
			{
				using var brotli = new BrotliStream(new MemoryStream(bytes), CompressionMode.Decompress);
				using var output = new MemoryStream((int)Math.Min(bytes.Length * 8L, MaxSectionBytes));
				var buffer = new byte[1 << 16];
				int read;
				while ((read = brotli.Read(buffer)) > 0)
				{
					if (output.Length + read > MaxSectionBytes)
						throw new InvalidDataException($"it decompresses into more than {MaxSectionBytes} bytes.");

					output.Write(buffer, 0, read);
				}

				body = output.ToArray();
			}
			catch (Exception e) when (e is InvalidDataException or IOException)
			{
				throw new SnapshotLoadException($"The section {name} of the snapshot cannot be read: {e.Message}", e);
			}

			var reader = new SectionReader(body, name);
			var count = reader.Count("strings");
			var strings = new string[count];
			for (var i = 0; i < count; i++)
				strings[i] = reader.String();

			// An empty key or value reads as none, as one that the text of MiniYaml leaves empty does.
			strings[0] = null;
			var nodes = ReadNodes(ref reader, strings, 0);
			if (!reader.AtEnd)
				throw new SnapshotLoadException($"The section {name} of the snapshot has bytes after its last node.");

			return [.. nodes];
		}

		static readonly IEnumerable<MiniYamlNode> NoNodes = (ImmutableArray<MiniYamlNode>)[];

		static MiniYamlNode[] ReadNodes(ref SectionReader reader, string[] strings, int depth)
		{
			if (depth > MaxDepth)
				throw new SnapshotLoadException($"The section {reader.Name} of the snapshot is nested deeper than {MaxDepth}.");

			var count = reader.Count("children");
			var nodes = new MiniYamlNode[count];
			for (var i = 0; i < count; i++)
			{
				var key = strings[reader.Index(strings.Length)];
				var value = strings[reader.Index(strings.Length)];
				var children = ReadNodes(ref reader, strings, depth + 1);

				nodes[i] = new MiniYamlNode(key, new MiniYaml(value, children.Length == 0 ? NoNodes : ImmutableCollectionsMarshal.AsImmutableArray(children)));
			}

			return nodes;
		}

		ref struct SectionReader(byte[] data, string name)
		{
			readonly ReadOnlySpan<byte> data = data;
			int position;

			public readonly string Name = name;
			public readonly bool AtEnd => position == data.Length;

			int Number()
			{
				var result = 0;
				for (var shift = 0; shift < 35; shift += 7)
				{
					if (position >= data.Length)
						throw new SnapshotLoadException($"The section {Name} of the snapshot ends in the middle of a number.");

					var b = data[position++];
					result |= (b & 0x7F) << shift;
					if ((b & 0x80) == 0)
						return result;
				}

				throw new SnapshotLoadException($"The section {Name} of the snapshot holds a number that is too long.");
			}

			public int Count(string what)
			{
				var count = Number();
				if (count < 0 || count > data.Length - position)
					throw new SnapshotLoadException($"The section {Name} of the snapshot names {count} {what}, more than it holds.");

				return count;
			}

			public int Index(int length)
			{
				var index = Number();
				if (index < 0 || index >= length)
					throw new SnapshotLoadException($"The section {Name} of the snapshot names string {index}, and it has {length}.");

				return index;
			}

			public string String()
			{
				var length = Count("bytes of a string");
				var s = Encoding.UTF8.GetString(data.Slice(position, length));
				position += length;
				return s;
			}
		}
	}
}
