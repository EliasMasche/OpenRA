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
using OpenRA.Primitives;
using OpenRA.Traits;

namespace OpenRA.Mods.Common.Activities
{
	[SaveableActivity]
	public class MoveOntoAndTurn : MoveOnto
	{
		const string DesiredFacingKey = "DesiredFacing";

		readonly WAngle? desiredFacing;

		public MoveOntoAndTurn(Actor self, in Target target, in WVec offset, WAngle? desiredFacing, Color? targetLineColor = null)
			: base(self, target, offset, null, targetLineColor)
		{
			this.desiredFacing = desiredFacing;
		}

		internal MoveOntoAndTurn(Actor self, SnapshotReader r, MiniYaml yaml)
			: base(self, r, yaml)
		{
			var facing = yaml.NodeWithKeyOrDefault(DesiredFacingKey).Value.Value;
			if (!string.IsNullOrEmpty(facing))
				desiredFacing = FieldLoader.GetValue<WAngle>(DesiredFacingKey, facing);
		}

		public override bool Tick(Actor self)
		{
			if (base.Tick(self))
			{
				if (!IsCanceling && desiredFacing.HasValue && desiredFacing.Value != Mobile.Facing)
				{
					QueueChild(new Turn(self, desiredFacing.Value));
					return false;
				}

				return true;
			}

			return false;
		}

		public override List<MiniYamlNode> SaveState(Actor self, SnapshotWriter w)
		{
			var nodes = base.SaveState(self, w);
			nodes.Add(new MiniYamlNode(DesiredFacingKey, desiredFacing.HasValue ? FieldSaver.FormatValue(desiredFacing.Value) : ""));
			return nodes;
		}
	}
}
