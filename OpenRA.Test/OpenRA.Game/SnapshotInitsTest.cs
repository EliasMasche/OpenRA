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

using NUnit.Framework;
using OpenRA.GameSaves;
using OpenRA.Traits;

namespace OpenRA.Test
{
	[TestFixture]
	sealed class SnapshotInitsTest
	{
		[TestCase(TestName = "Placement values round trip through their init loaders")]
		public void PlacementValuesRoundTrip()
		{
			var location = new CPos(12, 34);
			Assert.That(FieldLoader.GetValue<CPos>("value", FieldSaver.FormatValue(location)), Is.EqualTo(location));

			var position = new WPos(1024, 2048, 512);
			Assert.That(FieldLoader.GetValue<WPos>("value", FieldSaver.FormatValue(position)), Is.EqualTo(position));

			var facing = new WAngle(384);
			Assert.That(FieldLoader.GetValue<WAngle>("value", FieldSaver.FormatValue(facing)), Is.EqualTo(facing));
		}

		[TestCase(TestName = "Trait instances on one actor keep distinct keys")]
		public void TraitInstancesKeepDistinctKeys()
		{
			var rush = new SnapshotTraitKey(3, "BaseBuilderBotModuleInfo", "rush");
			var normal = new SnapshotTraitKey(3, "BaseBuilderBotModuleInfo", "normal");

			Assert.That(rush.ToString(), Is.Not.EqualTo(normal.ToString()));
			Assert.That(SnapshotTraitKey.Parse(rush.ToString()), Is.EqualTo(rush));
			Assert.That(SnapshotTraitKey.Parse(normal.ToString()), Is.EqualTo(normal));
		}

		[TestCase(TestName = "A sub-cell is written as a number, not an enum name")]
		public void SubCellIsWrittenAsNumber()
		{
			const SubCell Value = SubCell.First;

			Assert.That(FieldSaver.FormatValue(Value), Is.EqualTo("First"));
			Assert.That(FieldLoader.GetValue<int>("value", ((int)Value).ToStringInvariant()), Is.EqualTo((int)Value));
		}
	}
}
