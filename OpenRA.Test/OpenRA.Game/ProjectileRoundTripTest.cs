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
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using OpenRA.Effects;
using OpenRA.GameRules;
using OpenRA.GameSaves;
using OpenRA.Traits;

namespace OpenRA.Test
{
	[TestFixture]
	[NonParallelizable]
	sealed class ProjectileRoundTripTest
	{
		const string TestMap = "mods/ra/maps/agenda.oramap";

		const int TicksBeforeSave = 1;

		const int TicksAfterRestore = 1;

		const int Seed = 4242;

		[TestCase(TestName = "Round trip: TeslaZap")]
		public void TeslaZap()
		{
			AssertRoundTrip("teslazap");
		}

		[TestCase(TestName = "Round trip: Bullet")]
		public void Bullet()
		{
			AssertRoundTrip("25mm");
		}

		[TestCase(TestName = "Round trip: GravityBomb")]
		public void GravityBomb()
		{
			AssertRoundTrip("parabomb", launchHeight: 3072);
		}

		[TestCase(TestName = "Round trip: InstantHit")]
		public void InstantHit()
		{
			AssertRoundTrip("chaingun", ticksBeforeSave: 0);
		}

		[TestCase(TestName = "Round trip: Missile")]
		public void Missile()
		{
			AssertRoundTrip("dragon");
		}

		[TestCase(TestName = "Round trip: NukeLaunch")]
		public void NukeLaunch()
		{
			AssertRoundTrip(world => new OpenRA.Mods.Common.Effects.NukeLaunch(
				world.Players.First(p => !p.NonCombatant),
				"atomic", world.Map.Rules.Weapons["atomic"], "effect", "up", "down",
				new WPos(1024, 1024, 0), new WPos(5120, 5120, 0),
				WDist.Zero, true, new WDist(160), 0, 40, false,
				null, [], "effect", false, 0, 2), ticksAfterRestore: 45);
		}

		[TestCase(TestName = "Round trip: IonCannon")]
		public void IonCannon()
		{
			AssertRoundTrip(world => new OpenRA.Mods.Cnc.Effects.IonCannon(
				world.Players.First(p => !p.NonCombatant),
				world.Map.Rules.Weapons["atomic"], world, new WPos(1024, 1024, 0),
				Target.FromPos(new WPos(3072, 3072, 0)),
				"explosion", "piff", "effect", 7));
		}

		[TestCase(TestName = "Round trip: DropPodImpact")]
		public void DropPodImpact()
		{
			AssertRoundTrip(world => new OpenRA.Mods.Cnc.Effects.DropPodImpact(
				world.Players.First(p => !p.NonCombatant),
				world.Map.Rules.Weapons["atomic"], world, new WPos(1024, 1024, 0),
				Target.FromPos(new WPos(3072, 3072, 0)),
				7, "explosion", "piff", "effect"));
		}

		[TestCase(TestName = "Round trip: DelayedImpact")]
		public void DelayedImpact()
		{
			AssertRoundTrip(world =>
			{
				var weapon = world.Map.Rules.Weapons["atomic"];
				var firedBy = world.Actors.First(a => a.IsInWorld && a.OccupiesSpace != null && a.Owner != null);
				var target = Target.FromPos(firedBy.CenterPosition + new WVec(3072, 0, 0));

				return new OpenRA.Effects.DelayedImpact(20, weapon.Warheads[0], target,
					new WarheadArgs
					{
						Weapon = weapon,
						Source = firedBy.CenterPosition,
						World = world,
						SourceOwner = firedBy.Owner,
						SourceActor = firedBy,
						WeaponTarget = target
					});
			});
		}

		[TestCase(TestName = "Round trip: RevealShroudEffect")]
		public void RevealShroudEffect()
		{
			AssertRoundTrip(world => new OpenRA.Mods.Common.Effects.RevealShroudEffect(
				new WPos(3072, 3072, 0), new WDist(5120), Shroud.SourceType.Visibility,
				world.Players.First(p => !p.NonCombatant), PlayerRelationship.Ally, 0, 50));
		}

		[TestCase(TestName = "A restore does not draw from the shared random sequence")]
		public void RestoreDoesNotAdvanceSharedRandom()
		{
			var snapshot = SaveProjectile("teslazap", out _);

			var map = HeadlessGame.LoadMap(TestMap);
			using (var headless = HeadlessGame.CreateWorld(map, HeadlessGame.CreateSession(map, Seed)))
			{
				var before = headless.World.SharedRandom.Last;
				var count = headless.World.SharedRandom.TotalCount;

				Restore(headless.World, snapshot);

				Assert.That(headless.World.SharedRandom.Last, Is.EqualTo(before),
					"Restoring a projectile advanced the shared random sequence.");
				Assert.That(headless.World.SharedRandom.TotalCount, Is.EqualTo(count),
					"Restoring a projectile drew from the shared random sequence.");
			}
		}

		static void AssertRoundTrip(string weaponName, int launchHeight = 0, int ticksBeforeSave = TicksBeforeSave,
			int ticksAfterRestore = TicksAfterRestore)
		{
			AssertRoundTrip(world => Create(world, weaponName, launchHeight, out _), weaponName, ticksBeforeSave, ticksAfterRestore);
		}

