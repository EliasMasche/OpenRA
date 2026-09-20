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

namespace OpenRA.GameSaves
{
	/// <summary>How a section payload is stored.</summary>
	public enum SnapshotSectionEncoding : byte
	{
		Raw = 0,
		Deflate = 1,
	}

	/// <summary>One entry in the section table of a snapshot.</summary>
	public sealed class SnapshotSection
	{
		public readonly string Name;

		public readonly long Offset;

		public readonly long Length;

		public readonly SnapshotSectionEncoding Encoding;

		public readonly long UncompressedLength;

		public SnapshotSection(string name, long offset, long length, SnapshotSectionEncoding encoding, long uncompressedLength)
		{
			Name = name;
			Offset = offset;
			Length = length;
			Encoding = encoding;
			UncompressedLength = uncompressedLength;
		}
	}
}
