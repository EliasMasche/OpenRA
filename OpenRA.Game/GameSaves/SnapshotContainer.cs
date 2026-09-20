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
using System.Text;

namespace OpenRA.GameSaves
{
	/// <summary>Constants and primitives that the snapshot reader and writer share.</summary>
	/// <remarks>
	/// The layout is a magic string, the header, the section payloads, and a section table. The table sits
	/// at the end of the file, and the last twelve bytes point back to it. A writer can therefore add a
	/// section without knowing its length in advance.
	/// </remarks>
	public static class SnapshotContainer
	{
		public const int SectionTableMarker = -0x5041;
		public const int EndMarker = -0x5042;

		public const int TrailerLength = 8 + 4;

		public const int MaxSections = 1024;

		public const int MaxSectionNameLength = 256;

		public static readonly byte[] Magic = "ORASNAP\0"u8.ToArray();

		internal static void WriteInt64(Stream s, long value)
		{
			s.Write(value);
		}

		internal static long ReadInt64(Stream s)
		{
			Span<byte> buffer = stackalloc byte[8];
			s.ReadBytes(buffer);
			return BitConverter.ToInt64(buffer);
		}

		internal static void WriteSectionName(Stream s, string name)
		{
			s.WriteLengthPrefixedString(Encoding.UTF8, name);
		}

		internal static string ReadSectionName(Stream s)
		{
			return s.ReadLengthPrefixedString(Encoding.UTF8, MaxSectionNameLength);
		}
	}
}
