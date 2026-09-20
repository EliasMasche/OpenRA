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
using OpenRA.Primitives;
using OpenRA.Support;

namespace OpenRA.GameSaves
{
	/// <summary>Rebuilds a world from a snapshot, in a fixed order.</summary>
	/// <remarks>
	/// <see cref="World.IsRestoringSnapshot"/> must be set before the world loads, not when
	/// <see cref="Restore"/> starts. The frame number and the actor id counter depend on it.
	/// <para>
	/// The order below is a contract. A later position depends on data that an earlier one loaded:
	/// <list type="number">
	/// <item><description>Verify the header. The map must match.</description></item>
	/// <item><description>Create the actors. Each keeps the id it was saved with.</description></item>
	/// <item><description>Load the bulk sections that the script reads.</description></item>
	/// <item><description>Run the deferred map script.</description></item>
	/// <item><description>Load each actor's trait state, then add it to the world.</description></item>
	/// <item><description>Load the world state and the player state.</description></item>
	/// <item><description>Restore the effects, in order.</description></item>
	/// <item><description>Resolve the deferred actor references and targets.</description></item>
	/// <item><description>Load the remaining bulk sections.</description></item>
	/// <item><description>Notify the traits that the state is restored.</description></item>
	/// <item><description>Restore the world tick and the random state.</description></item>
	/// <item><description>Verify the sync hash. A mismatch is a desync.</description></item>
	/// </list>
	/// </para>
	/// </remarks>
	public sealed class WorldRestorer
	{
		public const string WorldSection = "World";
		public const string PlayersSection = "Players";
		public const string ActorsSection = "Actors";
		public const string EffectsSection = "Effects";
		public const string RandomSection = "Random";

		public const string MapActorsSection = "MapActors";

		public const string LobbySection = "Lobby";

		public const string DiffSection = "Diff";

		internal const string WorldTickKey = "WorldTick";
		internal const string PausedKey = "Paused";
		internal const string GameOverKey = "IsGameOver";

		internal const string TypeKey = "Type";
		internal const string OwnerKey = "Owner";
		internal const string InWorldKey = "InWorld";
		internal const string GenerationKey = "Generation";
		internal const string TraitsKey = "Traits";

		internal const string NextActorIDKey = "NextActorID";
		internal const string WinStateKey = "WinState";

		const string FirstMapActorIdKey = "FirstMapActorId";
		const string LastMapActorIdKey = "LastMapActorId";

		readonly World world;
		readonly SnapshotReader reader;
		readonly ActivitySerializer activities;
		readonly EffectSerializer effects;

		public bool Diagnostics { get; set; }

		bool restoring;

		public SnapshotDiff SavedDiff { get; set; }

		public WorldRestorer(World world, SnapshotReader reader, ActivityRegistry registry, EffectRegistry effectRegistry)
		{
			ArgumentNullException.ThrowIfNull(world);
			ArgumentNullException.ThrowIfNull(reader);
			ArgumentNullException.ThrowIfNull(registry);
			ArgumentNullException.ThrowIfNull(effectRegistry);

			this.world = world;
			this.reader = reader;
			activities = new ActivitySerializer(registry);
			effects = new EffectSerializer(effectRegistry);
		}

		public void Restore(bool lenient = false, WorldLoadMode mode = WorldLoadMode.RestoredLocalSave)
		{
			if (restoring)
				throw new InvalidOperationException("A restore is already in progress.");

			if (!world.IsRestoringSnapshot)
				throw new InvalidOperationException(
					"World.IsRestoringSnapshot must be set before the world is loaded, not when the restore begins. " +
					"See the ordering contract on WorldRestorer.");

			VerifyHeader();

			restoring = true;
			try
			{
				var actorNodes = reader.ReadYamlSection(ActorsSection) ?? [];
				var restored = CreateActors(actorNodes);

				// SpawnMapActors must hold the map actor list first, because the
				// deferred script reads those actors at its top level.
				LoadBeforeDeferredScript();

				// The script installs the bindings that the saved script state refers to,
				// and that state loads later, in RestoreBulkState.
				RunDeferredScripts();

				LoadState(restored);

				LoadPersistentState();

				effects.Restore(world, reader.ReadYamlSection(EffectsSection) ?? [], reader);

				// The effects registered the completions that drop an effect whose references
				// did not resolve, so those completions must exist before this call.
				reader.RunDeferred();

				RestoreBulkState();

				NotifyRestored(restored);

				// The world tick and the random state move in this one call, so a failure
				// before it leaves both untouched.
				RestoreSimulationState();

				world.LoadMode = mode;

				if (Diagnostics)
					AuditRestore(restored, actorNodes.Count());
			}
			finally
			{
				restoring = false;

				world.IsRestoringSnapshot = false;
			}

			VerifySyncHash(lenient);
		}

