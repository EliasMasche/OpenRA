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
using System.Linq;
using NUnit.Framework;
using OpenRA.Mods.Common.Traits;
using OpenRA.Traits;

namespace OpenRA.Test
{
	[TestFixture]
	[NonParallelizable]
	sealed class BridgeRepairTest
	{
		const string TestMap = "mods/ra/maps/a-path-beyond.oramap";

		const int Seed = 4242;

		const int TicksToSettle = 3;

		[TestCase(TestName = "The test map builds bridges with huts")]
		public void MapHasBridges()
		{
			using var headless = HeadlessGame.CreateWorld(TestMap, Seed);
			headless.TickWorld(TicksToSettle);

			Assert.That(headless.World.ActorsWithTrait<Bridge>().Count(), Is.GreaterThan(0),
				"The map built no bridge spans.");
			Assert.That(headless.World.ActorsWithTrait<LegacyBridgeHut>().Count(), Is.GreaterThan(0),
				"The map built no bridge huts.");
		}

		[TestCase(TestName = "A repair walks the bridge one span at a time")]
		public void RepairPropagates()
		{
			using var headless = HeadlessGame.CreateWorld(TestMap, Seed);
			headless.TickWorld(TicksToSettle);

			var hut = LongestBridgeHut(headless.World, out var spans);
			Assert.That(spans.Count, Is.GreaterThan(1),
				"Propagation is only observable on a bridge of more than one span.");

			var repairer = AnyActor(headless.World);
			DamageAll(spans, repairer);

			hut.Repair(repairer);
			Assert.That(hut.Repairing, Is.True, "The hut did not start repairing.");

			headless.TickWorld(1);
			var afterFirstTick = spans.Count(s => s.Actor.Trait<Health>().DamageState == DamageState.Undamaged);
			Assert.That(afterFirstTick, Is.LessThan(spans.Count),
				"Every span was repaired at once, so the repair is not propagating.");

			headless.TickWorld(400);

			Assert.That(hut.Repairing, Is.False, "The repair never finished.");
			Assert.That(spans.All(s => s.Actor.Trait<Health>().DamageState == DamageState.Undamaged), Is.True,
				"The repair did not reach every span.");
		}

		[TestCase(TestName = "A repair interrupted by a snapshot finishes after the restore")]
		public void RepairSurvivesRoundTrip()
		{
			var stream = new MemoryStream();
			List<uint> spanIds;

			using (var headless = HeadlessGame.CreateWorld(TestMap, Seed))
			{
				headless.TickWorld(TicksToSettle);

				var hut = LongestBridgeHut(headless.World, out var spans);

				var repairer = AnyActor(headless.World);
				DamageAll(spans, repairer);
				hut.Repair(repairer);

				headless.TickWorld(2);
				Assert.That(hut.Repairing, Is.True, "The repair finished before the snapshot was taken.");

				spanIds = RemainingSpans(hut);
				Assert.That(spanIds, Is.Not.Empty, "The walk had nothing left to do when it was saved.");

				headless.SaveAtFrameEnd(stream);
				headless.TickWorld(1);
			}

			stream.Position = 0;

			var map = HeadlessGame.LoadMap(TestMap);
			using (var headless = HeadlessGame.CreateWorld(map, HeadlessGame.CreateSession(map, Seed), restoring: true))
			{
				headless.Restore(stream);

				var hut = headless.World.ActorsWithTrait<LegacyBridgeHut>()
					.Select(p => p.Trait)
					.FirstOrDefault(h => h.Repairing);

				Assert.That(hut, Is.Not.Null,
					"No hut came back still repairing, so the walk was lost on restore.");

				headless.TickWorld(400);

				Assert.That(hut.Repairing, Is.False, "The restored repair never finished.");

				var spans = headless.World.ActorsWithTrait<Bridge>()
					.Where(p => spanIds.Contains(p.Actor.ActorID))
					.ToList();

				Assert.That(spans, Is.Not.Empty, "The spans the walk was heading for are gone.");
				Assert.That(spans.All(s => s.Actor.Trait<Health>().DamageState == DamageState.Undamaged), Is.True,
					"The restored repair did not reach the spans it still had to cover.");
			}
		}

		static List<uint> RemainingSpans(LegacyBridgeHut hut)
		{
			var ids = new List<uint>();
			foreach (var (span, direction) in hut.PendingRepairs)
			{
				ids.Add(span.Actor.ActorID);
				foreach (var b in span.Enumerate(direction))
				{
					ids.Add(b.Actor.ActorID);
					if (b.RepairTerminatesHere(direction))
						break;
				}
			}

			return ids;
		}

		static LegacyBridgeHut LongestBridgeHut(World world, out List<TraitPair<Bridge>> spans)
		{
			var best = default(LegacyBridgeHut);
			var bestSpans = new List<TraitPair<Bridge>>();

			foreach (var pair in world.ActorsWithTrait<LegacyBridgeHut>())
			{
				var hut = pair.Trait;
				if (hut.Bridge == null)
					continue;

				var reachable = new List<TraitPair<Bridge>>();
				foreach (var span in world.ActorsWithTrait<Bridge>())
					if (Reaches(hut, span.Trait))
						reachable.Add(span);

				if (reachable.Count > bestSpans.Count)
				{
					best = hut;
					bestSpans = reachable;
				}
			}

			spans = bestSpans;
			return best;
		}

		static bool Reaches(LegacyBridgeHut hut, Bridge span)
		{
			if (hut.Bridge == span)
				return true;

			for (var d = 0; d <= 1; d++)
				foreach (var b in hut.Bridge.Enumerate(d))
					if (b == span)
						return true;

			return false;
		}

		static void DamageAll(List<TraitPair<Bridge>> spans, Actor source)
		{
			foreach (var span in spans)
			{
				var health = span.Actor.Trait<Health>();
				health.InflictDamage(span.Actor, source, new Damage(health.MaxHP / 2), true);
			}
		}

		static Actor AnyActor(World world)
		{
			return world.Actors.First(a => a.IsInWorld && a.Owner != null && a.OccupiesSpace != null);
		}
	}
}
