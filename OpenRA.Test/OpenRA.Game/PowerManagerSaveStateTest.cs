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
using OpenRA.Mods.Common.Traits;

namespace OpenRA.Test
{
	[TestFixture]
	[NonParallelizable]
	sealed class PowerManagerSaveStateTest
	{
		const string TestMap = "mods/ra/maps/agenda.oramap";
		const int Seed = 4242;

		const int TicksBeforeSave = 60;

		[TestCase(TestName = "A snapshot carries every player's power section")]
		public void TotalsAreWrittenToTheSnapshot()
		{
			var map = HeadlessGame.LoadMap(TestMap);
			using var source = HeadlessGame.CreateWorld(map, HeadlessGame.CreateSession(map, Seed));
			source.Tick(TicksBeforeSave);

			using var ms = new MemoryStream();
			source.Save(ms);
			ms.Position = 0;

			using var reader = new SnapshotReader(ms);
			foreach (var player in source.World.Players)
				Assert.That(reader.SectionNames, Does.Contain("PowerManager/" + player.InternalName),
					$"no power section for {player.InternalName}");
		}

		[TestCase(TestName = "Restored power totals match the saved ones")]
		public void TotalsSurviveARoundTrip()
		{
			var expected = new Dictionary<string, (int Provided, int Drained)>();
			byte[] snapshot;

			var map = HeadlessGame.LoadMap(TestMap);
			using (var source = HeadlessGame.CreateWorld(map, HeadlessGame.CreateSession(map, Seed)))
			{
				source.Tick(TicksBeforeSave);

				foreach (var player in source.World.Players)
				{
					var power = player.PlayerActor.Trait<PowerManager>();
					expected[player.InternalName] = (power.PowerProvided, power.PowerDrained);
				}

				using var ms = new MemoryStream();
				source.Save(ms);
				snapshot = ms.ToArray();
			}

			var restoreMap = HeadlessGame.LoadMap(TestMap);
			using var restored = HeadlessGame.CreateWorld(restoreMap, HeadlessGame.CreateSession(restoreMap, Seed), restoring: true);
			using (var ms = new MemoryStream(snapshot))
				restored.Restore(ms);

			foreach (var player in restored.World.Players)
			{
				var power = player.PlayerActor.Trait<PowerManager>();
				Assert.That((power.PowerProvided, power.PowerDrained), Is.EqualTo(expected[player.InternalName]),
					$"power totals for {player.InternalName}");
			}
		}
	}
}
