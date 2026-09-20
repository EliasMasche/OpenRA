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

using OpenRA.Activities;
using OpenRA.GameSaves;
using OpenRA.Mods.Common.Traits;
using OpenRA.Primitives;
using OpenRA.Traits;

namespace OpenRA.Mods.Common.Activities
{
	public sealed class MoveSpec
	{
		const string KindKey = "Kind";
		const string CellKey = "Cell";
		const string TargetKey = "Target";
		const string NearEnoughKey = "NearEnough";
		const string MinRangeKey = "MinRange";
		const string MaxRangeKey = "MaxRange";
		const string IgnoreActorKey = "IgnoreActor";
		const string EvaluateNearestMovableCellKey = "EvaluateNearestMovableCell";
		const string TargetLineColorKey = "TargetLineColor";

		public enum MoveKind { ToCell, WithinRange, Follow }

		public readonly MoveKind Kind;
		public readonly CPos Cell;
		public readonly WDist NearEnough;
		public readonly WDist MinRange;
		public readonly WDist MaxRange;
		public readonly bool EvaluateNearestMovableCell;
		public readonly Color? TargetLineColor;

		Target target;
		Actor ignoreActor;

		MoveSpec(MoveKind kind, CPos cell, in Target target, WDist nearEnough, WDist minRange, WDist maxRange,
			Actor ignoreActor, bool evaluateNearestMovableCell, Color? targetLineColor)
		{
			Kind = kind;
			Cell = cell;
			this.target = target;
			NearEnough = nearEnough;
			MinRange = minRange;
			MaxRange = maxRange;
			this.ignoreActor = ignoreActor;
			EvaluateNearestMovableCell = evaluateNearestMovableCell;
			TargetLineColor = targetLineColor;
		}

		public static MoveSpec ToCellAt(CPos cell, int nearEnough = 0, Actor ignoreActor = null,
			bool evaluateNearestMovableCell = false, Color? targetLineColor = null)
		{
			return new MoveSpec(MoveKind.ToCell, cell, Target.Invalid, WDist.FromCells(nearEnough),
				WDist.Zero, WDist.Zero, ignoreActor, evaluateNearestMovableCell, targetLineColor);
		}

		public static MoveSpec WithinRangeOf(in Target target, WDist maxRange, Color? targetLineColor = null)
		{
			return new MoveSpec(MoveKind.WithinRange, CPos.Zero, target, WDist.Zero,
				WDist.Zero, maxRange, null, false, targetLineColor);
		}

		public static MoveSpec Following(in Target target, WDist minRange, WDist maxRange, Color? targetLineColor = null)
		{
			return new MoveSpec(MoveKind.Follow, CPos.Zero, target, WDist.Zero,
				minRange, maxRange, null, false, targetLineColor);
		}

		public Activity Resolve(Actor self, IMove move)
		{
			return Kind switch
			{
				MoveKind.WithinRange => move.MoveWithinRange(target, MinRange, MaxRange, null, TargetLineColor),
				MoveKind.Follow => move.MoveFollow(self, target, MinRange, MaxRange, null, TargetLineColor),
				_ => move.MoveTo(Cell, NearEnough.Length / 1024, ignoreActor, EvaluateNearestMovableCell, TargetLineColor)
			};
		}

		public MiniYaml SaveState(SnapshotWriter w)
		{
			return new MiniYaml("",
			[
				new MiniYamlNode(KindKey, FieldSaver.FormatValue(Kind)),
				new MiniYamlNode(CellKey, FieldSaver.FormatValue(Cell)),
				new MiniYamlNode(TargetKey, w.TargetRef(target)),
				new MiniYamlNode(NearEnoughKey, FieldSaver.FormatValue(NearEnough)),
				new MiniYamlNode(MinRangeKey, FieldSaver.FormatValue(MinRange)),
				new MiniYamlNode(MaxRangeKey, FieldSaver.FormatValue(MaxRange)),
				new MiniYamlNode(IgnoreActorKey, w.ActorRef(ignoreActor)),
				new MiniYamlNode(EvaluateNearestMovableCellKey, FieldSaver.FormatValue(EvaluateNearestMovableCell)),
				new MiniYamlNode(TargetLineColorKey, TargetLineColor.HasValue ? FieldSaver.FormatValue(TargetLineColor.Value) : "")
			]);
		}

		public static MoveSpec LoadState(MiniYaml yaml, SnapshotReader r)
		{
			var nodes = yaml.ToDictionary();
			var color = nodes[TargetLineColorKey].Value;

			var spec = new MoveSpec(
				FieldLoader.GetValue<MoveKind>(KindKey, nodes[KindKey].Value),
				FieldLoader.GetValue<CPos>(CellKey, nodes[CellKey].Value),
				Target.Invalid,
				FieldLoader.GetValue<WDist>(NearEnoughKey, nodes[NearEnoughKey].Value),
				FieldLoader.GetValue<WDist>(MinRangeKey, nodes[MinRangeKey].Value),
				FieldLoader.GetValue<WDist>(MaxRangeKey, nodes[MaxRangeKey].Value),
				null,
				FieldLoader.GetValue<bool>(EvaluateNearestMovableCellKey, nodes[EvaluateNearestMovableCellKey].Value),
				string.IsNullOrEmpty(color) ? null : FieldLoader.GetValue<Color>(TargetLineColorKey, color));

			r.DeferTarget(nodes[TargetKey].Value, t => spec.target = t);
			r.DeferActor(nodes[IgnoreActorKey].Value, a => spec.ignoreActor = a);

			return spec;
		}
	}
}
