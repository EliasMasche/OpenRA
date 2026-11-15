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
using OpenRA.Activities;
using OpenRA.Mods.Common.Traits;
using OpenRA.Primitives;
using OpenRA.Traits;

namespace OpenRA.Mods.Common.Activities
{
	public enum MoveIntentType { MoveTo, MoveWithinRangeOfActorCell, MoveFollow }

	/// <summary>
	/// The move an <see cref="AttackMoveActivity"/> issues again after each engagement.
	/// It describes the move as data instead of holding a factory delegate, so the activity's state stays saveable.
	/// </summary>
	public readonly struct MoveIntent
	{
		public readonly MoveIntentType Type;
		public readonly IMove Mover;
		public readonly CPos Cell;
		public readonly int NearEnough;
		public readonly Actor IgnoreActor;
		public readonly bool EvaluateNearestMovableCell;
		public readonly Actor TargetActor;
		public readonly Target Target;
		public readonly WDist MinRange;
		public readonly WDist MaxRange;
		public readonly Color? TargetLineColor;

		MoveIntent(MoveIntentType type, IMove mover, CPos cell = default, int nearEnough = 0, Actor ignoreActor = null,
			bool evaluateNearestMovableCell = false, Actor targetActor = null, Target target = default,
			WDist minRange = default, WDist maxRange = default, Color? targetLineColor = null)
		{
			Type = type;
			Mover = mover;
			Cell = cell;
			NearEnough = nearEnough;
			IgnoreActor = ignoreActor;
			EvaluateNearestMovableCell = evaluateNearestMovableCell;
			TargetActor = targetActor;
			Target = target;
			MinRange = minRange;
			MaxRange = maxRange;
			TargetLineColor = targetLineColor;
		}

		public static MoveIntent MoveTo(IMove mover, CPos cell, int nearEnough = 0, Actor ignoreActor = null,
			bool evaluateNearestMovableCell = false, Color? targetLineColor = null)
		{
			return new MoveIntent(MoveIntentType.MoveTo, mover, cell, nearEnough, ignoreActor,
				evaluateNearestMovableCell, targetLineColor: targetLineColor);
		}

		/// <summary>Moves within range of the cell the target actor occupies when the move is issued, not when it is described.</summary>
		public static MoveIntent MoveWithinRangeOfActorCell(IMove mover, Actor targetActor, WDist range)
		{
			return new MoveIntent(MoveIntentType.MoveWithinRangeOfActorCell, mover, targetActor: targetActor, maxRange: range);
		}

		public static MoveIntent MoveFollow(IMove mover, in Target target, WDist minRange, WDist maxRange, Color? targetLineColor = null)
		{
			return new MoveIntent(MoveIntentType.MoveFollow, mover, target: target, minRange: minRange, maxRange: maxRange,
				targetLineColor: targetLineColor);
		}

		public Activity Create(Actor self)
		{
			switch (Type)
			{
				case MoveIntentType.MoveTo:
					return Mover.MoveTo(Cell, NearEnough, IgnoreActor, EvaluateNearestMovableCell, TargetLineColor);
				case MoveIntentType.MoveWithinRangeOfActorCell:
					return Mover.MoveWithinRange(Target.FromCell(self.World, TargetActor.Location), MaxRange);
				case MoveIntentType.MoveFollow:
					return Mover.MoveFollow(self, Target, MinRange, MaxRange, targetLineColor: TargetLineColor);
				default:
					throw new InvalidOperationException($"Unknown move intent type {Type}.");
			}
		}
	}
}
