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
using System.IO;
using System.Linq;
using NUnit.Framework;
using OpenRA.GameSaves;

namespace OpenRA.Test
{
	[TestFixture]
	[NonParallelizable]
	sealed class SnapshotLobbyTest
	{
		const string TestMap = "mods/ra/maps/agenda.oramap";

		[TestCase(TestName = "Lobby section round trips without a world")]
		public void RoundTripsWithoutAWorld()
		{
			var map = HeadlessGame.LoadMap(TestMap);
			var session = HeadlessGame.CreateSession(map, seed: 4242, faction: "soviet", botTypes: "rush");
			var expected = SnapshotLobby.FromSession(session, HeadlessGame.ModData.MapCache[map.Uid]);

			var actual = RoundTrip(expected);

			Assert.That(actual, Is.Not.Null);
			Assert.That(actual.GlobalSettings.Map, Is.EqualTo(session.GlobalSettings.Map));
			Assert.That(actual.GlobalSettings.RandomSeed, Is.EqualTo(session.GlobalSettings.RandomSeed));

			Assert.That(actual.GlobalSettings.GameTimestep, Is.EqualTo(session.GlobalSettings.GameTimestep));

			Assert.That(actual.Slots.Keys, Is.EquivalentTo(expected.Slots.Keys));
			Assert.That(actual.SlotClients.Keys, Is.EquivalentTo(expected.SlotClients.Keys));

			foreach (var kv in expected.SlotClients)
			{
				var restored = actual.SlotClients[kv.Key];
				Assert.That(restored.Faction, Is.EqualTo(kv.Value.Faction), $"Faction for slot {kv.Key}.");
				Assert.That(restored.Color, Is.EqualTo(kv.Value.Color), $"Color for slot {kv.Key}.");
				Assert.That(restored.Team, Is.EqualTo(kv.Value.Team), $"Team for slot {kv.Key}.");
				Assert.That(restored.SpawnPoint, Is.EqualTo(kv.Value.SpawnPoint), $"SpawnPoint for slot {kv.Key}.");
				Assert.That(restored.Handicap, Is.EqualTo(kv.Value.Handicap), $"Handicap for slot {kv.Key}.");
				Assert.That(restored.Slot, Is.EqualTo(kv.Value.Slot), $"Slot for slot {kv.Key}.");
				Assert.That(restored.Bot, Is.EqualTo(kv.Value.Bot), $"Bot for slot {kv.Key}.");
				Assert.That(restored.BotName, Is.EqualTo(kv.Value.BotName), $"BotName for slot {kv.Key}.");
			}
		}

		[TestCase(TestName = "Lobby section records the bot that occupied a slot")]
		public void RecordsBots()
		{
			var map = HeadlessGame.LoadMap(TestMap);
			var session = HeadlessGame.CreateSession(map, seed: 4242, faction: "Random", botTypes: "rush");
			var lobby = RoundTrip(SnapshotLobby.FromSession(session, HeadlessGame.ModData.MapCache[map.Uid]));

			Assert.That(lobby.SlotClients.Values.Any(c => c.Bot == "rush"), Is.True,
				"The bot that occupied a slot was not recorded.");
		}

		[TestCase(TestName = "Absent lobby section reads as null")]
		public void AbsentSectionReadsAsNull()
		{
			using var ms = new MemoryStream();
			using (var w = new SnapshotWriter(ms, Header()))
				w.WriteYamlSection("Unrelated", []);

			ms.Position = 0;
			using var r = new SnapshotReader(ms);
			Assert.That(SnapshotLobby.Read(r), Is.Null);
		}

		static SnapshotLobby RoundTrip(SnapshotLobby lobby)
		{
			using var ms = new MemoryStream();
			using (var w = new SnapshotWriter(ms, Header()))
				lobby.Write(w);

			ms.Position = 0;
			using var r = new SnapshotReader(ms);
			return SnapshotLobby.Read(r);
		}

		static SnapshotHeader Header()
		{
			return new SnapshotHeader("test", "ra", "test", "uid", 0, 0, 0, 0, 0, DateTime.UtcNow, SnapshotFlags.None, 0, 0);
		}
	}
}
