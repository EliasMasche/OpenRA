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
using OpenRA.GameSaves;
using OpenRA.Mods.Common.Traits;
using OpenRA.Traits;

namespace OpenRA.Test
{
	[TestFixture]
	[NonParallelizable]
	sealed class SnapshotRoundTripTest
	{
		const string TestMap = "mods/ra/maps/agenda.oramap";

		const int TicksBeforeSave = 60;

		const int TicksAfterRestore = 40;

		[TestCase(TestName = "Round trip: idle world")]
		public void IdleWorld()
		{
			AssertRoundTrip(_ => { });
		}

		[TestCase(TestName = "Round trip: units moving")]
		public void UnitsMoving()
		{
			AssertRoundTrip(headless =>
			{
				var world = headless.World;
				var movers = world.Actors
					.Where(a => a.IsInWorld && a.Owner != null && !a.Owner.NonCombatant && a.TraitOrDefault<Mobile>() != null)
					.Take(5)
					.ToList();

				Assert.That(movers, Is.Not.Empty, "No mobile actors to order.");

				foreach (var a in movers)
				{
					var destination = a.Location + new CVec(3, 3);
					world.IssueOrder(new Order("Move", a, Target.FromCell(world, destination), false));
				}

				headless.Tick(20);
			});
		}

		[TestCase(TestName = "Round trip: damaged units")]
		public void DamagedUnits()
		{
			AssertRoundTrip(headless =>
			{
				var world = headless.World;
				var targets = world.Actors
					.Where(a => a.IsInWorld && a.TraitOrDefault<Health>() != null)
					.Take(5)
					.ToList();

				Assert.That(targets, Is.Not.Empty, "No actors with health to damage.");

				foreach (var a in targets)
				{
					var health = a.Trait<Health>();
					health.InflictDamage(a, a, new Damage(health.MaxHP / 4), true);
				}
			});
		}

		[TestCase(TestName = "Round trip: production and economy")]
		public void ProductionAndEconomy()
		{
			AssertRoundTrip(headless =>
			{
				var world = headless.World;
				var player = world.Players.FirstOrDefault(p => !p.NonCombatant);
				Assert.That(player, Is.Not.Null, "No playable player.");

				player.PlayerActor.Trait<PlayerResources>().GiveCash(5000);

				var queue = world.ActorsWithTrait<ProductionQueue>()
					.FirstOrDefault(q => q.Actor.Owner == player && q.Trait.Enabled);

				if (queue.Trait != null)
				{
					var buildable = queue.Trait.AllItems().FirstOrDefault();
					if (buildable != null)
						world.IssueOrder(Order.StartProduction(queue.Actor, buildable.Name, 1));
				}

				headless.Tick(20);
			});
		}

		[TestCase(TestName = "Round trip: projectiles in flight")]
		public void ProjectilesInFlight()
		{
			AssertRoundTrip(headless =>
			{
				var world = headless.World;
				var armed = world.Actors
					.Where(a => a.IsInWorld && a.TraitsImplementing<Armament>().Any())
					.Take(2)
					.ToList();

				foreach (var a in armed)
				{
					var target = world.Actors.FirstOrDefault(t => t.IsInWorld && t.Owner != a.Owner && t.TraitOrDefault<Health>() != null);
					if (target != null)
						world.IssueOrder(new Order("Attack", a, Target.FromActor(target), false));
				}

				headless.Tick(30);
			});
		}

		[TestCase(TestName = "Round trip: harvester mid-harvest")]
		public void HarvesterMidHarvest()
		{
			AssertRoundTrip(headless => headless.Tick(150));
		}

