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
using Eluant;
using OpenRA.Activities;
using OpenRA.GameSaves;
using OpenRA.Scripting;

namespace OpenRA.Mods.Common.Scripting.Snapshot
{
	[SaveableActivity]
	public sealed class LuaCallWithSelf : Activity, IDisposable, ILuaHandleHolder
	{
		const string HandleKey = "Handle";

		readonly ScriptContext context;

		LuaFunction function;

		readonly int savedHandle = -1;

		public LuaCallWithSelf(LuaFunction function, ScriptContext context)
		{
			this.function = (LuaFunction)function.CopyReference();
			this.context = context;
		}

		LuaCallWithSelf(Actor self, SnapshotReader r, MiniYaml yaml)
		{
			savedHandle = FieldLoader.GetValue<int>(HandleKey, yaml.NodeWithKeyOrDefault(HandleKey)?.Value.Value);

			context = this.RegisterForHandles(self.World);
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
				using (var a = self.ToLuaValue(context))
					function.Call(a).Dispose();
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

		public LuaFunction PendingFunction => function;

		public override List<MiniYamlNode> SaveState(Actor self, SnapshotWriter w)
		{
			if (function == null)
				return null;

			return [new(HandleKey, FieldSaver.FormatValue(context.RegisterHandle(function)))];
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
