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
using System.Linq;
using NUnit.Framework;

namespace OpenRA.Test
{
	[TestFixture]
	[NonParallelizable]
	sealed class HeadlessWorldTest
	{
		const string TestMap = "mods/ra/maps/agenda.oramap";

		[TestCase(TestName = "Headless world loads and ticks without a renderer")]
		public void BootsAndTicks()
		{
			using var headless = HeadlessGame.CreateWorld(TestMap);
			var world = headless.World;

			Assert.That(world.Players, Is.Not.Empty, "World has no players.");

			Assert.That(world.LocalPlayer, Is.Not.Null, "The human client did not resolve to a player.");

			var actorsAtStart = world.Actors.Count();
			Assert.That(actorsAtStart, Is.GreaterThan(0), "Map actors and starting units were not created.");

			headless.Tick(100);

			Assert.That(world.WorldTick, Is.GreaterThan(0), "World did not advance.");
		}

		[TestCase(TestName = "Same seed produces the same sync hash every tick")]
		public void IsDeterministic()
		{
			const int Seed = 12345;
			const int Ticks = 200;

			var first = HashTrajectory(Seed, Ticks);
			var second = HashTrajectory(Seed, Ticks);

			for (var i = 0; i < Ticks; i++)
				Assert.That(second[i], Is.EqualTo(first[i]), $"Worlds diverged at tick {i}.");
		}

		[TestCase(TestName = "Different seeds produce different worlds")]
		public void SeedChangesTheWorld()
		{
			var a = HashTrajectory(1, 50);
			var b = HashTrajectory(999, 50);

			Assert.That(a, Is.Not.EqualTo(b), "Two different seeds produced identical worlds.");
		}

		static List<int> HashTrajectory(int seed, int ticks)
		{
			var map = HeadlessGame.LoadMap(TestMap);
			using var headless = HeadlessGame.CreateWorld(map, HeadlessGame.CreateSession(map, seed));

			var hashes = new List<int>(ticks);
			for (var i = 0; i < ticks; i++)
			{
				headless.Tick();
				hashes.Add(headless.World.SyncHash());
			}

			return hashes;
		}
	}
}