		[TestCase(TestName = "Requested save is written to the save directory")]
		public void RequestedSaveIsWrittenToDisk()
		{
			var map = HeadlessGame.LoadMap(TestMap);
			using var source = HeadlessGame.CreateWorld(map, HeadlessGame.CreateSession(map, 4242));
			source.Tick(TicksBeforeSave);

			const string Filename = "test-save" + SavePaths.Extension;
			var path = Path.Combine(SavePaths.BaseSaveDirectory(HeadlessGame.ModData.Manifest), Filename);

			try
			{
				source.World.RequestGameSave(Filename, isAutosave: true);
				Assert.That(File.Exists(path), Is.False, "The save was written before the tick ended.");

				source.Tick();

				Assert.That(File.Exists(path), Is.True, "The save was not written.");
				Assert.That(File.Exists(path + ".tmp"), Is.False, "The temporary file was left behind.");

				using var stream = File.OpenRead(path);
				Assert.That(SaveFileFormatDetector.Detect(stream), Is.EqualTo(SaveFileFormat.Snapshot));

				using var reader = new SnapshotReader(stream);
				Assert.That(reader.Header.MapUid, Is.EqualTo(map.Uid));
				Assert.That(reader.Header.Flags, Is.EqualTo(SnapshotFlags.Autosave));
				Assert.That(SnapshotLobby.Read(reader), Is.Not.Null,
					"A save written from a game carries the session it was played in.");
			}
			finally
			{
				File.Delete(path);
			}
		}

		[TestCase(TestName = "Every map saves as a snapshot, and a scripted one also records orders")]
		public void ScriptedMapsAlsoUseSnapshots()
		{
			var scripted = HeadlessGame.LoadMap("ra|maps/allies-01");
			Assert.That(UsesSnapshotSaves(scripted), Is.True,
				"A scripted map attempts a snapshot, and falls back to a replay only when one refuses.");

			Assert.That(NeedsOrderStream(scripted), Is.True,
				"A scripted map must keep its order stream, or a refused snapshot has nothing to fall back to.");

			var map = HeadlessGame.LoadMap(TestMap);
			Assert.That(UsesSnapshotSaves(map), Is.True, "An unscripted map should save as a snapshot.");

			Assert.That(NeedsOrderStream(map), Is.False,
				"An unscripted map cannot refuse, so recording its orders all game buys nothing.");

			using var world = HeadlessGame.CreateWorld(map, HeadlessGame.CreateSession(map, 4242));
			Assert.That(world.World.UseSnapshotSaves, Is.EqualTo(UsesSnapshotSaves(map)));
		}

		[TestCase(TestName = "Server and client agree on the save format")]
		public void ServerAgreesWithClientOnSaveFormat()
		{
			foreach (var path in new[] { TestMap, "ra|maps/allies-01" })
			{
				var map = HeadlessGame.LoadMap(path);
				var preview = HeadlessGame.ModData.MapCache[map.Uid];

				Assert.That(SnapshotPolicy.UsesSnapshotSaves(preview.WorldActorInfo), Is.EqualTo(UsesSnapshotSaves(map)),
					$"The server and the client disagree about the save format of {path}.");

				Assert.That(SnapshotPolicy.NeedsOrderStream(preview.WorldActorInfo), Is.EqualTo(NeedsOrderStream(map)),
					$"The server and the client disagree about whether {path} needs an order stream.");
			}
		}

		static bool UsesSnapshotSaves(Map map)
		{
			return SnapshotPolicy.UsesSnapshotSaves(map.Rules.Actors[SystemActors.World]);
		}

		static bool NeedsOrderStream(Map map)
		{
			return SnapshotPolicy.NeedsOrderStream(map.Rules.Actors[SystemActors.World]);
		}

		[TestCase(TestName = "Save scheduled mid-tick runs after the frame-end queue")]
		public void SaveWaitsForTheFrameEndQueue()
		{
			var map = HeadlessGame.LoadMap(TestMap);
			using var source = HeadlessGame.CreateWorld(map, HeadlessGame.CreateSession(map, 4242));
			source.Tick(TicksBeforeSave);

			var world = source.World;
			var victim = world.Actors.First(a => a.IsInWorld && a.OccupiesSpace != null && !a.Owner.NonCombatant);

			world.AddFrameEndTask(w => w.AddFrameEndTask(_ => victim.Dispose()));

			using var ms = new MemoryStream();
			source.SaveAtFrameEnd(ms);
			source.Tick();

			Assert.That(victim.IsDead, Is.True, "The frame-end task under test did not run.");

			ms.Position = 0;
			using var reader = new SnapshotReader(ms);
			var actors = reader.ReadYamlSection(WorldRestorer.ActorsSection);

			Assert.That(actors.Any(n => n.Key == victim.ActorID.ToStringInvariant()), Is.False,
				"The snapshot holds an actor that the frame-end queue had already disposed, so it " +
				"was taken before the queue finished draining.");
		}

