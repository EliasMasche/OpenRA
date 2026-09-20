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
using OpenRA.Activities;
using OpenRA.GameSaves;
using OpenRA.Mods.Common.Activities;

namespace OpenRA.Mods.Common.Scripting.Snapshot
{
	[SaveableActivity]
	public class LuaPatrol : Activity
	{
		const string WaypointsKey = "Waypoints";
		const string WaitKey = "Wait";

		readonly CPos[] waypoints;
		readonly int wait;

		public LuaPatrol(CPos[] waypoints, int wait)
		{
			this.waypoints = waypoints;
			this.wait = wait;
		}

		protected LuaPatrol(Actor self, SnapshotReader r, MiniYaml yaml)
		{
			var nodes = yaml.ToDictionary();

			waypoints = FieldLoader.GetValue<CPos[]>(WaypointsKey, nodes[WaypointsKey].Value);
			wait = FieldLoader.GetValue<int>(WaitKey, nodes[WaitKey].Value);
		}

		public override bool Tick(Actor self)
		{
			if (IsCanceling)
				return true;

			foreach (var wpt in waypoints)
			{
				self.QueueActivity(new AttackMoveActivity(self, MoveSpec.ToCellAt(wpt, 2)));
				self.QueueActivity(new Wait(wait));
			}

			self.QueueActivity(new LuaPatrol(waypoints, wait));
			return true;
		}

		public override List<MiniYamlNode> SaveState(Actor self, SnapshotWriter w)
		{
			return
			[
				new(WaypointsKey, FieldSaver.FormatValue(waypoints)),
				new(WaitKey, FieldSaver.FormatValue(wait))
			];
		}
	}
}