		void AuditRestore(List<RestoredActor> restored, int namedInFile)
		{
			var created = new SnapshotAudit();
			foreach (var r in restored)
				created.Add(r.Actor);

			var inWorld = new SnapshotAudit();
			foreach (var r in restored)
				if (r.InWorld)
					inWorld.Add(r.Actor);

			var sb = new System.Text.StringBuilder();
			sb.Append("Restored world, tick ").Append(world.WorldTick)
				.Append(": ").Append(namedInFile).Append(" actor(s) named by the file, ")
				.Append(created.Total).Append(" recreated, ")
				.Append(inWorld.Total).Append(" added to the world, ")
				.Append(created.Total - inWorld.Total).Append(" left out of it").AppendLine();

			if (created.Total != namedInFile)
				sb.Append("  LOST AT RECREATE: the file named ").Append(namedInFile)
					.Append(" actor(s) and ").Append(created.Total).Append(" were built").AppendLine();

			if (inWorld.Total != created.Total)
			{
				sb.Append("  NOT ADDED TO THE WORLD, by type:").AppendLine();

				foreach (var line in created.DifferencesFrom(inWorld))
					sb.Append("    ").Append(line).AppendLine();
			}

			sb.Append("  by type:").AppendLine();
			foreach (var line in inWorld.Describe(string.Empty, "    ").Split(Environment.NewLine,
				StringSplitOptions.RemoveEmptyEntries).Skip(1))
				sb.Append(line).AppendLine();

			SnapshotAudit.Write(sb.ToString());
		}

		void RestoreBulkState()
		{
			foreach (var actor in PersistentActors())
			{
				foreach (var trait in actor.TraitsImplementing<IWorldSaveState>())
				{
					if (!reader.HasSection(trait.SectionName))
						continue;

					using (var s = reader.OpenSection(trait.SectionName))
						trait.LoadState(actor, s, reader);
				}
			}
		}

		IEnumerable<Actor> PersistentActors()
		{
			yield return world.WorldActor;

			foreach (var player in world.Players)
				yield return player.PlayerActor;
		}

		MapActorRoster ReadMapActorRoster()
		{
			var nodes = reader.ReadYamlSection(MapActorsSection);
			if (nodes == null)
				return MapActorRoster.None;

			var names = new List<KeyValuePair<string, uint>>();
			var first = 0u;
			var last = 0u;

			foreach (var n in nodes)
			{
				if (string.IsNullOrEmpty(n.Key))
					continue;

				if (n.Key == FirstMapActorIdKey)
				{
					first = FieldLoader.GetValue<uint>(FirstMapActorIdKey, n.Value.Value);
					continue;
				}

				if (n.Key == LastMapActorIdKey)
				{
					last = FieldLoader.GetValue<uint>(LastMapActorIdKey, n.Value.Value);
					continue;
				}

				names.Add(new KeyValuePair<string, uint>(n.Key, FieldLoader.GetValue<uint>(n.Key, n.Value.Value)));
			}

			return new MapActorRoster(names, first, last);
		}

