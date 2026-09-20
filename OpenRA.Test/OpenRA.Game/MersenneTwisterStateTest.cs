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
using NUnit.Framework;
using OpenRA.Support;

namespace OpenRA.Test
{
	[TestFixture]
	sealed class MersenneTwisterStateTest
	{
		const int Seed = 12345;

		static MersenneTwisterState RoundTrip(MersenneTwisterState state)
		{
			var stream = new MemoryStream();
			state.Write(stream);
			stream.Seek(0, SeekOrigin.Begin);
			return MersenneTwisterState.Read(stream);
		}

		[TestCase(0)]
		[TestCase(1)]
		[TestCase(623)]
		[TestCase(624)]
		[TestCase(625)]
		[TestCase(1000)]
		public void RestoresSequenceAfterRoundTrip(int drawnBeforeSave)
		{
			var original = new MersenneTwister(Seed);
			for (var i = 0; i < drawnBeforeSave; i++)
				original.NextUint();

			var restored = new MersenneTwister(Seed + 1);
			restored.RestoreState(RoundTrip(original.SaveState()));

			var expected = Enumerable.Range(0, 100).Select(_ => original.NextUint()).ToArray();
			var actual = Enumerable.Range(0, 100).Select(_ => restored.NextUint()).ToArray();
			Assert.That(actual, Is.EqualTo(expected));
		}

		[TestCase(TestName = "Mersenne Twister Last and TotalCount survive a round trip")]
		public void PreservesLastAndTotalCount()
		{
			var original = new MersenneTwister(Seed);
			for (var i = 0; i < 50; i++)
				original.NextUint();

			var restored = new MersenneTwister(Seed + 1);
			restored.RestoreState(RoundTrip(original.SaveState()));

			Assert.That(restored.Last, Is.EqualTo(original.Last));
			Assert.That(restored.TotalCount, Is.EqualTo(original.TotalCount));
		}

		[TestCase(TestName = "Mersenne Twister state serializes to a fixed length")]
		public void SerializesToFixedLength()
		{
			var stream = new MemoryStream();
			new MersenneTwister(Seed).SaveState().Write(stream);
			Assert.That(stream.Length, Is.EqualTo(MersenneTwisterState.SerializedLength));
		}

		[TestCase(TestName = "Mersenne Twister state rejects a wrongly sized vector")]
		public void RejectsWrongStateVectorLength()
		{
			static void Act() => new MersenneTwisterState(new uint[16], 0, 0, 0);
			Assert.That(Act, Throws.TypeOf<ArgumentException>());
		}

		[TestCase(-1)]
		[TestCase(MersenneTwisterState.StateLength)]
		public void RejectsOutOfRangeIndex(int index)
		{
			void Act() => new MersenneTwisterState(new uint[MersenneTwisterState.StateLength], index, 0, 0);
			Assert.That(Act, Throws.TypeOf<ArgumentOutOfRangeException>());
		}

		[TestCase(TestName = "Mersenne Twister state rejects a corrupt serialized index")]
		public void RejectsCorruptSerializedIndex()
		{
			var stream = new MemoryStream();
			new MersenneTwister(Seed).SaveState().Write(stream);

			var bytes = stream.ToArray();
			BitConverter.TryWriteBytes(bytes.AsSpan(MersenneTwisterState.StateLength * 4), MersenneTwisterState.StateLength);

			void Act() => MersenneTwisterState.Read(new MemoryStream(bytes));
			Assert.That(Act, Throws.TypeOf<InvalidDataException>());
		}

		[TestCase(TestName = "Mersenne Twister state copies the vector it is given")]
		public void StateIsDefensivelyCopied()
		{
			var vector = new uint[MersenneTwisterState.StateLength];
			var state = new MersenneTwisterState(vector, 0, 0, 0);

			vector[0] = 0xDEADBEEF;

			Assert.That(state.GetStateVector()[0], Is.EqualTo(0u));
		}
	}
}
