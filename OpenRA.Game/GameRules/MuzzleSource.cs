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
	/// <summary>
	/// Where a projectile in flight reads its launcher's current muzzle.
	/// It is a named object rather than a delegate so the projectile's state stays plain data.
	/// </summary>
	public interface IMuzzleSource
	{
		WPos Position { get; }
		WAngle Facing { get; }
	}

	public sealed class FixedMuzzleSource : IMuzzleSource
	{
		public WPos Position { get; }
		public WAngle Facing { get; }

		public FixedMuzzleSource(WPos position, WAngle facing)
		{
			Position = position;
			Facing = facing;
		}
	}

	public sealed class ActorCenterMuzzleSource : IMuzzleSource
	{
		readonly Actor actor;

		public ActorCenterMuzzleSource(Actor actor)
		{
			this.actor = actor;
		}

		public WPos Position => actor.CenterPosition;
		public WAngle Facing => WAngle.Zero;
	}
}
