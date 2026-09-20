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
using OpenRA.GameSaves;
using OpenRA.Traits;

namespace OpenRA.Test
{
	[TestFixture]
	sealed class SnapshotRefsTest
	{
		[TestCase(TestName = "A null actor round-trips as the null sentinel")]
		public void NullActor()
		{
			Assert.That(SnapshotRefs.FormatActor(null), Is.EqualTo(SnapshotRefs.Null));
			Assert.That(SnapshotRefs.ParseActor(null, SnapshotRefs.Null), Is.Null);
			Assert.That(SnapshotRefs.TryParseActorID(SnapshotRefs.Null, out _), Is.False);
		}

		[TestCase(TestName = "A null player round-trips as the null sentinel")]
		public void NullPlayer()
		{
			Assert.That(SnapshotRefs.FormatPlayer(null), Is.EqualTo(SnapshotRefs.Null));
			Assert.That(SnapshotRefs.ParsePlayer(null, SnapshotRefs.Null), Is.Null);
		}

		static IEnumerable<TestCaseData> ActorIDTestCases()
		{
			return
			[
				new TestCaseData("A:0", 0u).SetName("Zero"),
				new TestCaseData("A:42", 42u).SetName("Typical"),
				new TestCaseData("A:4294967295", uint.MaxValue).SetName("Maximum"),
				new TestCaseData("A:7:3", 7u).SetName("With generation"),
			];
		}

		[TestCaseSource(nameof(ActorIDTestCases))]
		public void ParsesActorID(string value, uint expected)
		{
			Assert.That(SnapshotRefs.TryParseActorID(value, out var actorID), Is.True);
			Assert.That(actorID, Is.EqualTo(expected));
		}

		[TestCase(TestName = "A dangling actor reference resolves to null")]
		public void DanglingActorResolvesToNull()
		{
			Assert.That(SnapshotRefs.ParseActor(null, "A:12"), Is.Null);
		}

		static IEnumerable<TestCaseData> MalformedActorTestCases()
		{
			return
			[
				new TestCaseData("A").SetName("No separator"),
				new TestCaseData("A:").SetName("Empty id"),
				new TestCaseData("A:x").SetName("Non-numeric id"),
				new TestCaseData("A:-1").SetName("Negative id"),
				new TestCaseData("A:1:2:3").SetName("Too many parts"),
				new TestCaseData("Z:1").SetName("Unknown prefix"),
			];
		}

		[TestCaseSource(nameof(MalformedActorTestCases))]
		public void RejectsMalformedActorReference(string value)
		{
			void Act() => SnapshotRefs.TryParseActorID(value, out _);
			Assert.That(Act, Throws.TypeOf<InvalidDataException>());
		}

		static IEnumerable<TestCaseData> MalformedPlayerTestCases()
		{
			return
			[
				new TestCaseData("P").SetName("No separator"),
				new TestCaseData("P:").SetName("Empty name"),
				new TestCaseData("Z:Multi0").SetName("Unknown prefix"),
			];
		}

		[TestCaseSource(nameof(MalformedPlayerTestCases))]
		public void RejectsMalformedPlayerReference(string value)
		{
			void Act() => SnapshotRefs.ParsePlayer(null, value);
			Assert.That(Act, Throws.TypeOf<InvalidDataException>());
		}

		[TestCase(TestName = "A player name containing a separator round-trips")]
		public void PlayerNameWithSeparator()
		{
			Assert.That(SnapshotRefs.ParsePlayer(null, "P:a:b"), Is.Null);

			static void Act() => SnapshotRefs.ParsePlayer(null, "P:a:b");
			Assert.That(Act, Throws.Nothing);
		}

		static string RoundTripTarget(in Target target)
		{
			var formatted = SnapshotRefs.FormatTarget(target);
			return SnapshotRefs.FormatTarget(SnapshotRefs.ParseTarget(null, formatted));
		}

		[TestCase(TestName = "An invalid target round-trips")]
		public void InvalidTarget()
		{
			Assert.That(SnapshotRefs.FormatTarget(Target.Invalid), Is.EqualTo("Invalid"));
			Assert.That(SnapshotRefs.ParseTarget(null, "Invalid").Type, Is.EqualTo(TargetType.Invalid));
			Assert.That(RoundTripTarget(Target.Invalid), Is.EqualTo("Invalid"));
		}

		static IEnumerable<TestCaseData> PositionTestCases()
		{
			return
			[
				new TestCaseData(WPos.Zero).SetName("Zero"),
				new TestCaseData(new WPos(1, -2, 3)).SetName("Mixed signs"),
				new TestCaseData(new WPos(int.MinValue, 0, int.MaxValue)).SetName("Extremes"),
			];
		}

