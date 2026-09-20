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
using OpenRA.GameSaves;
using OpenRA.Graphics;
using OpenRA.Traits;

namespace OpenRA.Mods.Common.Traits
{
	[TraitLocation(SystemActors.Player)]
	public class GameSaveViewportManagerInfo : TraitInfo
	{
		public override object Create(ActorInitializer init) { return new GameSaveViewportManager(this); }
	}

	public class GameSaveViewportManager : IWorldLoaded, IGameSaveTraitData, ISaveState
	{
		const string ViewportKey = "Viewport";
		const string RenderPlayerKey = "RenderPlayer";

		readonly GameSaveViewportManagerInfo info;

		WorldRenderer worldRenderer;

		public GameSaveViewportManager(GameSaveViewportManagerInfo info)
		{
			this.info = info;
		}

		void IWorldLoaded.WorldLoaded(World w, WorldRenderer wr) { worldRenderer = wr; }

		List<MiniYamlNode> IGameSaveTraitData.IssueTraitData(Actor self)
		{
			return SaveViewport(self);
		}

		void IGameSaveTraitData.ResolveTraitData(Actor self, MiniYaml data)
		{
			Restore(data);
		}

		TraitInfo ISaveState.SaveStateInfo => info;

		List<MiniYamlNode> ISaveState.SaveState(Actor self, SnapshotWriter w)
		{
			return SaveViewport(self);
		}

		void ISaveState.LoadState(Actor self, MiniYaml data, SnapshotReader r)
		{
			Restore(data);
		}

		List<MiniYamlNode> SaveViewport(Actor self)
		{
			if (worldRenderer == null)
				return null;

			// HACK: Store the viewport state for the skirmish observer on the first bot's trait
			// TODO: This won't make sense for MP saves
			var localPlayer = worldRenderer.World.LocalPlayer;
			if ((localPlayer != null && localPlayer.PlayerActor != self) ||
				(localPlayer == null && self.Owner != self.World.Players.FirstOrDefault(p => p.IsBot)))
				return null;

			var nodes = new List<MiniYamlNode>()
			{
				new(ViewportKey, FieldSaver.FormatValue(worldRenderer.Viewport.CenterPosition))
			};

			var renderPlayer = worldRenderer.World.RenderPlayer;
			if (localPlayer == null && renderPlayer != null)
				nodes.Add(new MiniYamlNode(RenderPlayerKey, FieldSaver.FormatValue(renderPlayer.PlayerActor.ActorID)));

			return nodes;
		}

		void Restore(MiniYaml data)
		{
			if (worldRenderer == null)
				return;

			var viewportNode = data.NodeWithKeyOrDefault(ViewportKey);
			if (viewportNode != null)
				worldRenderer.Viewport.Center(FieldLoader.GetValue<WPos>(ViewportKey, viewportNode.Value.Value));

			var renderPlayerNode = data.NodeWithKeyOrDefault(RenderPlayerKey);
			if (renderPlayerNode != null)
			{
				var renderPlayerActorID = FieldLoader.GetValue<uint>(RenderPlayerKey, renderPlayerNode.Value.Value);
				worldRenderer.World.RenderPlayer = worldRenderer.World.GetActorById(renderPlayerActorID).Owner;
			}
		}
	}
}
