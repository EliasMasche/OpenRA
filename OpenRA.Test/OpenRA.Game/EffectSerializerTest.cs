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
using OpenRA.GameSaves;
using OpenRA.Graphics;

namespace OpenRA.Test
{
	#region Stub effects


	[SaveableEffect]
	sealed class CountingEffect : ISaveableEffect
	{
		public int Value;

		public CountingEffect(int value) { Value = value; }

		internal CountingEffect(World _1, SnapshotReader _2, MiniYaml yaml)
		{
			Value = FieldLoader.GetValue<int>("Value", yaml.NodeWithKeyOrDefault("Value").Value.Value);
		}

		public void Tick(World world) { }
		public IEnumerable<IRenderable> Render(WorldRenderer wr) => [];

		public List<MiniYamlNode> SaveState(World world, SnapshotWriter w)
		{
			return [new("Value", FieldSaver.FormatValue(Value))];
		}
	}

	[SaveableEffect]
	sealed class SyncedCountingEffect : ISaveableEffect, ISync
	{
		[VerifySync]
		public int Value;

		public SyncedCountingEffect(int value) { Value = value; }

		internal SyncedCountingEffect(World _1, SnapshotReader _2, MiniYaml yaml)
		{
			Value = FieldLoader.GetValue<int>("Value", yaml.NodeWithKeyOrDefault("Value").Value.Value);
		}

		public void Tick(World world) { }
		public IEnumerable<IRenderable> Render(WorldRenderer wr) => [];

		public List<MiniYamlNode> SaveState(World world, SnapshotWriter w)
		{
			return [new("Value", FieldSaver.FormatValue(Value))];
		}
	}

	sealed class UnsaveableEffect : IEffect
	{
		public void Tick(World world) { }
		public IEnumerable<IRenderable> Render(WorldRenderer wr) => [];
	}

	[SaveableEffect]
	sealed class DecliningEffect : ISaveableEffect
	{
		public DecliningEffect() { }

		internal DecliningEffect(World _1, SnapshotReader _2, MiniYaml _3) { }

		public void Tick(World world) { }
		public IEnumerable<IRenderable> Render(WorldRenderer wr) => [];
		public List<MiniYamlNode> SaveState(World world, SnapshotWriter w) => null;
	}

	#endregion

	[TestFixture]
	[NonParallelizable]
	sealed class EffectSerializerTest
	{
		const string TestMap = "mods/ra/maps/agenda.oramap";

		static EffectSerializer Serializer()
		{
			return new EffectSerializer(new EffectRegistry(
				[typeof(CountingEffect), typeof(SyncedCountingEffect), typeof(DecliningEffect)]));
		}

		static SnapshotHeader Header()
		{
			return new SnapshotHeader("test", "test", "1", "uid", 0, 0, 0, 0, 0, DateTime.UtcNow, SnapshotFlags.None);
		}

		static SnapshotWriter Writer()
		{
			return new SnapshotWriter(new MemoryStream(), Header());
		}

		static SnapshotReader Reader()
		{
			var stream = new MemoryStream();
			using (var w = new SnapshotWriter(stream, Header()))
				w.WriteYamlSection("Empty", []);

			stream.Position = 0;
			return new SnapshotReader(stream);
		}

		static void RoundTrip(Func<IEnumerable<IEffect>> build, Action<World, EffectSerializer> assert)
		{
			var s = Serializer();
			List<MiniYamlNode> saved;

			var map = HeadlessGame.LoadMap(TestMap);
			using (var source = HeadlessGame.CreateWorld(map, HeadlessGame.CreateSession(map)))
			{
				foreach (var e in build())
					source.World.Add(e);

				using (var w = Writer())
					saved = s.Save(source.World, w);
			}

			var yaml = MiniYaml.FromString(saved.WriteToString(), "test").ToList();

			var restoreMap = HeadlessGame.LoadMap(TestMap);
			using (var restored = HeadlessGame.CreateWorld(restoreMap, HeadlessGame.CreateSession(restoreMap)))
			{
				using (var r = Reader())
					s.Restore(restored.World, yaml, r);

				assert(restored.World, s);
			}
		}

		[TestCase(TestName = "Effects are saved in the order the world holds them")]
		public void SavesInWorldEffectOrder()
		{
			var map = HeadlessGame.LoadMap(TestMap);
			using (var headless = HeadlessGame.CreateWorld(map, HeadlessGame.CreateSession(map)))
			{
				for (var i = 0; i < 4; i++)
					headless.World.Add(new CountingEffect(i));

				List<MiniYamlNode> saved;
				using (var w = Writer())
					saved = Serializer().Save(headless.World, w);

				string[] expectedKeys = ["0", "1", "2", "3"];
				Assert.That(saved.Select(n => n.Key), Is.EqualTo(expectedKeys));

				int[] expectedValues = [0, 1, 2, 3];
				Assert.That(saved.Select(SavedValue), Is.EqualTo(expectedValues));
			}
		}

