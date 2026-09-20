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
using System.Linq;
using System.Text;
using NUnit.Framework;
using OpenRA.GameSaves;

namespace OpenRA.Test
{
	[TestFixture]
	sealed class SnapshotContainerTest
	{
		const string TestEngine = "test-engine";
		const string TestMod = "ra";
		const string TestModVersion = "1.0";
		const string TestMapUid = "0123456789abcdef0123456789abcdef01234567";

		static SnapshotHeader TestHeader(int worldTick = 42, int syncHash = 0x1234)
		{
			return new SnapshotHeader(TestEngine, TestMod, TestModVersion, TestMapUid, worldTick, syncHash, 0, 0, 0,
				new DateTime(2026, 9, 5, 1, 2, 3, DateTimeKind.Utc), SnapshotFlags.Autosave, 7, 9);
		}

		static byte[] WriteSections(SnapshotHeader header, params (string Name, byte[] Data, bool Compress)[] sections)
		{
			var stream = new MemoryStream();
			using (var w = new SnapshotWriter(stream, header))
			{
				foreach (var (name, data, compress) in sections)
					using (var s = w.BeginSection(name, compress))
						s.Write(data, 0, data.Length);
			}

			return stream.ToArray();
		}

		static byte[] ReadSection(SnapshotReader r, string name)
		{
			using (var s = r.OpenSection(name))
			{
				if (s == null)
					return null;

				var result = new MemoryStream();
				s.CopyTo(result);
				return result.ToArray();
			}
		}

		[TestCase(TestName = "Snapshot header data persists over serialization")]
		public void RoundTripsHeaderFields()
		{
			var header = new SnapshotHeader("engine/1.2.3", "", "äöü", "0123456789abcdef",
				int.MinValue, int.MaxValue, -1, 0x7FFFFFFF, ulong.MaxValue,
				DateTime.UtcNow, SnapshotFlags.Autosave | SnapshotFlags.HasScriptState, 3, 5);

			var bytes = WriteSections(header);
			using (var r = new SnapshotReader(new MemoryStream(bytes)))
			{
				Assert.That(r.Header.FormatVersion, Is.EqualTo(SnapshotHeader.CurrentFormatVersion));
				Assert.That(r.Header.EngineVersion, Is.EqualTo(header.EngineVersion));
				Assert.That(r.Header.ModId, Is.EqualTo(header.ModId));
				Assert.That(r.Header.ModVersion, Is.EqualTo(header.ModVersion));
				Assert.That(r.Header.MapUid, Is.EqualTo(header.MapUid));
				Assert.That(r.Header.WorldTick, Is.EqualTo(header.WorldTick));
				Assert.That(r.Header.SyncHash, Is.EqualTo(header.SyncHash));
				Assert.That(r.Header.ReportedFrame, Is.EqualTo(header.ReportedFrame));
				Assert.That(r.Header.ReportedSyncHash, Is.EqualTo(header.ReportedSyncHash));
				Assert.That(r.Header.ReportedDefeatState, Is.EqualTo(header.ReportedDefeatState));
				Assert.That(r.Header.SavedUtc, Is.EqualTo(header.SavedUtc));
				Assert.That(r.Header.Flags, Is.EqualTo(header.Flags));
				Assert.That(r.Header.DroppedActivities, Is.EqualTo(header.DroppedActivities));
				Assert.That(r.Header.DroppedEffects, Is.EqualTo(header.DroppedEffects));
			}
		}

