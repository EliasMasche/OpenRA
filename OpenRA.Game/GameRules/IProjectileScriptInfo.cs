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

using Eluant;
using Eluant.ObjectBinding;
using OpenRA.Scripting;

namespace OpenRA.GameRules
{
	public interface IProjectileScriptInfo : IScriptBindable, IScriptNotifyBind, ILuaTableBinding
	{
		WPos Position { get; }

		WPos TargetPosition { get; }

		Actor SourceActor { get; }

		WeaponInfo Weapon { get; }

		protected ScriptProjectileInterface LuaInterface { get; set; }

		void IScriptNotifyBind.OnScriptBind(ScriptContext context)
		{
			LuaInterface ??= new ScriptProjectileInterface(context, this);
		}

		LuaValue ILuaTableBinding.this[LuaRuntime runtime, LuaValue keyValue]
		{
			get => LuaInterface[runtime, keyValue];
			set => LuaInterface[runtime, keyValue] = value;
		}
	}
}