		public static void WriteMapActorRoster(Stream stream, MapActorRoster roster)
		{
			ArgumentNullException.ThrowIfNull(stream);
			ArgumentNullException.ThrowIfNull(roster);

			var nodes = new List<MiniYamlNode>
			{
				new(FirstMapActorIdKey, FieldSaver.FormatValue(roster.FirstID)),
				new(LastMapActorIdKey, FieldSaver.FormatValue(roster.LastID))
			};

			nodes.AddRange(roster.Names.Select(kv => new MiniYamlNode(kv.Key, FieldSaver.FormatValue(kv.Value))));

			var bytes = System.Text.Encoding.UTF8.GetBytes(nodes.WriteToString());
			stream.Write(bytes, 0, bytes.Length);
		}

		void LoadMapActorRoster(MapActorRoster roster)
		{
			foreach (var actor in PersistentActors())
				foreach (var r in actor.TraitsImplementing<IMapActorRoster>())
					r.LoadRoster(roster, reader);
		}

		void VerifyHeader()
		{
			var mapUid = reader.Header.MapUid;
			if (mapUid != world.Map.Uid)
				throw new SnapshotVerificationException(
					$"Snapshot was saved on map '{mapUid}', but this world is '{world.Map.Uid}'.");
		}

		void VerifySyncHash(bool lenient)
		{
			var expected = reader.Header.SyncHash;
			var actual = world.SyncHash();
			if (expected == actual)
				return;

			var message = $"Restored world does not match the snapshot: sync hash is {actual}, expected {expected}.";

			var saved = SavedDiff ?? SnapshotDiff.Load(reader.ReadYamlSection(DiffSection));
			if (saved != null)
			{
				var detail = SnapshotDiff.Compare(saved, SnapshotDiff.Capture(world));
				if (!string.IsNullOrEmpty(detail))
					message += Environment.NewLine + detail;
			}

			if (!lenient)
				throw new SnapshotVerificationException(message);

			Log.Write("debug", message);
		}

		List<RestoredActor> CreateActors(IEnumerable<MiniYamlNode> actorNodes)
		{
			var restored = new List<RestoredActor>();

			var roster = ReadMapActorRoster();
			var mapActorIds = roster.ActorIds();
			LoadMapActorRoster(roster);

			foreach (var node in actorNodes.OrderBy(n => ParseActorID(n.Key)))
			{
				var actorID = ParseActorID(node.Key);
				var nodes = node.Value.ToDictionary();

				if (!nodes.TryGetValue(TypeKey, out var type) || string.IsNullOrEmpty(type.Value))
					throw new InvalidDataException($"Saved actor {actorID} has no type.");

				var inits = new TypeDictionary { new RestoringInit() };

				if (mapActorIds.Contains(actorID))
					inits.Add(new SpawnedByMapInit());

				if (nodes.TryGetValue(OwnerKey, out var owner))
				{
					var player = SnapshotRefs.ParsePlayer(world, owner.Value);
					if (player != null)
						inits.Add(new OwnerInit(player));
				}

				SnapshotInits.Load(world, inits, node.Value);

				var actor = world.RestoreActor(actorID, type.Value, inits);

				reader.RecordRestoredActor(actorID, actor);

				if (nodes.TryGetValue(GenerationKey, out var generation))
					actor.Generation = Exts.ParseInt32Invariant(generation.Value);

				var inWorld = !nodes.TryGetValue(InWorldKey, out var w) || FieldLoader.GetValue<bool>(InWorldKey, w.Value);
				restored.Add(new RestoredActor(actor, node.Value, inWorld));
			}

			return restored;
		}

		void LoadBeforeDeferredScript()
		{
			foreach (var actor in PersistentActors())
			{
				foreach (var trait in actor.TraitsImplementing<IWorldSaveState>())
				{
					if (trait is not ILoadBeforeDeferredScript || !reader.HasSection(trait.SectionName))
						continue;

					using (var s = reader.OpenSection(trait.SectionName))
						trait.LoadState(actor, s, reader);
				}
			}
		}

		void LoadPersistentState()
		{
			var worldNodes = reader.ReadYamlSection(WorldSection);
			if (worldNodes != null)
				LoadTraitState(world.WorldActor, new MiniYaml("", worldNodes).NodeWithKeyOrDefault(TraitsKey)?.Value.Nodes);

			LoadPlayerState();
		}

