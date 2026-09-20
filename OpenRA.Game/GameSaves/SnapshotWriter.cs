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
using System.IO;
using System.IO.Compression;
using System.Text;
using OpenRA.Traits;

namespace OpenRA.GameSaves
{
	/// <summary>Writes a snapshot: a magic string, a header, the sections, and a section table.</summary>
	/// <remarks>
	/// A saveable type receives a <see cref="SnapshotWriter"/> in its save method. The writer notes the
	/// offset of a section when it opens, and the length when it closes. It writes the section table last,
	/// at the end of the file, because a section has no length until it closes.
	/// </remarks>
	public sealed class SnapshotWriter : IDisposable
	{
		const int MinimumCompressibleLength = 64;

		readonly Stream output;
		readonly List<SnapshotSection> sections = [];
		readonly HashSet<string> sectionNames = [];

		SectionStream openSection;
		bool disposed;

		public SnapshotWriter(Stream output, SnapshotHeader header)
		{
			ArgumentNullException.ThrowIfNull(output);
			ArgumentNullException.ThrowIfNull(header);
			if (!output.CanWrite)
				throw new ArgumentException("Stream must be writable.", nameof(output));

			this.output = output;
			output.Write(SnapshotContainer.Magic, 0, SnapshotContainer.Magic.Length);
			header.Write(output);
		}

		public Stream BeginSection(string name, bool compress = false)
		{
			ObjectDisposedException.ThrowIf(disposed, this);

			if (string.IsNullOrEmpty(name))
				throw new ArgumentException("Section name must not be empty.", nameof(name));

			if (Encoding.UTF8.GetByteCount(name) > SnapshotContainer.MaxSectionNameLength)
				throw new ArgumentException($"Section name '{name}' is longer than {SnapshotContainer.MaxSectionNameLength} bytes.", nameof(name));

			if (openSection != null)
				throw new InvalidOperationException($"Section '{openSection.Name}' is still open.");

			if (!sectionNames.Add(name))
				throw new InvalidOperationException($"Section '{name}' has already been written.");

			openSection = new SectionStream(this, name, output.Position, compress);
			return openSection;
		}

		#region Reference encoding

		public string ActorRef(Actor a)
		{
			return SnapshotRefs.FormatActor(a);
		}

		public string PlayerRef(Player p)
		{
			return SnapshotRefs.FormatPlayer(p);
		}

		public string TargetRef(in Target target)
		{
			return SnapshotRefs.FormatTarget(target);
		}

		#endregion

		public void WriteYamlSection(string name, IEnumerable<MiniYamlNode> nodes, bool compress = false)
		{
			ArgumentNullException.ThrowIfNull(nodes);

			var bytes = Encoding.UTF8.GetBytes(nodes.WriteToString());
			using (var s = BeginSection(name, compress && bytes.Length >= MinimumCompressibleLength))
				s.Write(bytes, 0, bytes.Length);
		}

		void EndSection(SectionStream section, long uncompressedLength)
		{
			var length = output.Position - section.Offset;
			var encoding = section.Compress ? SnapshotSectionEncoding.Deflate : SnapshotSectionEncoding.Raw;
			sections.Add(new SnapshotSection(section.Name, section.Offset, length, encoding, uncompressedLength));
			openSection = null;
		}

		void WriteSectionTable()
		{
			var tableOffset = output.Position;
			output.Write(SnapshotContainer.SectionTableMarker);
			output.Write(sections.Count);
			foreach (var s in sections)
			{
				SnapshotContainer.WriteSectionName(output, s.Name);
				SnapshotContainer.WriteInt64(output, s.Offset);
				SnapshotContainer.WriteInt64(output, s.Length);
				output.WriteByte((byte)s.Encoding);
				SnapshotContainer.WriteInt64(output, s.UncompressedLength);
			}

			SnapshotContainer.WriteInt64(output, tableOffset);
			output.Write(SnapshotContainer.EndMarker);
		}

		public void Dispose()
		{
			if (disposed)
				return;

			if (openSection != null)
				throw new InvalidOperationException($"Section '{openSection.Name}' was not closed before disposing the writer.");

			WriteSectionTable();
			output.Flush();
			disposed = true;
		}

		sealed class SectionStream : Stream
		{
			public readonly string Name;
			public readonly long Offset;
			public readonly bool Compress;

			readonly SnapshotWriter parent;
			readonly Stream sink;
			long written;
			bool closed;

			public SectionStream(SnapshotWriter parent, string name, long offset, bool compress)
			{
				this.parent = parent;
				Name = name;
				Offset = offset;
				Compress = compress;

				sink = compress ? new ZLibStream(parent.output, CompressionLevel.Optimal, true) : parent.output;
			}

			public override bool CanRead => false;
			public override bool CanSeek => false;
			public override bool CanWrite => true;
			public override long Length => written;
			public override long Position { get => written; set => throw new NotSupportedException(); }

			public override void Write(byte[] buffer, int offset, int count)
			{
				sink.Write(buffer, offset, count);
				written += count;
			}

			public override void Write(ReadOnlySpan<byte> buffer)
			{
				sink.Write(buffer);
				written += buffer.Length;
			}

			public override void WriteByte(byte value)
			{
				sink.WriteByte(value);
				written++;
			}

			public override void Flush() { sink.Flush(); }
			public override int Read(byte[] buffer, int offset, int count) { throw new NotSupportedException(); }
			public override long Seek(long offset, SeekOrigin origin) { throw new NotSupportedException(); }
			public override void SetLength(long value) { throw new NotSupportedException(); }

			protected override void Dispose(bool disposing)
			{
				if (disposing && !closed)
				{
					closed = true;

					if (Compress)
						sink.Dispose();

					parent.EndSection(this, written);
				}

				base.Dispose(disposing);
			}
		}
	}
}
