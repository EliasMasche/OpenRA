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
using System.Linq;
using OpenRA.Network;

namespace OpenRA.GameSaves
{
	/// <summary>The file layouts that a save browser can meet.</summary>
	public enum SaveFileFormat
	{
		Unknown,

		LegacyReplay,

		Snapshot,
	}

	/// <summary>Tells a snapshot from a legacy replay without reading the whole file.</summary>
	/// <remarks>
	/// A snapshot starts with the magic string. A legacy replay ends with the end-of-file marker, so the
	/// test reads both ends of the file. The stream position is restored afterwards.
	/// </remarks>
	public static class SaveFileFormatDetector
	{
		public static SaveFileFormat Detect(Stream s)
		{
			ArgumentNullException.ThrowIfNull(s);

			if (!s.CanSeek || s.Length < SnapshotContainer.Magic.Length)
				return SaveFileFormat.Unknown;

			var position = s.Position;
			try
			{
				s.Seek(0, SeekOrigin.Begin);
				if (s.ReadBytes(SnapshotContainer.Magic.Length).SequenceEqual(SnapshotContainer.Magic))
					return SaveFileFormat.Snapshot;

				if (s.Length < 4)
					return SaveFileFormat.Unknown;

				s.Seek(-4, SeekOrigin.End);
				if (s.ReadInt32() == GameSave.EOFMarker)
					return SaveFileFormat.LegacyReplay;

				return SaveFileFormat.Unknown;
			}
			finally
			{
				s.Seek(position, SeekOrigin.Begin);
			}
		}

		public static SaveFileFormat Detect(string path)
		{
			try
			{
				using (var s = File.OpenRead(path))
					return Detect(s);
			}
			catch (Exception)
			{
				return SaveFileFormat.Unknown;
			}
		}
	}
}