		void LoadState(List<RestoredActor> restored)
		{
			foreach (var r in restored)
			{
				LoadTraitState(r.Actor, r.Yaml.NodeWithKeyOrDefault(TraitsKey)?.Value.Nodes);

				var activityNodes = r.Yaml.NodeWithKeyOrDefault(ActivitySerializer.ActivitiesKey);
				if (activityNodes != null)
					activities.Restore(r.Actor, activityNodes.Value, reader, r.Actor.RestoreCurrentActivity);

				if (r.InWorld)
					world.Add(r.Actor);
			}
		}

		void LoadPlayerState()
		{
			var playerNodes = reader.ReadYamlSection(PlayersSection);
			if (playerNodes == null)
				return;

			foreach (var node in playerNodes)
			{
				var player = world.Players.FirstOrDefault(p => p.InternalName == node.Key);
				if (player == null)
					continue;

				var nodes = node.Value.ToDictionary();
				if (nodes.TryGetValue(WinStateKey, out var winState))
					player.WinState = FieldLoader.GetValue<WinState>(WinStateKey, winState.Value);

				LoadTraitState(player.PlayerActor, node.Value.NodeWithKeyOrDefault(TraitsKey)?.Value.Nodes);
			}
		}

		void RunDeferredScripts()
		{
			foreach (var actor in PersistentActors())
				foreach (var d in actor.TraitsImplementing<IDeferScriptUntilRestored>())
					d.RunDeferredScript();
		}

		void LoadTraitState(Actor actor, IEnumerable<MiniYamlNode> traitNodes)
		{
			if (traitNodes == null)
				return;

			var byKey = new Dictionary<SnapshotTraitKey, ISaveState>();
			foreach (var trait in actor.TraitsImplementing<ISaveState>())
				byKey[World.TraitKey(actor, trait.SaveStateInfo)] = trait;

			foreach (var node in traitNodes)
			{
				if (byKey.TryGetValue(SnapshotTraitKey.Parse(node.Key), out var trait))
					trait.LoadState(actor, node.Value, reader);
			}
		}

		void NotifyRestored(List<RestoredActor> restored)
		{
			foreach (var r in restored)
				foreach (var n in r.Actor.TraitsImplementing<INotifyStateRestored>())
					n.StateRestored(r.Actor);

			foreach (var actor in PersistentActors())
				foreach (var n in actor.TraitsImplementing<INotifyStateRestored>())
					n.StateRestored(actor);
		}

		void RestoreSimulationState()
		{
			var nodes = new MiniYaml("", reader.ReadYamlSection(WorldSection) ?? []).ToDictionary();

			var worldTick = nodes.TryGetValue(WorldTickKey, out var tick)
				? Exts.ParseInt32Invariant(tick.Value)
				: reader.Header.WorldTick;

			var paused = nodes.TryGetValue(PausedKey, out var p) && FieldLoader.GetValue<bool>(PausedKey, p.Value);
			var gameOver = nodes.TryGetValue(GameOverKey, out var o) && FieldLoader.GetValue<bool>(GameOverKey, o.Value);

			var nextActorID = nodes.TryGetValue(NextActorIDKey, out var n)
				? FieldLoader.GetValue<uint>(NextActorIDKey, n.Value)
				: 0;

			using (var s = reader.OpenSection(RandomSection))
			{
				if (s == null)
					throw new InvalidDataException("Snapshot has no random number generator state.");

				world.RestoreSimulationState(worldTick, MersenneTwisterState.Read(s), paused, gameOver, nextActorID);
			}
		}

		static uint ParseActorID(string key)
		{
			if (!uint.TryParse(key, System.Globalization.NumberStyles.None, System.Globalization.NumberFormatInfo.InvariantInfo, out var actorID))
				throw new InvalidDataException($"Malformed saved actor id '{key}'.");

			return actorID;
		}

		readonly record struct RestoredActor(Actor Actor, MiniYaml Yaml, bool InWorld);
	}
}