		[TestCase(TestName = "The v2 header has one fixed layout")]
		public void HeaderLayoutIsPinned()
		{
			Assert.That(SnapshotHeader.CurrentFormatVersion, Is.EqualTo(2),
				"v1 is the replay-based container and v2 is this prototype's snapshot format; a version " +
				"change is a deliberate decision, not a side effect of adding a field.");

			var stream = new MemoryStream();
			long headerEnd;
			using (var w = new SnapshotWriter(stream, TestHeader()))
				headerEnd = stream.Position;

			var bytes = stream.ToArray();

			Assert.That(headerEnd - SnapshotContainer.Magic.Length, Is.EqualTo(116),
				"The v2 header layout changed. A save written with the old layout is unreadable, not " +
				"merely stale — the reader consumes a fixed field list, so there is no way to tell the " +
				"two apart. Update this number deliberately, and decide what happens to the saves " +
				"written before the change.");

			using (var r = new SnapshotReader(new MemoryStream(bytes)))
			{
				Assert.That(r.SectionNames, Is.Empty);
				Assert.That(r.Header.FormatVersion, Is.EqualTo(2));
			}
		}

		[TestCase(TestName = "Snapshot sections persist over serialization")]
		public void RoundTripsMultipleSections()
		{
			var a = Encoding.UTF8.GetBytes("section a");
			var b = new byte[] { 0, 1, 2, 250, 251 };
			byte[] c = [];

			var bytes = WriteSections(TestHeader(), ("A", a, false), ("B", b, false), ("Empty", c, false));
			using (var r = new SnapshotReader(new MemoryStream(bytes)))
			{
				string[] expectedNames = ["A", "B", "Empty"];
				Assert.That(r.SectionNames, Is.EquivalentTo(expectedNames));

				Assert.That(ReadSection(r, "B"), Is.EqualTo(b));
				Assert.That(ReadSection(r, "Empty"), Is.EqualTo(c));
				Assert.That(ReadSection(r, "A"), Is.EqualTo(a));
			}
		}

		[TestCase(TestName = "Snapshot compressed sections persist over serialization")]
		public void RoundTripsCompressedSection()
		{
			var data = new byte[64 * 1024];
			for (var i = 0; i < data.Length; i++)
				data[i] = (byte)(i % 16);

			var stream = new MemoryStream();
			using (var w = new SnapshotWriter(stream, TestHeader()))
			using (var s = w.BeginSection("Bulk", compress: true))
				s.Write(data, 0, data.Length);

			using (var r = new SnapshotReader(new MemoryStream(stream.ToArray())))
			{
				Assert.That(ReadSection(r, "Bulk"), Is.EqualTo(data));

				Assert.That(stream.Length, Is.LessThan(data.Length));
			}
		}

		[TestCase(TestName = "Snapshot yaml sections persist over serialization")]
		public void RoundTripsYamlSection()
		{
			var nodes = new List<MiniYamlNode>
			{
				new("Alpha", "1"),
				new("Beta", new MiniYaml("", [new("Nested", "yes")])),
			};

			var stream = new MemoryStream();
			using (var w = new SnapshotWriter(stream, TestHeader()))
				w.WriteYamlSection("Yaml", nodes);

			using (var r = new SnapshotReader(new MemoryStream(stream.ToArray())))
			{
				var restored = r.ReadYamlSection("Yaml");
				Assert.That(restored.WriteToString(), Is.EqualTo(nodes.WriteToString()));
			}
		}

		[TestCase(TestName = "Snapshot sections from a newer format are ignored")]
		public void SkipsUnknownSections()
		{
			var a = Encoding.UTF8.GetBytes("known");
			var future = Encoding.UTF8.GetBytes("written by a newer engine");

			var bytes = WriteSections(TestHeader(), ("A", a, false), ("Future", future, false));
			using (var r = new SnapshotReader(new MemoryStream(bytes)))
			{
				Assert.That(ReadSection(r, "A"), Is.EqualTo(a));
				Assert.That(r.HasSection("Future"), Is.True);
			}
		}

		[TestCase(TestName = "Snapshot returns null for an absent section")]
		public void ReturnsNullForAbsentSection()
		{
			var bytes = WriteSections(TestHeader());
			using (var r = new SnapshotReader(new MemoryStream(bytes)))
			{
				Assert.That(r.OpenSection("Nope"), Is.Null);
				Assert.That(r.ReadYamlSection("Nope"), Is.Null);
				Assert.That(r.HasSection("Nope"), Is.False);
			}
		}

