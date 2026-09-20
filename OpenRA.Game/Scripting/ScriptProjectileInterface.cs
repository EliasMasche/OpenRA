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

using OpenRA.GameRules;

namespace OpenRA.Scripting
{
	public class ScriptProjectileInterface : ScriptObjectWrapper
	{
		readonly IProjectileScriptInfo projectile;

		protected override string DuplicateKeyError(string memberName)
		{
			return $"Projectile '{projectile.Weapon.GetType().Name}' defines the command '{memberName}' on multiple groups";
		}

		protected override string MemberNotFoundError(string memberName)
		{
			return $"Projectile does not define a property '{memberName}'";
		}

		public ScriptProjectileInterface(ScriptContext context, IProjectileScriptInfo projectile)
			: base(context)
		{
			this.projectile = projectile;

			Bind(CreateObjects(Context.ProjectileCommands, [Context, projectile]));
		}
	}
}
