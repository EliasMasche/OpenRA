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

using System.Linq;
using NUnit.Framework;
using OpenRA.GameRules;
using OpenRA.Traits;

namespace OpenRA.Test
{
	[TestFixture]
	[NonParallelizable]
	sealed class ProjectileScriptApiTest
	{
		const string TestMap = "mods/ra/maps/agenda.oramap";
		const int Seed = 4242;

		[TestCase(TestName = "A projectile in flight reports where it is and what fired it")]
		public void ProjectileReportsItsState()
		{
			using var headless = HeadlessGame.CreateWorld(TestMap, Seed);
			var world = headless.World;

			var projectile = Fire(world, "25mm", out var firedBy, out var target);
			world.Add(projectile);
			headless.TickWorld(1);

			var info = projectile as IProjectileScriptInfo;
			Assert.That(info, Is.Not.Null, "A Bullet does not expose itself to scripts.");

			Assert.That(info.SourceActor, Is.EqualTo(firedBy), "The projectile named the wrong firing actor.");
			Assert.That(info.Weapon, Is.EqualTo(world.Map.Rules.Weapons["25mm"]), "The projectile named the wrong weapon.");
			Assert.That(info.TargetPosition, Is.EqualTo(target), "The projectile named the wrong target.");

			Assert.That(info.Position, Is.Not.EqualTo(WPos.Zero), "The projectile reported no position.");
		}

		[TestCase(TestName = "ProjectilesInWorld returns the projectiles that are in flight")]
		public void QueryFindsProjectiles()
		{
			using var headless = HeadlessGame.CreateWorld(TestMap, Seed);
			var world = headless.World;

			Assert.That(InWorld(world), Is.Empty, "The world reported projectiles before any were fired.");

			world.Add(Fire(world, "25mm", out _, out _));
			headless.TickWorld(1);

			Assert.That(InWorld(world).Count, Is.EqualTo(1), "The fired projectile was not reported.");
		}

		[TestCase(TestName = "The circle query only returns projectiles inside the radius")]
		public void CircleQueryFilters()
		{
			using var headless = HeadlessGame.CreateWorld(TestMap, Seed);
			var world = headless.World;

			var projectile = Fire(world, "25mm", out _, out _);
			world.Add(projectile);
			headless.TickWorld(1);

			var info = (IProjectileScriptInfo)projectile;
			var here = info.Position;

			Assert.That(InCircle(world, here, new WDist(4096)), Is.Not.Empty,
				"A projectile inside the radius was not returned.");

			var elsewhere = here + new WVec(1000000, 1000000, 0);
			Assert.That(InCircle(world, elsewhere, new WDist(1024)), Is.Empty,
				"A projectile outside the radius was returned.");
		}

		static System.Collections.Generic.List<IProjectileScriptInfo> InWorld(World world)
		{
			return world.Effects.OfType<IProjectileScriptInfo>().ToList();
		}

		static System.Collections.Generic.List<IProjectileScriptInfo> InCircle(World world, WPos centre, WDist radius)
		{
			return world.Effects.OfType<IProjectileScriptInfo>()
				.Where(p => (p.Position - centre).LengthSquared <= radius.LengthSquared)
				.ToList();
		}

		static IProjectile Fire(World world, string weaponName, out Actor firedBy, out WPos target)
		{
			var weapon = world.Map.Rules.Weapons[weaponName];

			firedBy = world.Actors.First(a => a.IsInWorld && a.OccupiesSpace != null && a.Owner != null);

			var source = firedBy.CenterPosition;
			target = firedBy.CenterPosition + new WVec(4096, 4096, 0);
			var to = target;

			return weapon.Projectile.Create(new ProjectileArgs
			{
				Weapon = weapon,
				Facing = (to - source).Yaw,
				CurrentMuzzleFacing = () => (to - source).Yaw,
				DamageModifiers = [],
				InaccuracyModifiers = [],
				RangeModifiers = [],
				Source = source,
				CurrentSource = () => source,
				World = firedBy.World,
				SourceActor = firedBy,
				SourceOwner = firedBy.Owner,
				PassiveTarget = to,
				GuidedTarget = Target.FromPos(to)
			});
		}
	}
}