		[TestCase(TestName = "Snapshot with no sections persists over serialization")]
		public void EmptySnapshotRoundTrips()
		{
			var bytes = WriteSections(TestHeader());
			using (var r = new SnapshotReader(new MemoryStream(bytes)))
			{
				Assert.That(r.SectionNames, Is.Empty);
				Assert.That(r.Header.WorldTick, Is.EqualTo(42));
			}
		}

		[TestCase(TestName = "Snapshot reading rejects a file that is not a snapshot")]
		public void RejectsBadMagic()
		{
			var bytes = new byte[64];
			void Act() => new SnapshotReader(new MemoryStream(bytes));
			Assert.That(Act, Throws.TypeOf<InvalidDataException>());
		}

		[TestCase(TestName = "Snapshot reading rejects an unsupported format version")]
		public void RejectsUnsupportedVersion()
		{
			var bytes = WriteSections(TestHeader());
			BitConverter.TryWriteBytes(bytes.AsSpan(SnapshotContainer.Magic.Length), 999);

			void Act() => new SnapshotReader(new MemoryStream(bytes));
			Assert.That(Act, Throws.TypeOf<InvalidDataException>());
		}

		[TestCase(TestName = "Snapshot reading rejects a corrupt trailer")]
		public void RejectsBadTrailer()
		{
			var bytes = WriteSections(TestHeader(), ("A", [1, 2, 3], false));
			bytes[^1] ^= 0xFF;

			void Act() => new SnapshotReader(new MemoryStream(bytes));
			Assert.That(Act, Throws.TypeOf<InvalidDataException>());
		}

		[TestCase(TestName = "Snapshot reading rejects an out of range section table offset")]
		public void RejectsBadSectionTableOffset()
		{
			var bytes = WriteSections(TestHeader(), ("A", [1, 2, 3], false));
			BitConverter.TryWriteBytes(bytes.AsSpan(bytes.Length - SnapshotContainer.TrailerLength), long.MaxValue);

			void Act() => new SnapshotReader(new MemoryStream(bytes));
			Assert.That(Act, Throws.TypeOf<InvalidDataException>());
		}

		[TestCase(4)]
		[TestCase(16)]
		[TestCase(40)]
		public void RejectsTruncatedFile(int keepBytes)
		{
			var bytes = WriteSections(TestHeader(), ("A", [1, 2, 3], false)).Take(keepBytes).ToArray();

			void Act() => new SnapshotReader(new MemoryStream(bytes));
			Assert.That(Act, Throws.InstanceOf<Exception>());
		}

		[TestCase(TestName = "Snapshot reading rejects a non-seekable stream")]
		public void RejectsNonSeekableStream()
		{
			var bytes = WriteSections(TestHeader());
			void Act() => new SnapshotReader(new NonSeekableStream(bytes));
			Assert.That(Act, Throws.TypeOf<InvalidDataException>());
		}

		[TestCase(TestName = "Snapshot writing rejects a duplicate section name")]
		public void RejectsDuplicateSectionNames()
		{
			static void Act()
			{
				using var w = new SnapshotWriter(new MemoryStream(), TestHeader());
				w.BeginSection("A").Dispose();
				w.BeginSection("A").Dispose();
			}

			Assert.That(Act, Throws.TypeOf<InvalidOperationException>());
		}

		[TestCase(TestName = "Snapshot writing rejects nested sections")]
		public void ThrowsOnNestedSections()
		{
			static void Act()
			{
				using var w = new SnapshotWriter(new MemoryStream(), TestHeader());
				using var outer = w.BeginSection("A");
				w.BeginSection("B");
			}

			Assert.That(Act, Throws.TypeOf<InvalidOperationException>());
		}

		sealed class NonSeekableStream : MemoryStream
		{
			public NonSeekableStream(byte[] buffer)
				: base(buffer) { }

			public override bool CanSeek => false;
		}
	}
}
