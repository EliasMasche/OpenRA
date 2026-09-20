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
using OpenRA.GameRules;

namespace OpenRA.Mods.Common.Traits
{
	sealed class ArmamentProjectileSource : IProjectileSource
	{
		readonly Actor self;
		readonly Armament armament;
		readonly Barrel barrel;

		public ArmamentProjectileSource(Actor self, Armament armament, Barrel barrel)
		{
			ArgumentNullException.ThrowIfNull(self);
			ArgumentNullException.ThrowIfNull(armament);
			ArgumentNullException.ThrowIfNull(barrel);

			this.self = self;
			this.armament = armament;
			this.barrel = barrel;
		}

		public WPos CurrentSource()
		{
			return self.CenterPosition + armament.MuzzleOffset(self, barrel);
		}

		public WAngle CurrentMuzzleFacing()
		{
			return armament.MuzzleOrientation(self, barrel).Yaw;
		}

		public int SaveBarrel()
		{
			return Array.IndexOf(armament.Barrels, barrel);
		}

		public IProvidesProjectileSource SaveOwner()
		{
			return armament;
		}
	}
}
