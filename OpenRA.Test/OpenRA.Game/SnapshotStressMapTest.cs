#region Copyright & License Information
/*
 * Copyright (c) The OpenRA Developers and Contributors
 * This file is part of OpenRA, which is free software. It is made
 * available to you under the terms of the GNU General Public License
 * as published by the Free Software Foundation, either version 3 of
 * the License, or (at your option) any later version. For more information,
 * see COPYING.
 */
#endregion

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using OpenRA.GameSaves;
using OpenRA.Mods.Common.Traits;

namespace OpenRA.Test
{
	[TestFixture]
	sealed class SnapshotStressMapTest
	{
		const int DockingGaugeTicks = 300;

		const int TicksAfterRestore = 20;

		static readonly string TestMap = Path.GetFullPath(Path.Combine(TestContext.CurrentContext.TestDirectory, "..",
			"mods", "ra", "maps", "snapshot-stress"));

		static readonly int[] SaveTicks = [40, 120];

		[TestCase(40, TestName = "Stress map round trip at tick 40")]
		[TestCase(120, TestName = "Stress map round trip at tick 120")]
		public void RoundTrip(int ticks)
		{
			Assert.That(SaveTicks, Does.Contain(ticks), "the case list and the guarded ticks have drifted apart.");

			var map = HeadlessGame.LoadMap(TestMap);
			byte[] snapshot;
			SnapshotDiff savedDiff;
			int expectedHash;
			var effectsSeen = 0;

			using (var source = HeadlessGame.CreateWorld(map, HeadlessGame.CreateSession(map)))
			{
				for (var tick = 0; tick < ticks; tick++)
				{
					source.Tick();
					if (source.World.SyncedEffects.Any())
						effectsSeen++;
				}

				var world = source.World;
				savedDiff = source.CaptureDiff();
				AssertScenario(world, savedDiff, ticks);

				using (var ms = new MemoryStream())
				{
					source.Save(ms);
					snapshot = ms.ToArray();
				}

				expectedHash = world.SyncHash();
			}

			Assert.That(effectsSeen, Is.GreaterThan(0),
				$"no synced effect was ever in flight over {ticks} ticks, so the effect round trip is not covered.");

			var restoreMap = HeadlessGame.LoadMap(TestMap);
			using (var restored = HeadlessGame.CreateWorld(restoreMap, HeadlessGame.CreateSession(restoreMap), restoring: true))
			{
				using (var ms = new MemoryStream(snapshot))
					restored.Restore(ms, savedDiff: savedDiff);

				Assert.That(restored.World.SyncHash(), Is.EqualTo(expectedHash),
					"the restored world does not match the snapshot it was built from.");

				for (var i = 0; i < TicksAfterRestore; i++)
					restored.Tick();
			}
		}

		[TestCase(TestName = "Stress map reaches the docking process")]
		public void DockingIsReached()
		{
			var map = HeadlessGame.LoadMap(TestMap);
			using (var world = HeadlessGame.CreateWorld(map, HeadlessGame.CreateSession(map)))
			{
				var firstDocked = -1;
				for (var tick = 1; tick <= DockingGaugeTicks && firstDocked < 0; tick++)
				{
					world.Tick();
					if (NonDefaultEntries(world.CaptureDiff(), "DockHost").Any())
						firstDocked = tick;
				}

				Assert.That(firstDocked, Is.GreaterThan(0),
					$"No dock was occupied within {DockingGaugeTicks} ticks: the map no longer exercises docking.");
			}
		}

		static void AssertScenario(World world, SnapshotDiff diff, int ticks)
		{
			Assert.That(world.Actors.Count(), Is.GreaterThan(250),
				$"Only {world.Actors.Count()} actors at tick {ticks}: the map lost most of its content.");

			Assert.That(world.Players.Count(p => p.IsBot), Is.GreaterThan(0),
				"no bot player is seated, so nothing on the map acts on its own.");

			Assert.That(world.ActorsHavingTrait<Husk>().Any(), Is.True,
				"nothing has died, so death and husk state is not covered.");

			Assert.That(NonDefaultEntries(diff, "ChangesHealth").Any(), Is.True,
				"no actor is counting down damage, so the damage path is not covered.");

			Assert.That(NonDefaultEntries(diff, "AutoTarget").Any(), Is.True,
				"no actor is tracking a target, so the targeting path is not covered.");

			Assert.That(diff.Entries.Any(e => e.Trait.StartsWith("Attack", StringComparison.Ordinal) && e.Hash != 0), Is.True,
				"no attack trait holds a non-default value, so the aiming path is not covered.");
		}

		static IEnumerable<SnapshotDiff.Entry> NonDefaultEntries(SnapshotDiff diff, string trait)
		{
			return diff.Entries.Where(e => (e.Trait == trait || e.Trait.StartsWith(trait + "#", StringComparison.Ordinal)) && e.Hash != 0);
		}
	}
}
