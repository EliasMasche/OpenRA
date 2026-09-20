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

namespace OpenRA.Support
{
	public sealed class MersenneTwisterState
	{
		public const int StateLength = 624;

		public const int SerializedLength = StateLength * 4 + 3 * 4;

		readonly uint[] mt;

		public readonly int Index;

		public readonly int Last;

		public readonly int TotalCount;

		public MersenneTwisterState(uint[] mt, int index, int last, int totalCount)
		{
			ArgumentNullException.ThrowIfNull(mt);
			if (mt.Length != StateLength)
				throw new ArgumentException($"State vector must be {StateLength} words long, but was {mt.Length}.", nameof(mt));

			if (index < 0 || index >= StateLength)
				throw new ArgumentOutOfRangeException(nameof(index), index, $"Index must be within [0, {StateLength}).");

			this.mt = (uint[])mt.Clone();
			Index = index;
			Last = last;
			TotalCount = totalCount;
		}

		public uint[] GetStateVector()
		{
			return (uint[])mt.Clone();
		}

		public void Write(Stream s)
		{
			ArgumentNullException.ThrowIfNull(s);

			var buffer = new byte[SerializedLength];
			var offset = 0;
			foreach (var word in mt)
			{
				BitConverter.TryWriteBytes(buffer.AsSpan(offset), word);
				offset += 4;
			}

			BitConverter.TryWriteBytes(buffer.AsSpan(offset), Index);
			BitConverter.TryWriteBytes(buffer.AsSpan(offset + 4), Last);
			BitConverter.TryWriteBytes(buffer.AsSpan(offset + 8), TotalCount);

			s.Write(buffer, 0, buffer.Length);
		}

		public static MersenneTwisterState Read(Stream s)
		{
			ArgumentNullException.ThrowIfNull(s);

			var buffer = new byte[SerializedLength];
			s.ReadBytes(buffer, 0, buffer.Length);

			var mt = new uint[StateLength];
			var offset = 0;
			for (var i = 0; i < StateLength; i++)
			{
				mt[i] = BitConverter.ToUInt32(buffer, offset);
				offset += 4;
			}

			var index = BitConverter.ToInt32(buffer, offset);
			var last = BitConverter.ToInt32(buffer, offset + 4);
			var totalCount = BitConverter.ToInt32(buffer, offset + 8);

			if (index < 0 || index >= StateLength)
				throw new InvalidDataException($"Mersenne Twister state index {index} is outside [0, {StateLength}).");

			return new MersenneTwisterState(mt, index, last, totalCount);
		}
	}
}
