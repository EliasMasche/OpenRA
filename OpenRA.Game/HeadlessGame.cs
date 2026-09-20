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
using OpenRA.GameSaves;
using OpenRA.Network;
using OpenRA.Primitives;

namespace OpenRA
{
	public static class HeadlessGame
	{
		public static ModData ModData { get; private set; }

		public static ActivityRegistry ActivityRegistry => ModData.ActivityRegistry;

		public static EffectRegistry EffectRegistry => ModData.EffectRegistry;

		public static bool IsInitialized => ModData != null;

		static bool ownsModData;

		public static void Initialize(string modId, string engineDir = "..", string supportDir = null)
		{
			ArgumentException.ThrowIfNullOrEmpty(modId);

			if (IsInitialized)
				throw new InvalidOperationException(
					$"A headless mod is already loaded ('{ModData.Manifest.Id}'). Call Shutdown before loading another.");

			Platform.OverrideEngineDir(engineDir);

			if (supportDir != null)
				Platform.OverrideSupportDir(supportDir);

			foreach (var channel in new[] { "debug", "perf", "sync", "sound", "server" })
				Log.AddChannel(channel, null);

			Game.InitializeSettings(Arguments.Empty);
			Game.LoadEngineVersion();

			var mods = new InstalledMods([Path.Combine(Platform.EngineDir, "mods")], []);
			if (!mods.ContainsKey(modId))
				throw new InvalidOperationException(
					$"Mod '{modId}' was not found under '{Path.Combine(Platform.EngineDir, "mods")}'. " +
					$"Available mods: {string.Join(", ", mods.Keys.OrderBy(k => k, StringComparer.Ordinal))}.");

			VerifyModAssemblies(mods[modId]);

			ModData = Game.ModData = new ModData(mods[modId], mods);
			ownsModData = true;

			Game.Sound = new Sound(new HeadlessPlatform(), Game.Settings.Sound);
			Game.Sound.Initialize(ModData.SoundLoaders, ModData.DefaultFileSystem);

			Game.Sound.DisableAllSounds = true;

			ModData.MapCache.LoadMaps(ModData);

			Widgets.ChromeMetrics.Initialize(ModData);
		}

		public static void UseExistingMod(ModData modData)
		{
			ArgumentNullException.ThrowIfNull(modData);

			if (IsInitialized)
				throw new InvalidOperationException($"A headless mod is already loaded ('{ModData.Manifest.Id}').");

			ModData = Game.ModData = modData;

			Game.Settings ??= new Settings(Path.Combine(Platform.SupportDir, "settings.yaml"), Arguments.Empty);

			if (Game.EngineVersion == null)
				Game.LoadEngineVersion();

			if (Game.Sound == null)
			{
				Game.Sound = new Sound(new HeadlessPlatform(), Game.Settings.Sound);
				Game.Sound.Initialize(ModData.SoundLoaders, ModData.DefaultFileSystem);
				Game.Sound.DisableAllSounds = true;
			}

			ModData.MapCache.LoadMaps(ModData);
			Widgets.ChromeMetrics.Initialize(ModData);
		}

		static void VerifyModAssemblies(Manifest manifest)
		{
			var missing = manifest.Assemblies
				.Where(a => !File.Exists(Path.Combine(Platform.BinDir, a)))
				.ToList();

			if (missing.Count > 0)
				throw new FileNotFoundException(
					$"Mod '{manifest.Id}' needs {string.Join(", ", missing)} in '{Platform.BinDir}', " +
					"but they are not there. Build the whole solution, not just one project.");
		}

		public static void Shutdown()
		{
			if (!IsInitialized)
				return;

			Game.Sound?.Dispose();
			Game.Sound = null;

			if (ownsModData)
				ModData.Dispose();

			ownsModData = false;
			ModData = Game.ModData = null;
		}

		public static Map LoadMap(string path)
		{
			EnsureInitialized();

			var package = ModData.ModFiles.OpenPackage(path)
				?? throw new FileNotFoundException($"Map package '{path}' was not found in mod '{ModData.Manifest.Id}'.");

			return new Map(ModData, package);
		}

		public static Session CreateSession(Map map, int seed = 0, string faction = "Random", params string[] botTypes)
		{
			ArgumentNullException.ThrowIfNull(map);
			EnsureInitialized();

			var session = new Session();
			session.GlobalSettings.Map = map.Uid;
			session.GlobalSettings.RandomSeed = seed;
			session.GlobalSettings.GameUid = Guid.NewGuid().ToString();

			session.GlobalSettings.EnableSyncReports = false;

			var gameSpeeds = ModData.GetOrCreate<GameSpeeds>();
			session.GlobalSettings.GameTimestep = gameSpeeds.Speeds[gameSpeeds.DefaultSpeed].Timestep;

			var mapPlayers = new MapPlayers(map.PlayerDefinitions).Players;
			session.Slots = mapPlayers.Values
				.Select(Session.Slot.FromPlayerReference)
				.Where(s => s != null)
				.ToDictionary(s => s.PlayerReference);

			if (session.Slots.Count == 0)
				throw new InvalidDataException($"Map '{map.Title}' has no playable players, so no world can be started on it.");

			var slots = session.Slots.Keys.ToList();

			var localClientId = ((IConnection)new EchoConnection()).LocalClientId;

			session.Clients.Add(new Session.Client
			{
				Index = localClientId,
				Slot = slots[0],
				Name = "Headless",
				Faction = faction,
				PreferredColor = Color.Red,
				Color = Color.Red,
				State = Session.ClientState.Ready,
				IsAdmin = true
			});

			for (var i = 0; i < botTypes.Length; i++)
			{
				if (i + 1 >= slots.Count)
					throw new InvalidDataException(
						$"Map '{map.Title}' has {slots.Count} slots, which cannot hold one human and {botTypes.Length} bots.");

				session.Clients.Add(new Session.Client
				{
					Index = localClientId + i + 1,
					Slot = slots[i + 1],
					Name = botTypes[i],
					Bot = botTypes[i],

					BotControllerClientIndex = localClientId,
					Faction = faction,
					PreferredColor = Color.Blue,
					Color = Color.Blue,
					State = Session.ClientState.Ready
				});
			}

			for (var i = 1 + botTypes.Length; i < slots.Count; i++)
			{
				var slot = slots[i];
				if (!mapPlayers.TryGetValue(slot, out var reference) || string.IsNullOrEmpty(reference.Bot) || !session.Slots[slot].AllowBots)
					continue;

				session.Clients.Add(new Session.Client
				{
					Index = localClientId + i,
					Slot = slot,
					Name = reference.Bot,
					Bot = reference.Bot,
					BotControllerClientIndex = localClientId,
					Faction = faction,
					PreferredColor = reference.Color,
					Color = reference.Color,
					State = Session.ClientState.Ready
				});
			}

			return session;
		}