		static void AssertRoundTrip(Func<World, IEffect> create, int ticksBeforeSave = TicksBeforeSave,
			int ticksAfterRestore = TicksAfterRestore)
		{
			AssertRoundTrip(create, "projectile", ticksBeforeSave, ticksAfterRestore);
		}

		static void AssertRoundTrip(Func<World, IEffect> create, string what, int ticksBeforeSave,
			int ticksAfterRestore = TicksAfterRestore)
		{
			var snapshot = SaveProjectile(create, what, out var expected, ticksBeforeSave);

			var map = HeadlessGame.LoadMap(TestMap);
			using (var headless = HeadlessGame.CreateWorld(map, HeadlessGame.CreateSession(map, Seed)))
			{
				var restored = Restore(headless.World, snapshot);

				Assert.That(restored.Count, Is.EqualTo(1), $"Expected one restored {what} projectile.");

				var detail = SnapshotDiff.Compare(expected, SnapshotDiff.Capture(headless.World));
				Assert.That(EffectLines(detail), Is.Empty,
					$"A restored {what} does not match the one that was saved.");

				Assert.That(Text(ReSave(headless.World)), Is.EqualTo(Text(snapshot)),
					$"A restored {what} does not save back to the state it was restored from.");

				headless.TickWorld(ticksAfterRestore);
			}
		}

		static List<MiniYamlNode> ReSave(World world)
		{
			var serializer = new EffectSerializer(HeadlessGame.EffectRegistry);
			using (var w = new SnapshotWriter(new MemoryStream(), Header()))
				return serializer.Save(world, w);
		}

		static string Text(List<MiniYamlNode> nodes)
		{
			return nodes.WriteToString();
		}

		static List<MiniYamlNode> SaveProjectile(string weaponName, out SnapshotDiff expected,
			int launchHeight = 0, int ticksBeforeSave = TicksBeforeSave)
		{
			return SaveProjectile(world => Create(world, weaponName, launchHeight, out _), weaponName,
				out expected, ticksBeforeSave);
		}

		static List<MiniYamlNode> SaveProjectile(Func<World, IEffect> create, string what,
			out SnapshotDiff expected, int ticksBeforeSave = TicksBeforeSave)
		{
			var map = HeadlessGame.LoadMap(TestMap);
			using (var headless = HeadlessGame.CreateWorld(map, HeadlessGame.CreateSession(map, Seed)))
			{
				var world = headless.World;
				var projectile = create(world);
				Assert.That(projectile, Is.Not.Null, $"'{what}' created no projectile.");

				world.Add(projectile);

				headless.TickWorld(ticksBeforeSave);

				expected = SnapshotDiff.Capture(world);

				var serializer = new EffectSerializer(HeadlessGame.EffectRegistry);
				using (var w = new SnapshotWriter(new MemoryStream(), Header()))
					return serializer.Save(world, w);
			}
		}

		static IEnumerable<string> EffectLines(string detail)
		{
			if (string.IsNullOrEmpty(detail))
				return [];

			return detail.Split('\n').Where(l => l.Contains("effect ", StringComparison.Ordinal));
		}

		static IProjectile Create(World world, string weaponName, int launchHeight, out Actor firedBy)
		{
			var weapon = world.Map.Rules.Weapons[weaponName];

			firedBy = world.Actors.FirstOrDefault(a => a.IsInWorld && a.OccupiesSpace != null && a.Owner != null);
			Assert.That(firedBy, Is.Not.Null, "No actor in the world to fire from.");

			var source = firedBy.CenterPosition + new WVec(0, 0, launchHeight);
			var target = firedBy.CenterPosition + new WVec(4096, 4096, 0);

			var args = new ProjectileArgs
			{
				Weapon = weapon,
				Facing = (target - source).Yaw,
				CurrentMuzzleFacing = () => (target - source).Yaw,
				DamageModifiers = [],
				InaccuracyModifiers = [],
				RangeModifiers = [],
				Source = source,
				CurrentSource = () => source,
				World = world,
				SourceActor = firedBy,
				SourceOwner = firedBy.Owner,
				PassiveTarget = target,
				GuidedTarget = Target.FromPos(target)
			};

			return weapon.Projectile.Create(args);
		}

		static List<IEffect> Restore(World world, List<MiniYamlNode> snapshot)
		{
			var yaml = MiniYaml.FromString(snapshot.WriteToString(), "test").ToList();

			var serializer = new EffectSerializer(HeadlessGame.EffectRegistry);
			using (var r = Reader(world))
			{
				serializer.Restore(world, yaml, r);
				r.RunDeferred();
			}

			return world.Effects.ToList();
		}

		static SnapshotHeader Header()
		{
			return new SnapshotHeader("test", "test", "1", "uid", 0, 0, 0, 0, 0, DateTime.UtcNow, SnapshotFlags.None);
		}

		static SnapshotReader Reader(World world)
		{
			var stream = new MemoryStream();
			using (var w = new SnapshotWriter(stream, Header()))
				w.WriteYamlSection("Empty", []);

			stream.Position = 0;
			return new SnapshotReader(stream, false, world);
		}
	}
}