		[TestCaseSource(nameof(PositionTestCases))]
		public void PositionTargetRoundTrips(WPos pos)
		{
			var target = Target.FromPos(pos);
			var formatted = SnapshotRefs.FormatTarget(target);

			var restored = SnapshotRefs.ParseTarget(null, formatted);
			Assert.That(restored.Type, Is.EqualTo(TargetType.Terrain));
			Assert.That(restored.CenterPosition, Is.EqualTo(pos));
			Assert.That(RoundTripTarget(target), Is.EqualTo(formatted));
		}

		[TestCase(TestName = "A multi-position target round-trips")]
		public void MultiplePositionsRoundTrip()
		{
			const string Formatted = "TP:1,2,3;10,20,30;40,50,60";
			var restored = SnapshotRefs.ParseTarget(null, Formatted);

			Assert.That(restored.CenterPosition, Is.EqualTo(new WPos(1, 2, 3)));
			Assert.That(restored.Positions, Is.EqualTo(new[] { new WPos(10, 20, 30), new WPos(40, 50, 60) }));
			Assert.That(SnapshotRefs.FormatTarget(restored), Is.EqualTo(Formatted));
		}

		[TestCase(TestName = "A single implicit position is not stored twice")]
		public void ImplicitPositionIsNotDuplicated()
		{
			Assert.That(SnapshotRefs.FormatTarget(Target.FromPos(new WPos(1, 2, 3))), Is.EqualTo("TP:1,2,3"));
		}

		static IEnumerable<TestCaseData> UnresolvableTargetTestCases()
		{
			return
			[
				new TestCaseData("A:1:0").SetName("Actor"),
				new TestCaseData("F:Multi0:1").SetName("FrozenActor"),
				new TestCaseData("C:0").SetName("TerrainCell"),
				new TestCaseData("C:0:1").SetName("TerrainCell with subcell"),
			];
		}

		[TestCaseSource(nameof(UnresolvableTargetTestCases))]
		public void UnresolvableTargetsBecomeInvalid(string value)
		{
			Assert.That(SnapshotRefs.ParseTarget(null, value).Type, Is.EqualTo(TargetType.Invalid));
		}

		[TestCase(TestName = "A frozen actor target keys the viewer by internal name")]
		public void FrozenActorUsesInternalName()
		{
			static void Act() => SnapshotRefs.ParseTarget(null, "F:a:b:12");
			Assert.That(Act, Throws.Nothing);
		}

		static IEnumerable<TestCaseData> MalformedTargetTestCases()
		{
			return
			[
				new TestCaseData("A:1").SetName("Actor without generation"),
				new TestCaseData("A:x:0").SetName("Actor with non-numeric id"),
				new TestCaseData("F:Multi0").SetName("Frozen actor without id"),
				new TestCaseData("F:Multi0:x").SetName("Frozen actor with non-numeric id"),
				new TestCaseData("F::1").SetName("Frozen actor with empty viewer"),
				new TestCaseData("C:x").SetName("Cell with non-numeric bits"),
				new TestCaseData("C:0:1:2").SetName("Cell with too many parts"),
				new TestCaseData("TP:").SetName("Position with empty payload"),
				new TestCaseData("TP:1,2").SetName("Position with too few coordinates"),
				new TestCaseData("TP:1,2,x").SetName("Position with non-numeric coordinate"),
				new TestCaseData("TP:1,2,3;4,5").SetName("Trailing position malformed"),
				new TestCaseData("Q:1").SetName("Unknown prefix"),
			];
		}

		[TestCaseSource(nameof(MalformedTargetTestCases))]
		public void RejectsMalformedTarget(string value)
		{
			void Act() => SnapshotRefs.ParseTarget(null, value);
			Assert.That(Act, Throws.TypeOf<InvalidDataException>());
		}

		[TestCase(TestName = "An empty or null target reference is invalid, not an error")]
		public void EmptyTargetIsInvalid()
		{
			Assert.That(SnapshotRefs.ParseTarget(null, null).Type, Is.EqualTo(TargetType.Invalid));
			Assert.That(SnapshotRefs.ParseTarget(null, "").Type, Is.EqualTo(TargetType.Invalid));
			Assert.That(SnapshotRefs.ParseTarget(null, SnapshotRefs.Null).Type, Is.EqualTo(TargetType.Invalid));
		}
	}
}
