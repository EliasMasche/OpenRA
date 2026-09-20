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
	public class Drag : Activity
	{
		const string StartKey = "Start";
		const string EndKey = "End";
		const string LengthKey = "Length";
		const string TicksKey = "Ticks";
		const string DesiredFacingKey = "DesiredFacing";

		readonly IPositionable positionable;
		readonly IDisabledTrait disableable;
		readonly WPos start;
		readonly WPos end;
		readonly int length;
		int ticks = 0;
		readonly WAngle? desiredFacing;

		public Drag(Actor self, WPos start, WPos end, int length, WAngle? facing = null)
		{
			positionable = self.Trait<IPositionable>();
			disableable = self.TraitOrDefault<IMove>() as IDisabledTrait;
			this.start = start;
			this.end = end;
			this.length = length;
			desiredFacing = facing;
			IsInterruptible = false;
		}

		protected Drag(Actor self, SnapshotReader r, MiniYaml yaml)
		{
			positionable = self.Trait<IPositionable>();
			disableable = self.TraitOrDefault<IMove>() as IDisabledTrait;

			var nodes = yaml.ToDictionary();
			start = FieldLoader.GetValue<WPos>(StartKey, nodes[StartKey].Value);
			end = FieldLoader.GetValue<WPos>(EndKey, nodes[EndKey].Value);
			length = FieldLoader.GetValue<int>(LengthKey, nodes[LengthKey].Value);
			ticks = FieldLoader.GetValue<int>(TicksKey, nodes[TicksKey].Value);

			var facing = nodes[DesiredFacingKey].Value;
			if (!string.IsNullOrEmpty(facing))
				desiredFacing = FieldLoader.GetValue<WAngle>(DesiredFacingKey, facing);
		}

		protected override void OnFirstRun(Actor self)
		{
			if (desiredFacing.HasValue)
				QueueChild(new Turn(self, desiredFacing.Value));
		}

		public override bool Tick(Actor self)
		{
			if (disableable != null && disableable.IsTraitDisabled)
				return false;

			var pos = length > 1
				? WPos.Lerp(start, end, ticks, length - 1)
				: end;

			positionable.SetCenterPosition(self, pos);
			if (++ticks >= length)
				return true;

			return false;
		}

		public override IEnumerable<Target> GetTargets(Actor self)
		{
			yield return Target.FromPos(end);
		}

		public override IEnumerable<TargetLineNode> TargetLineNodes(Actor self)
		{
			yield return new TargetLineNode(Target.FromPos(end), Color.Green);
		}

		public override List<MiniYamlNode> SaveState(Actor self, SnapshotWriter w)
		{
			return
			[
				new(StartKey, FieldSaver.FormatValue(start)),
				new(EndKey, FieldSaver.FormatValue(end)),
				new(LengthKey, FieldSaver.FormatValue(length)),
				new(TicksKey, FieldSaver.FormatValue(ticks)),

				new(DesiredFacingKey, desiredFacing.HasValue ? FieldSaver.FormatValue(desiredFacing.Value) : "")
			];
		}
	}
}
