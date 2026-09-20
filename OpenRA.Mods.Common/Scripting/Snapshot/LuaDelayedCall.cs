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
using OpenRA.Effects;
using OpenRA.GameSaves;
using OpenRA.Graphics;
using OpenRA.Scripting;

namespace OpenRA.Mods.Common.Scripting.Snapshot
{
	[SaveableEffect]
	public class LuaDelayedCall : IEffect, ISaveableEffect, ILuaHandleHolder
	{
		const string DelayKey = "Delay";
		const string HandleKey = "Handle";

		readonly ScriptContext context;

		LuaFunction function;
		int delay;

		readonly int savedHandle = -1;

		public LuaDelayedCall(ScriptContext context, int delay, LuaFunction function)
		{
			this.context = context;
			this.delay = delay;

			this.function = (LuaFunction)function.CopyReference();
		}

		internal LuaDelayedCall(World world, SnapshotReader r, MiniYaml yaml)
		{
			var nodes = yaml.ToDictionary();

			delay = FieldLoader.GetValue<int>(DelayKey, nodes[DelayKey].Value);
			savedHandle = FieldLoader.GetValue<int>(HandleKey, nodes[HandleKey].Value);

			context = this.RegisterForHandles(world);
		}

		void ILuaHandleHolder.ResolveHandles(ScriptContext context)
		{
			function = (LuaFunction)context.ResolveHandle(savedHandle).CopyReference();
		}

		List<MiniYamlNode> ISaveableEffect.SaveState(World world, SnapshotWriter w)
		{
			if (function == null)
				return null;

			return
			[
				new(DelayKey, FieldSaver.FormatValue(delay)),
				new(HandleKey, FieldSaver.FormatValue(context.RegisterHandle(function)))
			];
		}

		public void Tick(World world)
		{
			if (function == null)
				return;

			if (--delay > 0)
				return;

			world.AddFrameEndTask(w =>
			{
				w.Remove(this);

				try
				{
					using (function)
						function.Call().Dispose();
				}
				catch (Exception e)
				{
					if (w.Disposing)
						return;

					context.FatalError(e);
				}
				finally
				{
					function = null;
				}
			});
		}

		public IEnumerable<IRenderable> Render(WorldRenderer wr) { return SpriteRenderable.None; }
	}
}
