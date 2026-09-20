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
using OpenRA.Effects;
using OpenRA.GameSaves;
using OpenRA.Graphics;
using OpenRA.Traits;

namespace OpenRA.Mods.Common.Effects
{
	[SaveableEffect]
	public class RevealShroudEffect : IEffect, ISaveableEffect, IRequiresRestoredReferences
	{
		const string PosKey = "Pos";
		const string PlayerKey = "Player";
		const string SourceTypeKey = "SourceType";
		const string RevealRadiusKey = "RevealRadius";
		const string ValidStancesKey = "ValidStances";
		const string DurationKey = "Duration";
		const string TicksKey = "Ticks";

		static readonly PPos[] NoCells = [];

		readonly WPos pos;
		readonly Player player;
		readonly Shroud.SourceType sourceType;
		readonly WDist revealRadius;
		readonly PlayerRelationship validStances;
		readonly int duration;

		int ticks;

		public RevealShroudEffect(WPos pos, WDist radius, Shroud.SourceType type, Player forPlayer, PlayerRelationship stances, int delay = 0, int duration = 50)
		{
			this.pos = pos;
			player = forPlayer;
			revealRadius = radius;
			validStances = stances;
			sourceType = type;
			this.duration = duration;
			ticks = -delay;
		}

		internal RevealShroudEffect(World world, SnapshotReader r, MiniYaml yaml)
		{
			var nodes = yaml.ToDictionary();

			pos = FieldLoader.GetValue<WPos>(PosKey, nodes[PosKey].Value);
			player = r.ResolvePlayer(nodes[PlayerKey].Value);
			sourceType = FieldLoader.GetValue<Shroud.SourceType>(SourceTypeKey, nodes[SourceTypeKey].Value);
			revealRadius = FieldLoader.GetValue<WDist>(RevealRadiusKey, nodes[RevealRadiusKey].Value);
			validStances = FieldLoader.GetValue<PlayerRelationship>(ValidStancesKey, nodes[ValidStancesKey].Value);
			duration = FieldLoader.GetValue<int>(DurationKey, nodes[DurationKey].Value);
			ticks = FieldLoader.GetValue<int>(TicksKey, nodes[TicksKey].Value);

			if (player != null && ticks > 0 && ticks < duration)
			{
				var cells = ProjectedCells(world);
				foreach (var p in world.Players)
					AddCellsToPlayerShroud(p, cells);
			}
		}

		bool IRequiresRestoredReferences.ReferencesRestored => player != null;

		List<MiniYamlNode> ISaveableEffect.SaveState(World world, SnapshotWriter w)
		{
			return
			[
				new(PosKey, FieldSaver.FormatValue(pos)),
				new(PlayerKey, w.PlayerRef(player)),
				new(SourceTypeKey, FieldSaver.FormatValue(sourceType)),
				new(RevealRadiusKey, FieldSaver.FormatValue(revealRadius)),
				new(ValidStancesKey, FieldSaver.FormatValue(validStances)),
				new(DurationKey, FieldSaver.FormatValue(duration)),
				new(TicksKey, FieldSaver.FormatValue(ticks))
			];
		}

		void AddCellsToPlayerShroud(Player p, PPos[] uv)
		{
			if (!validStances.HasRelationship(player.RelationshipWith(p)))
				return;

			p.Shroud.AddSource(this, sourceType, uv);
		}

		void RemoveCellsFromPlayerShroud(Player p) { p.Shroud.RemoveSource(this); }

		PPos[] ProjectedCells(World world)
		{
			var map = world.Map;
			var range = revealRadius;
			if (range == WDist.Zero)
				return NoCells;

			return Shroud.ProjectedCellsInRange(map, pos, WDist.Zero, range).ToArray();
		}

		public void Tick(World world)
		{
			if (ticks == 0)
			{
				var cells = ProjectedCells(world);
				foreach (var p in world.Players)
					AddCellsToPlayerShroud(p, cells);
			}

			if (ticks == duration)
			{
				foreach (var p in world.Players)
					RemoveCellsFromPlayerShroud(p);

				world.AddFrameEndTask(w => w.Remove(this));
			}

			ticks++;
		}

		public IEnumerable<IRenderable> Render(WorldRenderer wr) { return SpriteRenderable.None; }
	}
}
