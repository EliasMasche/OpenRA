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
using OpenRA.Scripting;

namespace OpenRA.Mods.Common.Scripting.Snapshot
{
	[SaveableActivity]
	public sealed class LuaCallWithCargo : Activity, IDisposable, ILuaHandleHolder
	{
		const string HandleKey = "Handle";
		const string CargoKey = "Cargo";

		readonly ScriptContext context;
		readonly List<Actor> cargo = [];

		LuaFunction function;

		readonly int savedHandle = -1;

		public LuaCallWithCargo(IEnumerable<Actor> cargo, LuaFunction function, ScriptContext context)
		{
			this.cargo.AddRange(cargo);
			this.context = context;
			this.function = (LuaFunction)function.CopyReference();
		}

		LuaCallWithCargo(Actor self, SnapshotReader r, MiniYaml yaml)
		{
			var nodes = yaml.ToDictionary();

			savedHandle = FieldLoader.GetValue<int>(HandleKey, nodes[HandleKey].Value);

			context = this.RegisterForHandles(self.World);

			if (nodes.TryGetValue(CargoKey, out var saved) && !string.IsNullOrEmpty(saved.Value))
				foreach (var reference in saved.Value.Split(',', StringSplitOptions.RemoveEmptyEntries))
					r.DeferActor(reference, a => { if (a != null) cargo.Add(a); });
		}

		void ILuaHandleHolder.ResolveHandles(ScriptContext context)
		{
			function = (LuaFunction)context.ResolveHandle(savedHandle).CopyReference();
		}

		public override bool Tick(Actor self)
		{
			if (function == null)
				return true;

			try
			{
				var alive = cargo.Where(a => a != null && !a.Disposed).ToArray();
				using (LuaValue t = self.ToLuaValue(context), p = alive.ToLuaValue(context))
					function.Call(t, p).Dispose();
			}
			catch (Exception ex)
			{
				context.FatalError(ex);
			}

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
				new(HandleKey, FieldSaver.FormatValue(context.RegisterHandle(function))),
				new(CargoKey, cargo.Select(w.ActorRef).JoinWith(","))
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
