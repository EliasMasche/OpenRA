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

using OpenRA.Scripting;

namespace OpenRA.Mods.Common.Scripting.Snapshot
{
	public interface ILuaHandleHolder
	{
		void ResolveHandles(ScriptContext context);
	}

	public static class LuaHandleHolderExts
	{
		public static ScriptContext RegisterForHandles(this ILuaHandleHolder holder, World world)
		{
			var script = world.WorldActor.Trait<LuaScript>();
			script.RegisterHandleHolder(holder);

			return script.Context;
		}
	}
}
