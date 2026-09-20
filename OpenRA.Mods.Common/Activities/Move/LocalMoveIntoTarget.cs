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
using OpenRA.Activities;
using OpenRA.GameSaves;
using OpenRA.Mods.Common.Traits;
using OpenRA.Primitives;
using OpenRA.Traits;

namespace OpenRA.Mods.Common.Activities
{
	[SaveableActivity]
	public class LocalMoveIntoTarget : Activity
	{
		const string TargetKey = "Target";
		const string TargetMovementThresholdKey = "TargetMovementThreshold";
		const string TargetStartPosKey = "TargetStartPos";
		const string TargetLineColorKey = "TargetLineColor";

		readonly Mobile mobile;
		readonly Color? targetLineColor;
		readonly WDist targetMovementThreshold;

		Target target;
		WPos targetStartPos;

		public LocalMoveIntoTarget(Actor self, in Target target, WDist targetMovementThreshold, Color? targetLineColor = null)
		{
			mobile = self.Trait<Mobile>();
			this.target = target;
			this.targetMovementThreshold = targetMovementThreshold;
			this.targetLineColor = targetLineColor;
		}

		protected LocalMoveIntoTarget(Actor self, SnapshotReader r, MiniYaml yaml)
		{
			mobile = self.Trait<Mobile>();

			var nodes = yaml.ToDictionary();
			targetMovementThreshold = FieldLoader.GetValue<WDist>(TargetMovementThresholdKey, nodes[TargetMovementThresholdKey].Value);
			targetStartPos = FieldLoader.GetValue<WPos>(TargetStartPosKey, nodes[TargetStartPosKey].Value);

			var color = nodes[TargetLineColorKey].Value;
			if (!string.IsNullOrEmpty(color))
				targetLineColor = FieldLoader.GetValue<Color>(TargetLineColorKey, color);

			r.DeferTarget(nodes[TargetKey].Value, t => target = t);
		}

		protected override void OnFirstRun(Actor self)
		{
			targetStartPos = target.Positions.ClosestToIgnoringPath(self.CenterPosition);
		}

		public override bool Tick(Actor self)
		{
			if (IsCanceling || target.Type == TargetType.Invalid)
				return true;

			if (mobile.IsTraitDisabled || mobile.IsTraitPaused)
				return false;

			var currentPos = self.CenterPosition;
			var targetPos = target.Positions.ClosestToIgnoringPath(currentPos);

			// Give up if the target has moved too far
			if (targetMovementThreshold > WDist.Zero && (targetPos - targetStartPos).LengthSquared > targetMovementThreshold.LengthSquared)
				return true;

			// Turn if required
			var delta = targetPos - currentPos;
			var facing = delta.HorizontalLengthSquared != 0 ? delta.Yaw : mobile.Facing;
			if (facing != mobile.Facing)
			{
				mobile.Facing = Util.TickFacing(mobile.Facing, facing, mobile.TurnSpeed);
				return false;
			}

			// Can complete the move in this step
			var speed = mobile.MovementSpeedForCell(self.Location);
			if (delta.LengthSquared <= speed * speed)
			{
				mobile.SetCenterPosition(self, targetPos);
				return true;
			}

			// Move towards the target
			mobile.SetCenterPosition(self, currentPos + delta * speed / delta.Length);
			return false;
		}

		public override IEnumerable<Target> GetTargets(Actor self)
		{
			yield return target;
		}

		public override IEnumerable<TargetLineNode> TargetLineNodes(Actor self)
		{
			if (targetLineColor != null)
				yield return new TargetLineNode(target, targetLineColor.Value);
		}

		public override List<MiniYamlNode> SaveState(Actor self, SnapshotWriter w)
		{
			return
			[
				new(TargetKey, w.TargetRef(target)),
				new(TargetMovementThresholdKey, FieldSaver.FormatValue(targetMovementThreshold)),

				new(TargetStartPosKey, FieldSaver.FormatValue(targetStartPos)),
				new(TargetLineColorKey, targetLineColor.HasValue ? FieldSaver.FormatValue(targetLineColor.Value) : "")
			];
		}
	}
}
