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

using System.Collections.Generic;
using OpenRA.Effects;
using OpenRA.Graphics;

namespace OpenRA.Mods.Common.Effects
{
	/// <summary>
	/// Runs a simulation step after a delay, on the same tick as <see cref="DelayedAction"/> would.
	/// A subclass holds the data of its step instead of a delegate, so a save can write it.
	/// </summary>
	public abstract class DelayedSimEffect : IEffect
	{
		int delay;

		protected DelayedSimEffect(int delay)
		{
			this.delay = delay;
		}

		public void Tick(World world)
		{
			if (--delay <= 0)
				world.AddFrameEndTask(w => { w.Remove(this); Run(w); });
		}

		protected abstract void Run(World world);

		public IEnumerable<IRenderable> Render(WorldRenderer wr) { yield break; }
	}
}
