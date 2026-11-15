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
using OpenRA.Mods.Cnc.Traits;
using OpenRA.Traits;

namespace OpenRA.Mods.Cnc.Effects
{
	sealed class GpsSatellite : IEffect, ISpatiallyPartitionable, IRebuildAfterLoad
	{
		readonly string image;
		readonly string sequence;
		readonly Player launcher;

		[NotSaved("Built again after a load, from the image and the sequence.")]
		Animation anim;

		readonly string palette;
		readonly int revealDelay;
		WPos pos;
		int tick;

		public GpsSatellite(World world, WPos pos, string image, string sequence, string palette, int revealDelay, Player launcher)
		{
			this.image = image;
			this.sequence = sequence;
			this.palette = palette;
			this.pos = pos;
			this.launcher = launcher;
			this.revealDelay = revealDelay;

			BuildAnimation(world);
		}

		void BuildAnimation(World world)
		{
			anim = new Animation(world, image);
			anim.PlayRepeating(sequence);
			world.ScreenMap.Add(this, pos, anim.Image);
		}

		void IRebuildAfterLoad.RebuildAfterLoad(Actor self)
		{
			BuildAnimation(launcher.World);
		}

		public void Tick(World world)
		{
			anim.Tick();
			pos += new WVec(0, 0, 427);

			if (++tick > revealDelay)
			{
				var watcher = launcher.PlayerActor.Trait<GpsWatcher>();
				watcher.ReachedOrbit(launcher);
				world.AddFrameEndTask(w => { w.Remove(this); w.ScreenMap.Remove(this); });
			}

			world.ScreenMap.Update(this, pos, anim.Image);
		}

		public IEnumerable<IRenderable> Render(WorldRenderer wr)
		{
			if (anim == null)
				return [];

			return anim.Render(pos, wr.Palette(palette));
		}
	}
}
