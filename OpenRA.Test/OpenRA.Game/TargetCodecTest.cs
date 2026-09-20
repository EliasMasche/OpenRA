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

using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using OpenRA.Traits;

namespace OpenRA.Test
{
	[TestFixture]
	sealed class TargetCodecTest
	{
		static byte[] Write(in Target target)
		{
			var stream = new MemoryStream();
			using (var w = new BinaryWriter(stream, System.Text.Encoding.UTF8, true))
				TargetCodec.Write(w, target);

			return stream.ToArray();
		}

		static byte[] RoundTrip(byte[] bytes)
		{
			var read = TargetCodec.Read(null, new BinaryReader(new MemoryStream(bytes)));
			return Write(read);
		}

		[TestCase(TestName = "Target data persists over serialization (invalid)")]
		public void SerializeInvalid()
		{
			var bytes = Write(Target.Invalid);
			Assert.That(bytes, Has.Length.EqualTo(1));
			Assert.That(RoundTrip(bytes), Is.EqualTo(bytes));
		}

		static IEnumerable<TestCaseData> PositionTestCases()
		{
			return
			[
				new TestCaseData(WPos.Zero),
				new TestCaseData(new WPos(1, -2, 3)),
				new TestCaseData(new WPos(int.MinValue, 0, int.MaxValue)),
			];
		}

		[TestCaseSource(nameof(PositionTestCases))]
		public void SerializePosition(WPos pos)
		{
			var bytes = Write(Target.FromPos(pos));

			Assert.That(bytes, Has.Length.EqualTo(1 + 12 + 2));
			Assert.That(RoundTrip(bytes), Is.EqualTo(bytes));

			var restored = TargetCodec.Read(null, new BinaryReader(new MemoryStream(bytes)));
			Assert.That(restored.Type, Is.EqualTo(TargetType.Terrain));
			Assert.That(restored.CenterPosition, Is.EqualTo(pos));
		}

		[TestCase(TestName = "Target data persists over serialization (multiple positions)")]
		public void SerializeMultiplePositions()
		{
			var stream = new MemoryStream();
			var w = new BinaryWriter(stream);
			w.Write((byte)4);
			w.Write(1); w.Write(2); w.Write(3);
			w.Write((short)2);
			w.Write(10); w.Write(20); w.Write(30);
			w.Write(40); w.Write(50); w.Write(60);
			var bytes = stream.ToArray();

			Assert.That(RoundTrip(bytes), Is.EqualTo(bytes));

			var restored = TargetCodec.Read(null, new BinaryReader(new MemoryStream(bytes)));
			Assert.That(restored.CenterPosition, Is.EqualTo(new WPos(1, 2, 3)));
			Assert.That(restored.Positions, Is.EqualTo(new[] { new WPos(10, 20, 30), new WPos(40, 50, 60) }));
		}

		static IEnumerable<TestCaseData> UnresolvableTestCases()
		{
			return
			[
				new TestCaseData(new byte[] { 1, 0, 0, 0, 0, 0, 0, 0, 0 }).SetName("Actor"),
				new TestCaseData(new byte[] { 2, 0, 0, 0, 0, 0, 0, 0, 0 }).SetName("FrozenActor"),
				new TestCaseData(new byte[] { 3, 0, 0, 0, 0, 0 }).SetName("TerrainCell"),
			];
		}

		[TestCaseSource(nameof(UnresolvableTestCases))]
		public void ReadConsumesFullPayloadWithoutWorld(byte[] bytes)
		{
			var stream = new MemoryStream(bytes);
			var target = TargetCodec.Read(null, new BinaryReader(stream));

			Assert.That(target.Type, Is.EqualTo(TargetType.Invalid));
			Assert.That(stream.Position, Is.EqualTo(bytes.Length));
		}

		[TestCase(TestName = "Target deserialization rejects an unknown type tag")]
		public void RejectsUnknownTypeTag()
		{
			static void Act() => TargetCodec.Read(null, new BinaryReader(new MemoryStream([0xFF])));
			Assert.That(Act, Throws.TypeOf<InvalidDataException>());
		}

		[TestCase(TestName = "Target deserialization rejects a negative position count")]
		public void RejectsNegativePositionCount()
		{
			var stream = new MemoryStream();
			var w = new BinaryWriter(stream);
			w.Write((byte)4);
			w.Write(0); w.Write(0); w.Write(0);
			w.Write((short)-2);

			void Act() => TargetCodec.Read(null, new BinaryReader(new MemoryStream(stream.ToArray())));
			Assert.That(Act, Throws.TypeOf<InvalidDataException>());
		}
	}
}