		[TestCase(TestName = "Snapshot header describes the world it came from")]
		public void HeaderDescribesTheWorld()
		{
			var map = HeadlessGame.LoadMap(TestMap);
			using var source = HeadlessGame.CreateWorld(map, HeadlessGame.CreateSession(map, 4242));
			source.Tick(TicksBeforeSave);

			using var ms = new MemoryStream();
			source.World.SaveSnapshot(ms, SnapshotFlags.Autosave);
			ms.Position = 0;

			using var reader = new SnapshotReader(ms);
			var header = reader.Header;

			Assert.That(header.FormatVersion, Is.EqualTo(SnapshotHeader.CurrentFormatVersion));
			Assert.That(header.MapUid, Is.EqualTo(map.Uid));
			Assert.That(header.WorldTick, Is.EqualTo(source.World.WorldTick));
			Assert.That(header.SyncHash, Is.EqualTo(source.World.SyncHash()));
			Assert.That(header.ModId, Is.EqualTo(HeadlessGame.ModData.Manifest.Id));
			Assert.That(header.ModVersion, Is.EqualTo(HeadlessGame.ModData.Manifest.Metadata.Version));
			Assert.That(header.Flags, Is.EqualTo(SnapshotFlags.Autosave));

			Assert.That(header.EngineVersion, Is.EqualTo(Game.EngineVersion));
		}

		[TestCase(TestName = "Captured diff survives being written to a save and read back")]
		public void DiffSurvivesTheSaveFile()
		{
			var map = HeadlessGame.LoadMap(TestMap);
			using var source = HeadlessGame.CreateWorld(map, HeadlessGame.CreateSession(map, 4242));
			source.Tick(TicksBeforeSave);

			var captured = source.CaptureDiff();
			var reloaded = SnapshotDiff.Load(captured.Save());

			Assert.That(reloaded, Is.Not.Null);

			Assert.That(SnapshotDiff.Compare(captured, reloaded), Is.Empty);
			Assert.That(SnapshotDiff.Compare(reloaded, captured), Is.Empty);
		}

		[TestCase(TestName = "Unreadable diff section reports nothing rather than throwing")]
		public void DiffLoadToleratesGarbage()
		{
			Assert.That(SnapshotDiff.Load(null), Is.Null);
			Assert.That(SnapshotDiff.Load([new MiniYamlNode("0", "not, a, diff")]), Is.Null);
		}

		static void AssertRoundTrip(Action<HeadlessWorld> scenario)
		{
			const int Seed = 4242;

			var expected = new List<int>();
			var diffs = new List<SnapshotDiff>();
			SnapshotDiff savedDiff;
			byte[] snapshot;

			var map = HeadlessGame.LoadMap(TestMap);
			using (var source = HeadlessGame.CreateWorld(map, HeadlessGame.CreateSession(map, Seed)))
			{
				source.Tick(TicksBeforeSave);
				scenario(source);

				using (var ms = new MemoryStream())
				{
					source.Save(ms);
					snapshot = ms.ToArray();
				}

				savedDiff = source.CaptureDiff();
				expected.Add(source.World.SyncHash());
				diffs.Add(savedDiff);

				for (var i = 0; i < TicksAfterRestore; i++)
				{
					source.Tick();
					expected.Add(source.World.SyncHash());

					diffs.Add(source.CaptureDiff());
				}
			}

			var restoreMap = HeadlessGame.LoadMap(TestMap);
			using (var restored = HeadlessGame.CreateWorld(restoreMap, HeadlessGame.CreateSession(restoreMap, Seed), restoring: true))
			{
				using (var ms = new MemoryStream(snapshot))
					restored.Restore(ms, savedDiff: savedDiff);

				Assert.That(restored.World.SyncHash(), Is.EqualTo(expected[0]),
					"Restored world does not match the snapshot it was built from.");

				for (var i = 1; i <= TicksAfterRestore; i++)
				{
					restored.Tick();
					if (restored.World.SyncHash() == expected[i])
						continue;

					var detail = SnapshotDiff.Compare(diffs[i], restored.CaptureDiff());
					Assert.Fail($"Restored world diverged {i} tick(s) after the restore." +
						Environment.NewLine + detail);
				}
			}
		}
	}
}
