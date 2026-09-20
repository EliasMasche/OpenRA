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

namespace OpenRA.GameRules
{
	public interface IProjectileSource
	{
		WPos CurrentSource();
		WAngle CurrentMuzzleFacing();

		int SaveBarrel();

		IProvidesProjectileSource SaveOwner();
	}

	public interface IProvidesProjectileSource
	{
		string InstanceName { get; }

		IProjectileSource ProvideProjectileSource(Actor self, int barrel);
	}

	public sealed class FrozenProjectileSource : IProjectileSource
	{
		readonly WPos source;
		readonly WAngle facing;

		public FrozenProjectileSource(WPos source, WAngle facing)
		{
			this.source = source;
			this.facing = facing;
		}

		public WPos CurrentSource() { return source; }
		public WAngle CurrentMuzzleFacing() { return facing; }
		public int SaveBarrel() { return -1; }
		public IProvidesProjectileSource SaveOwner() { return null; }
	}
}
