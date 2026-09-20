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
using System.IO;
using NUnit.Framework;
using OpenRA.GameSaves;
using OpenRA.Network;

namespace OpenRA.Test
{
	[TestFixture]
	sealed class SaveFileFormatDetectorTest
	{
		static byte[] WriteSnapshot()
		{
			var stream = new MemoryStream();
			var header = new SnapshotHeader("engine", "ra", "1.0", "uid", 0, 0, 0, 0, 0, DateTime.UtcNow, SnapshotFlags.None);
			using (var w = new SnapshotWriter(stream, header))
			using (var s = w.BeginSection("A"))
				s.WriteByte(1);

			return stream.ToArray();
		}

		static byte[] WriteLegacySave()
		{
			var stream = new MemoryStream();
			stream.Write(0x11223344);
			stream.Write(GameSave.EOFMarker);
			return stream.ToArray();
		}

		[TestCase(TestName = "Save format detection recognises a snapshot")]
		public void DetectsSnapshot()
		{
			Assert.That(SaveFileFormatDetector.Detect(new MemoryStream(WriteSnapshot())), Is.EqualTo(SaveFileFormat.Snapshot));
		}

		[TestCase(TestName = "Save format detection recognises a legacy save")]
		public void DetectsLegacyReplay()
		{
			Assert.That(SaveFileFormatDetector.Detect(new MemoryStream(WriteLegacySave())), Is.EqualTo(SaveFileFormat.LegacyReplay));
		}

		[TestCase(0)]
		[TestCase(3)]
		[TestCase(64)]
		public void DetectsUnknown(int length)
		{
			Assert.That(SaveFileFormatDetector.Detect(new MemoryStream(new byte[length])), Is.EqualTo(SaveFileFormat.Unknown));
		}

		[TestCase(TestName = "Save format detection leaves the stream position alone")]
		public void PreservesStreamPosition()
		{
			var s = new MemoryStream(WriteSnapshot());
			s.Seek(5, SeekOrigin.Begin);

			SaveFileFormatDetector.Detect(s);

			Assert.That(s.Position, Is.EqualTo(5));
		}

		[TestCase(TestName = "Save format detection rejects a non-seekable stream")]
		public void DetectsUnknownForNonSeekableStream()
		{
			Assert.That(SaveFileFormatDetector.Detect(new NonSeekableStream(WriteSnapshot())), Is.EqualTo(SaveFileFormat.Unknown));
		}

		[TestCase(TestName = "Save format detection reports a missing file as unknown")]
		public void DetectsUnknownForMissingFile()
		{
			var path = Path.Combine(Path.GetTempPath(), "openra-test-missing-" + Guid.NewGuid().ToString("N") + ".orasav");
			Assert.That(SaveFileFormatDetector.Detect(path), Is.EqualTo(SaveFileFormat.Unknown));
		}

		sealed class NonSeekableStream : MemoryStream
		{
			public NonSeekableStream(byte[] buffer)
				: base(buffer) { }

			public override bool CanSeek => false;
		}
	}
}
