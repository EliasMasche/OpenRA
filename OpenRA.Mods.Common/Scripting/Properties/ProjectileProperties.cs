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
using OpenRA.GameSaves;
using OpenRA.Scripting;

namespace OpenRA.Mods.Common.Scripting
{
	[ScriptPropertyGroup("General")]
	public class ProjectileProperties : ScriptProjectileProperties
	{
		public ProjectileProperties(ScriptContext context, IProjectileScriptInfo projectile)
			: base(context, projectile) { }

		[Desc("The current position of the projectile.")]
		public WPos Position => Projectile.Position;

		[Desc("The position the projectile is travelling towards. For a guided projectile this ",
			"moves with its target.")]
		public WPos TargetPosition => Projectile.TargetPosition;

		[Desc("The actor that fired the projectile, or nil if that actor no longer exists.")]
		public Actor SourceActor => Projectile.SourceActor;

		[Desc("The name of the weapon that fired the projectile.")]
		public string Weapon => WeaponRefs.WeaponKeyOf(Context.World, Projectile.Weapon);
	}
}
