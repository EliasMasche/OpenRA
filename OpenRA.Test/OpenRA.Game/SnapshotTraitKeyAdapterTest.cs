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
using NUnit.Framework;
using OpenRA.GameSaves;

namespace OpenRA.Test
{
	[TestFixture]
	sealed class SnapshotTraitKeyAdapterTest
	{
		static List<(uint ActorID, string Trait, string Instance)> Traits()
		{
			return
			[
				(1, "SelectionInfo", null),
				(1, "ArmamentInfo", "primary"),
				(1, "ArmamentInfo", "secondary"),
				(2, "ControlGroupsInfo", null),
			];
		}

		static SnapshotTraitKey KeyOf((uint ActorID, string Trait, string Instance) t)
		{
			return new SnapshotTraitKey(t.ActorID, t.Trait, t.Instance);
		}

		[TestCase(TestName = "A stable key survives a trait being inserted before it")]
		public void KeySurvivesReordering()
		{
			var before = Traits();
			var target = KeyOf(before[2]);

			var after = Traits();
			after.Insert(0, (1, "HealthInfo", null));

			var oldIndex = before.FindIndex(t => KeyOf(t) == target);
			var newIndex = after.FindIndex(t => KeyOf(t) == target);

			Assert.That(newIndex, Is.Not.EqualTo(oldIndex), "the index moved, which is the failure the key prevents");
			Assert.That(KeyOf(after[newIndex]), Is.EqualTo(target), "the key still resolves to the same trait");
		}

		[TestCase(TestName = "Keys are unique across an actor's traits")]
		public void KeysAreUnique()
		{
			var keys = new HashSet<SnapshotTraitKey>();
			foreach (var t in Traits())
				Assert.That(keys.Add(KeyOf(t)), Is.True, $"duplicate key for {t.Trait}");

			Assert.That(keys, Has.Count.EqualTo(4));
		}

		[TestCase(TestName = "Two instances of one trait are addressed separately")]
		public void InstancesAreDistinct()
		{
			var primary = new SnapshotTraitKey(1, "ArmamentInfo", "primary");
			var secondary = new SnapshotTraitKey(1, "ArmamentInfo", "secondary");

			Assert.That(primary, Is.Not.EqualTo(secondary));
			Assert.That(primary.TraitName, Is.EqualTo(secondary.TraitName));
		}

		[TestCase(TestName = "The same trait on two actors gives different keys")]
		public void ActorIDDistinguishesKeys()
		{
			Assert.That(new SnapshotTraitKey(1, "SelectionInfo"), Is.Not.EqualTo(new SnapshotTraitKey(2, "SelectionInfo")));
		}
	}
}
