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
using OpenRA.GameSaves;
using OpenRA.Graphics;
using OpenRA.Traits;

namespace OpenRA.Mods.Common.Traits
{
	[TraitLocation(SystemActors.World)]
	[Desc("Spawns the initial units for each player upon game start.")]
	public class SpawnMapActorsInfo : TraitInfo<SpawnMapActors> { }

	public class SpawnMapActors : IWorldLoaded, IWorldSaveState, ILoadBeforeDeferredScript, IMapActorRoster
	{
		public bool MappingWasRead { get; private set; }

		readonly Dictionary<string, uint> idsByName = [];

		public Dictionary<string, Actor> Actors = [];

		MapActorRoster roster = MapActorRoster.None;

		string IWorldSaveState.SectionName => WorldRestorer.MapActorsSection;

		MapActorRoster IMapActorRoster.SaveRoster => roster;

		public bool IsMapActor(uint actorID)
		{
			return roster.Contains(actorID);
		}

		IEnumerable<KeyValuePair<string, Actor>> LiveActors()
		{
			return Actors.Where(kv => kv.Value.IsInWorld);
		}

		void IMapActorRoster.LoadRoster(MapActorRoster loaded, SnapshotReader r)
		{
			roster = loaded;

			idsByName.Clear();
			Actors.Clear();
			MappingWasRead = true;

			foreach (var kv in loaded.Names)
				idsByName[kv.Key] = kv.Value;
		}

		void IWorldSaveState.SaveState(Actor self, Stream s, SnapshotWriter w)
		{
			var live = LiveActors().ToArray();

			WorldRestorer.WriteMapActorRoster(s, roster with
			{
				Names = live.Select(kv => new KeyValuePair<string, uint>(kv.Key, kv.Value.ActorID)).ToArray()
			});
		}

		void IWorldSaveState.LoadState(Actor self, Stream s, SnapshotReader r)
		{
			s.CopyTo(Stream.Null);

			foreach (var kv in idsByName)
			{
				var actor = r.GetActorById(kv.Value);
				if (actor != null)
					Actors[kv.Key] = actor;
			}

			if (Actors.Count != idsByName.Count)
			{
				var missing = idsByName.Where(kv => !Actors.ContainsKey(kv.Key))
					.Select(kv => $"{kv.Key}={kv.Value}");

				throw new InvalidDataException($"{WorldRestorer.MapActorsSection} names {idsByName.Count} map actors, " +
					$"but {idsByName.Count - Actors.Count} of them are not in the restored world: " +
					string.Join(", ", missing));
			}

			if (roster.IsEmpty || roster.LastID < roster.FirstID ||
				idsByName.Values.Any(id => !roster.Contains(id)))
			{
				throw new InvalidDataException($"{WorldRestorer.MapActorsSection} records the id span " +
					$"{roster.FirstID}..{roster.LastID}, which does not hold its {idsByName.Count} named actors.");
			}
		}

		public void WorldLoaded(World world, WorldRenderer wr)
		{
			if (world.IsRestoringSnapshot)
				return;

			var preventMapSpawns = world.WorldActor.TraitsImplementing<IPreventMapSpawn>()
				.Concat(world.WorldActor.Owner.PlayerActor.TraitsImplementing<IPreventMapSpawn>())
				.ToArray();

			foreach (var kv in world.Map.ActorDefinitions)
			{
				var actorReference = new ActorReference(kv.Value.Value, kv.Value);

				// If an actor's doesn't have a valid owner transfer ownership to neutral
				var ownerInit = actorReference.Get<OwnerInit>();
				if (!world.Players.Any(p => p.InternalName == ownerInit.InternalName))
					actorReference.Replace(new OwnerInit(world.WorldActor.Owner));

				actorReference.Add(new SkipMakeAnimsInit());
				actorReference.Add(new SpawnedByMapInit());

				if (PreventMapSpawn(world, actorReference, preventMapSpawns))
					continue;

				var actor = world.CreateActor(true, actorReference);
				Actors[kv.Key] = actor;

				if (roster.IsEmpty || actor.ActorID < roster.FirstID)
					roster = roster with { FirstID = actor.ActorID };

				if (actor.ActorID > roster.LastID)
					roster = roster with { LastID = actor.ActorID };
			}
		}

		static bool PreventMapSpawn(World world, ActorReference actorReference, IEnumerable<IPreventMapSpawn> preventMapSpawns)
		{
			foreach (var pms in preventMapSpawns)
				if (pms.PreventMapSpawn(world, actorReference))
					return true;

			return false;
		}
	}

	public class SkipMakeAnimsInit : RuntimeFlagInit { }
}