		public static HeadlessWorld CreateWorld(Map map, Session session, bool restoring = false)
		{
			return CreateWorld(map, session, new EchoConnection(), restoring);
		}

		public static HeadlessWorld CreateWorld(Map map, Session session, IConnection connection, bool restoring = false)
		{
			ArgumentNullException.ThrowIfNull(map);
			ArgumentNullException.ThrowIfNull(session);
			ArgumentNullException.ThrowIfNull(connection);
			EnsureInitialized();

			var orderManager = new OrderManager(connection) { LobbyInfo = session };

			TextNotificationsManager.Clear();
			UnitOrders.Clear();
			Game.OrderManager = orderManager;

			World world = null;
			try
			{
				world = new World(map, ModData, orderManager, WorldType.Regular, hasRenderer: false)
				{
					IsRestoringSnapshot = restoring
				};

				orderManager.World = world;

				world.LoadComplete(null);
				orderManager.StartGame();
				world.PostLoadComplete(null);

				return new HeadlessWorld(world, orderManager);
			}
			catch
			{
				world?.Dispose();
				orderManager.Dispose();
				Game.OrderManager = null;
				throw;
			}
		}

		public static HeadlessWorld CreateWorld(string mapPath, int seed = 0)
		{
			return CreateWorld(LoadMap(mapPath), CreateSession(LoadMap(mapPath), seed));
		}

		static void EnsureInitialized()
		{
			if (!IsInitialized)
				throw new InvalidOperationException($"{nameof(HeadlessGame)}.{nameof(Initialize)} has not been called.");
		}
	}

	public sealed class HeadlessWorld : IDisposable
	{
		public World World { get; }
		public OrderManager OrderManager { get; }

		bool disposed;

		internal HeadlessWorld(World world, OrderManager orderManager)
		{
			World = world;
			OrderManager = orderManager;
		}

		public void Tick(int frames = 1)
		{
			ObjectDisposedException.ThrowIf(disposed, this);
			ArgumentOutOfRangeException.ThrowIfNegative(frames);

			for (var i = 0; i < frames; i++)
			{
				OrderManager.TickImmediate();

				if (OrderManager.TryTick())
				{
					World.OrderGenerator.Tick(World);
					World.Tick();
				}
			}
		}

		public void TickWorld(int ticks)
		{
			ObjectDisposedException.ThrowIf(disposed, this);
			ArgumentOutOfRangeException.ThrowIfNegative(ticks);

			var target = World.WorldTick + ticks;

			var guard = 0;
			var limit = 100 * (ticks + 1);
			while (World.WorldTick < target)
			{
				Tick();

				if (++guard > limit)
					throw new InvalidOperationException(
						$"World did not advance to tick {target} within {limit} frames (stopped at {World.WorldTick}).");
			}
		}

		public void Save(Stream stream)
		{
			ObjectDisposedException.ThrowIf(disposed, this);
			World.SaveSnapshot(stream);
		}

		public void SaveAtFrameEnd(Stream stream)
		{
			ObjectDisposedException.ThrowIf(disposed, this);
			ArgumentNullException.ThrowIfNull(stream);
			World.RequestSnapshotAtFrameEnd(w => w.SaveSnapshot(stream));
		}

		public void ScheduleSnapshot(int frame, Stream stream)
		{
			ObjectDisposedException.ThrowIf(disposed, this);
			ArgumentNullException.ThrowIfNull(stream);
			World.ScheduleSnapshot(frame, w => w.SaveSnapshot(stream));
		}

		public SnapshotDiff CaptureDiff()
		{
			ObjectDisposedException.ThrowIf(disposed, this);
			return SnapshotDiff.Capture(World);
		}

		public void Restore(Stream stream, bool lenient = false, SnapshotDiff savedDiff = null)
		{
			ObjectDisposedException.ThrowIf(disposed, this);
			ArgumentNullException.ThrowIfNull(stream);

			using (var reader = new SnapshotReader(stream, false, World))
			{
				var restorer = new WorldRestorer(World, reader, HeadlessGame.ActivityRegistry, HeadlessGame.EffectRegistry)
				{
					SavedDiff = savedDiff,
					Diagnostics = Game.Settings.Debug.SnapshotDiagnostics
				};

				restorer.Restore(lenient);
			}
		}

		public void Dispose()
		{
			if (disposed)
				return;

			disposed = true;

			World.Dispose();
			OrderManager.Dispose();

			if (Game.OrderManager == OrderManager)
				Game.OrderManager = null;
		}
	}
}
