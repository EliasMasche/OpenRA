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
using OpenRA.Mods.Common.Scripting;
using OpenRA.Mods.Common.Scripting.Snapshot;
using OpenRA.Scripting;

namespace OpenRA.Mods.Common.Activities
{
	[SaveableActivity]
	public sealed class CallLuaFunc : Activity, IDisposable, ILuaHandleHolder
	{
		const string HandleKey = "Handle";

		readonly ScriptContext context;
		LuaFunction function;

		readonly int savedHandle = -1;

		public CallLuaFunc(LuaFunction function, ScriptContext context)
		{
			this.function = (LuaFunction)function.CopyReference();
			this.context = context;
		}

		CallLuaFunc(Actor self, SnapshotReader r, MiniYaml yaml)
		{
			savedHandle = FieldLoader.GetValue<int>(HandleKey, yaml.NodeWithKeyOrDefault(HandleKey)?.Value.Value);

			context = this.RegisterForHandles(self.World);
		}

		void ILuaHandleHolder.ResolveHandles(ScriptContext context)
		{
			function = (LuaFunction)context.ResolveHandle(savedHandle).CopyReference();
		}

		public override List<MiniYamlNode> SaveState(Actor self, SnapshotWriter w)
		{
			if (function == null)
				return null;

			return [new(HandleKey, FieldSaver.FormatValue(context.RegisterHandle(function)))];
		}

		public override bool Tick(Actor self)
		{
			try
			{
				function?.Call().Dispose();
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

		public void Dispose()
		{
			if (function == null)
				return;

			function.Dispose();
			function = null;
		}
	}
}
