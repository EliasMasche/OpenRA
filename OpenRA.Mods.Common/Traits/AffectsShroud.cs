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
using OpenRA.Traits;

namespace OpenRA.Mods.Common.Traits
{
	public abstract class AffectsShroudInfo : ConditionalTraitInfo
	{
		public readonly WDist MinRange = WDist.Zero;

		public readonly WDist Range = WDist.Zero;

		[Desc("If >= 0, prevent cells that are this much higher than the actor from being revealed.")]
		public readonly int MaxHeightDelta = -1;

		[Desc("If > 0, force visibility to be recalculated if the unit moves within a cell by more than this distance.")]
		public readonly WDist MoveRecalculationThreshold = new(256);

		[Desc("Possible values are CenterPosition (measure range from the center) and ",
			"Footprint (measure range from the footprint)")]
		public readonly VisibilityType Type = VisibilityType.Footprint;
	}

	public abstract class AffectsShroud : ConditionalTrait<AffectsShroudInfo>, ISync, INotifyAddedToWorld,
		INotifyRemovedFromWorld, INotifyMoving, INotifyCenterPositionChanged, ITick, IRebuildAfterLoad
	{
		static readonly PPos[] NoCells = [];

		readonly HashSet<PPos> footprint;

		// The cells that the actor occupied at the last computation of its footprint source. The save holds them,
		// because a load must compute the same source from them.
		readonly List<CPos> occupiedCells = [];

		// The position and the range of the last computation of the source. A change of range or of the disabled state
		// computes the source again from the current position. It leaves cachedPos for the next move to compare with.
		// So neither cachedPos nor the current range gives the cells that a load must give the source.
		WPos sourcePos;
		WDist sourceRange;

		[VerifySync]
		CPos cachedLocation;

		[VerifySync]
		WDist cachedRange;

		[VerifySync]
		protected bool CachedTraitDisabled { get; private set; }

		WPos cachedPos;

		protected abstract void AddCellsToPlayerShroud(Actor self, Player player, PPos[] uv);
		protected abstract void RemoveCellsFromPlayerShroud(Actor self, Player player);

		protected AffectsShroud(AffectsShroudInfo info)
			: base(info)
		{
			if (Info.Type == VisibilityType.Footprint)
				footprint = [];
		}

		PPos[] ProjectedCells(Actor self)
		{
			if (Info.Type == VisibilityType.Footprint)
			{
				// PERF: Reuse collection to avoid allocations.
				occupiedCells.Clear();
				foreach (var (cell, _) in self.OccupiesSpace.OccupiedCells())
					occupiedCells.Add(cell);
			}

			sourcePos = self.CenterPosition;
			sourceRange = Range;
			return ProjectedCells(self, sourcePos, sourceRange);
		}

		// A load adds the source from the current position. The saved counts hold the cells of the last computation of
		// the source. Those cells come off at the next computation.
		void IRebuildAfterLoad.RebuildAfterLoad(Actor self)
		{
			if (!self.IsInWorld)
				return;

			var cells = ProjectedCells(self, sourcePos, sourceRange);
			foreach (var p in self.World.Players)
				p.Shroud.RestoreSource(this, cells);
		}

		PPos[] ProjectedCells(Actor self, WPos pos, WDist maxRange)
		{
			var map = self.World.Map;
			var minRange = Info.MinRange;
			if (maxRange <= minRange)
				return NoCells;

			if (Info.Type == VisibilityType.Footprint)
			{
				// PERF: Reuse collection to avoid allocations.
				footprint.UnionWith(occupiedCells
					.SelectMany(cell => Shroud.ProjectedCellsInRange(map, map.CenterOfCell(cell), minRange, maxRange, Info.MaxHeightDelta)));
				var cells = footprint.ToArray();
				footprint.Clear();
				return cells;
			}

			if (Info.Type == VisibilityType.GroundPosition)
				pos -= new WVec(WDist.Zero, WDist.Zero, self.World.Map.DistanceAboveTerrain(pos));

			return Shroud.ProjectedCellsInRange(map, pos, minRange, maxRange, Info.MaxHeightDelta)
				.ToArray();
		}

		void INotifyCenterPositionChanged.CenterPositionChanged(Actor self, byte oldLayer, byte newLayer)
		{
			if (!self.IsInWorld)
				return;

			var centerPosition = self.CenterPosition;
			var projectedPos = centerPosition - new WVec(0, centerPosition.Z, centerPosition.Z);
			var projectedLocation = self.World.Map.CellContaining(projectedPos);
			var pos = self.CenterPosition;

			var dirty = Info.MoveRecalculationThreshold.Length > 0 && (pos - cachedPos).LengthSquared > Info.MoveRecalculationThreshold.LengthSquared;
			if (!dirty && cachedLocation == projectedLocation)
				return;

			cachedLocation = projectedLocation;
			cachedPos = pos;

			UpdateShroudCells(self);
		}

		void ITick.Tick(Actor self)
		{
			if (!self.IsInWorld)
				return;

			var traitDisabled = IsTraitDisabled;
			var range = Range;

			if (cachedRange == range && traitDisabled == CachedTraitDisabled)
				return;

			cachedRange = range;
			CachedTraitDisabled = traitDisabled;

			UpdateShroudCells(self);
		}

		void UpdateShroudCells(Actor self)
		{
			var cells = ProjectedCells(self);
			foreach (var p in self.World.Players)
			{
				RemoveCellsFromPlayerShroud(self, p);
				AddCellsToPlayerShroud(self, p, cells);
			}
		}

		void INotifyAddedToWorld.AddedToWorld(Actor self)
		{
			var centerPosition = self.CenterPosition;
			var projectedPos = centerPosition - new WVec(0, centerPosition.Z, centerPosition.Z);
			cachedLocation = self.World.Map.CellContaining(projectedPos);
			cachedPos = centerPosition;
			CachedTraitDisabled = IsTraitDisabled;
			var cells = ProjectedCells(self);

			foreach (var p in self.World.Players)
				AddCellsToPlayerShroud(self, p, cells);
		}

		void INotifyRemovedFromWorld.RemovedFromWorld(Actor self)
		{
			foreach (var p in self.World.Players)
				RemoveCellsFromPlayerShroud(self, p);
		}

		public virtual WDist Range => CachedTraitDisabled ? WDist.Zero : Info.Range;

		void INotifyMoving.MovementTypeChanged(Actor self, MovementType type)
		{
			// Recalculate the visibility at our final stop position
			if (type == MovementType.None && self.IsInWorld)
			{
				var centerPosition = self.CenterPosition;
				var projectedPos = centerPosition - new WVec(0, centerPosition.Z, centerPosition.Z);
				var projectedLocation = self.World.Map.CellContaining(projectedPos);
				var pos = self.CenterPosition;

				cachedLocation = projectedLocation;
				cachedPos = pos;

				UpdateShroudCells(self);
			}
		}
	}
}