		[TestCase(TestName = "Restored effects keep the order they were saved in")]
		public void RestoresInSavedOrder()
		{
			RoundTrip(
				() => Enumerable.Range(0, 5).Select(i => (IEffect)new SyncedCountingEffect(i)),
				(world, _) =>
				{
					int[] expected = [0, 1, 2, 3, 4];
					Assert.That(Values(world), Is.EqualTo(expected));
				});
		}

		[TestCase(TestName = "Every saved effect comes back")]
		public void RestoresEffectCount()
		{
			RoundTrip(
				() => Enumerable.Range(0, 3).Select(i => (IEffect)new CountingEffect(i)),
				(world, _) => Assert.That(world.Effects.OfType<CountingEffect>().Count(), Is.EqualTo(3)));
		}

		[TestCase(TestName = "A dropped effect does not shift the ones after it")]
		public void DroppedEffectDoesNotShiftLaterOnes()
		{
			RoundTrip(
				() =>
				[
					new SyncedCountingEffect(1),
					new UnsaveableEffect(),
					new DecliningEffect(),
					new SyncedCountingEffect(2)
				],
				(world, s) =>
				{
					int[] expected = [1, 2];
					Assert.That(Values(world), Is.EqualTo(expected));
					Assert.That(s.DroppedEffects, Is.EqualTo(2));
				});
		}

		[TestCase(TestName = "Each synced effect hashes the same after a restore")]
		public void SyncedEffectHashesMatch()
		{
			var s = Serializer();
			List<MiniYamlNode> saved;
			SnapshotDiff expected;

			var map = HeadlessGame.LoadMap(TestMap);
			using (var source = HeadlessGame.CreateWorld(map, HeadlessGame.CreateSession(map)))
			{
				for (var i = 1; i <= 4; i++)
					source.World.Add(new SyncedCountingEffect(i * 7));

				expected = SnapshotDiff.Capture(source.World);

				using (var w = Writer())
					saved = s.Save(source.World, w);
			}

			var yaml = MiniYaml.FromString(saved.WriteToString(), "test").ToList();

			var restoreMap = HeadlessGame.LoadMap(TestMap);
			using (var restored = HeadlessGame.CreateWorld(restoreMap, HeadlessGame.CreateSession(restoreMap)))
			{
				using (var r = Reader())
					s.Restore(restored.World, yaml, r);

				var detail = SnapshotDiff.Compare(expected, SnapshotDiff.Capture(restored.World));
				Assert.That(EffectLines(detail), Is.Empty);
			}
		}

		static IEnumerable<string> EffectLines(string detail)
		{
			if (string.IsNullOrEmpty(detail))
				return [];

			return detail.Split('\n').Where(l => l.Contains(nameof(SyncedCountingEffect), StringComparison.Ordinal));
		}

		[TestCase(TestName = "Effects are restored in ascending key order whatever order they are read in")]
		public void RestoreIsIndependentOfNodeOrder()
		{
			var s = Serializer();
			var map = HeadlessGame.LoadMap(TestMap);
			List<MiniYamlNode> saved;

			using (var source = HeadlessGame.CreateWorld(map, HeadlessGame.CreateSession(map)))
			{
				for (var i = 0; i < 4; i++)
					source.World.Add(new SyncedCountingEffect(i));

				using (var w = Writer())
					saved = s.Save(source.World, w);
			}

			saved.Reverse();

			var restoreMap = HeadlessGame.LoadMap(TestMap);
			using (var restored = HeadlessGame.CreateWorld(restoreMap, HeadlessGame.CreateSession(restoreMap)))
			{
				using (var r = Reader())
					s.Restore(restored.World, saved, r);

				int[] expected = [0, 1, 2, 3];
				Assert.That(Values(restored.World), Is.EqualTo(expected));
			}
		}

		static IEnumerable<int> Values(World world)
		{
			return world.SyncedEffects.OfType<SyncedCountingEffect>().Select(e => e.Value);
		}

		static int SavedValue(MiniYamlNode node)
		{
			return FieldLoader.GetValue<int>("Value", node.Value.NodeWithKeyOrDefault("Value").Value.Value);
		}
	}
}
