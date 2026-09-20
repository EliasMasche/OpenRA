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
using System.Collections.Generic;
using System.Linq;
using Eluant;
using OpenRA.Activities;
using OpenRA.GameSaves;
using OpenRA.Mods.Common.Activities;
using OpenRA.Scripting;

namespace OpenRA.Mods.Common.Scripting.Snapshot
{
	[SaveableActivity]
	public class LuaPatrolUntil : Activity, IDisposable, ILuaHandleHolder
	{
		const string WaypointsKey = "Waypoints";
		const string WaitKey = "Wait";
		const string HandleKey = "Handle";

		readonly CPos[] waypoints;
		readonly int wait;
		readonly ScriptContext context;

		LuaFunction function;

		readonly int savedHandle = -1;

		public LuaPatrolUntil(CPos[] waypoints, int wait, LuaFunction function, ScriptContext context)
		{
			this.waypoints = waypoints;
			this.wait = wait;
			this.context = context;
			this.function = (LuaFunction)function.CopyReference();
		}

		protected LuaPatrolUntil(Actor self, SnapshotReader r, MiniYaml yaml)
		{
			var nodes = yaml.ToDictionary();

			waypoints = FieldLoader.GetValue<CPos[]>(WaypointsKey, nodes[WaypointsKey].Value);
			wait = FieldLoader.GetValue<int>(WaitKey, nodes[WaitKey].Value);
			savedHandle = FieldLoader.GetValue<int>(HandleKey, nodes[HandleKey].Value);

			context = this.RegisterForHandles(self.World);
		}

		void ILuaHandleHolder.ResolveHandles(ScriptContext context)
		{
			function = (LuaFunction)context.ResolveHandle(savedHandle).CopyReference();
		}

		public override bool Tick(Actor self)
		{
			if (IsCanceling || function == null)
			{
				Dispose();
				return true;
			}

			foreach (var wpt in waypoints)
			{
				self.QueueActivity(new AttackMoveActivity(self, MoveSpec.ToCellAt(wpt, 2)));
				self.QueueActivity(new Wait(wait));
			}

			bool repeat;
			try
			{
				using (var s = self.ToLuaValue(context))
					repeat = function.Call(s).First().ToBoolean();
			}
			catch (Exception ex)
			{
				context.FatalError(ex);
				Dispose();
				return true;
			}

			if (repeat)
				self.QueueActivity(new LuaPatrolUntil(waypoints, wait, function, context));

			Dispose();
			return true;
		}

		public override void Cancel(Actor self, bool keepQueue = false)
		{
			base.Cancel(self, keepQueue);
			Dispose();
		}

		public override List<MiniYamlNode> SaveState(Actor self, SnapshotWriter w)
		{
			if (function == null)
				return null;

			return
			[
				new(WaypointsKey, FieldSaver.FormatValue(waypoints)),
				new(WaitKey, FieldSaver.FormatValue(wait)),
				new(HandleKey, FieldSaver.FormatValue(context.RegisterHandle(function)))
			];
		}

		public void Dispose()
		{
			if (function == null)
				return;

			function.Dispose();
			function = null;
		}
	}
}
