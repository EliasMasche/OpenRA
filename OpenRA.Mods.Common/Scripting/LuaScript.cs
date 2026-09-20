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
using System.Collections.Frozen;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using OpenRA.GameSaves;
using OpenRA.Graphics;
using OpenRA.Mods.Common.Scripting.Snapshot;
using OpenRA.Mods.Common.Traits;
using OpenRA.Scripting;
using OpenRA.Scripting.Snapshot;
using OpenRA.Traits;

namespace OpenRA.Mods.Common.Scripting
{
	[TraitLocation(SystemActors.World)]
	[Desc("Part of the new Lua API.")]
	public class LuaScriptInfo : TraitInfo, Requires<SpawnMapActorsInfo>, NotBefore<SpawnStartingUnitsInfo>
	{
		[Desc("File names with location relative to the map.")]
		public readonly FrozenSet<string> Scripts = [];

		public override object Create(ActorInitializer init) { return new LuaScript(this); }
	}

	public class LuaScript : ITick, IWorldLoaded, INotifyActorDisposing, IWorldSaveState, INotifyStateRestored, ISaveState, IDeferScriptUntilRestored
	{
		const string ScriptSectionName = "Script";
		const string MapTriggersKey = "MapTriggers";
		const string GroupTriggersKey = "GroupTriggers";

		readonly LuaScriptInfo info;
		readonly List<ILuaHandleHolder> handleHolders = [];
		readonly List<LuaMapTrigger> mapTriggers = [];
		readonly List<LuaGroupTrigger> groupTriggers = [];
		public ScriptContext Context;
		bool disposed;

		bool chunkPending;

		World world;

		public LuaScript(LuaScriptInfo info)
		{
			this.info = info;
		}

		void IWorldLoaded.WorldLoaded(World world, WorldRenderer worldRenderer)
		{
			this.world = world;

			chunkPending = world.IsRestoringSnapshot;

			Context = new ScriptContext(world, worldRenderer,
				chunkPending ? [] : info.Scripts ?? Enumerable.Empty<string>());

			if (!chunkPending)
				Context.WorldLoaded();
		}

		void IDeferScriptUntilRestored.RunDeferredScript()
		{
			if (!chunkPending)
				return;

			chunkPending = false;

			var mapActors = world.WorldActor.TraitOrDefault<SpawnMapActors>();

			if (mapActors != null && !mapActors.MappingWasRead)
				throw new InvalidDataException(
					$"This save was written before snapshots recorded which actor each map name became, so " +
					$"'{WorldRestorer.MapActorsSection}' is missing and the map's named actors cannot be resolved. " +
					$"Saves taken by an older build have to be retaken.");

			if (mapActors != null)
				Context.ReinstallMapActorGlobals(mapActors.Actors);

			Context.RunScripts(info.Scripts ?? Enumerable.Empty<string>());
		}

		void ITick.Tick(Actor self)
		{
			Context.Tick();
		}

		void INotifyActorDisposing.Disposing(Actor self)
		{
			if (disposed)
				return;

			Context?.Dispose();

			disposed = true;
		}

		public bool FatalErrorOccurred => Context.FatalErrorOccurred;

		string IWorldSaveState.SectionName => ScriptSectionName;

		void IWorldSaveState.SaveState(Actor self, Stream s, SnapshotWriter w)
		{
			Context.SaveLuaState(s, ScriptUserdataCodec.ForSave(Context, w));
		}

		void IWorldSaveState.LoadState(Actor self, Stream s, SnapshotReader r)
		{
			Context.LoadLuaState(s, ScriptUserdataCodec.ForLoad(Context, r));
		}

		public void RegisterMapTrigger(LuaMapTrigger trigger)
		{
			ArgumentNullException.ThrowIfNull(trigger);

			mapTriggers.Add(trigger);
		}

		public void ForgetMapTrigger(int id)
		{
			mapTriggers.RemoveAll(t => t.Id == id);
		}

		public void RegisterGroupTrigger(LuaGroupTrigger trigger)
		{
			ArgumentNullException.ThrowIfNull(trigger);

			groupTriggers.Add(trigger);
		}

		TraitInfo ISaveState.SaveStateInfo => info;

		List<MiniYamlNode> ISaveState.SaveState(Actor self, SnapshotWriter w)
		{
			var live = groupTriggers.Where(t => !t.Spent).ToList();

			if (mapTriggers.Count == 0 && live.Count == 0)
				return null;

			var nodes = new List<MiniYamlNode>();

			if (mapTriggers.Count > 0)
				nodes.Add(new MiniYamlNode(MapTriggersKey, new MiniYaml("", mapTriggers
					.Select((t, i) => new MiniYamlNode(i.ToStringInvariant(), new MiniYaml("", t.SaveState(self.World, Context, w))))
					.ToList())));

			if (live.Count > 0)
				nodes.Add(new MiniYamlNode(GroupTriggersKey, new MiniYaml("", live
					.Select((t, i) => new MiniYamlNode(i.ToStringInvariant(), new MiniYaml("", t.SaveState(Context, w))))
					.ToList())));

			return nodes;
		}

		void ISaveState.LoadState(Actor self, MiniYaml data, SnapshotReader r)
		{
			mapTriggers.Clear();
			groupTriggers.Clear();

			var nodes = data.ToDictionary();

			if (nodes.TryGetValue(MapTriggersKey, out var saved))
				foreach (var node in saved.Nodes)
					mapTriggers.Add(LuaMapTrigger.LoadState(node.Value, r));

			if (nodes.TryGetValue(GroupTriggersKey, out var groups))
				foreach (var node in groups.Nodes)
					groupTriggers.Add(LuaGroupTrigger.LoadState(node.Value, r));
		}

		public void RegisterHandleHolder(ILuaHandleHolder holder)
		{
			ArgumentNullException.ThrowIfNull(holder);

			handleHolders.Add(holder);
		}

		void INotifyStateRestored.StateRestored(Actor self)
		{
			foreach (var holder in handleHolders)
				holder.ResolveHandles(Context);

			handleHolders.Clear();

			foreach (var trigger in mapTriggers)
				trigger.Restore(self.World, Context);

			foreach (var trigger in groupTriggers)
				trigger.Restore(Context);
		}
	}
}
