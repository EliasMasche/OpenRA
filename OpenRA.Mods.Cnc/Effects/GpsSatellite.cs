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
using OpenRA.GameSaves;
using OpenRA.Graphics;
using OpenRA.Mods.Cnc.Traits;

namespace OpenRA.Mods.Cnc.Effects
{
	[SaveableEffect]
	sealed class GpsSatellite : IEffect, ISpatiallyPartitionable, ISaveableEffect, IRequiresRestoredReferences
	{
		const string LauncherKey = "Launcher";
		const string PosKey = "Pos";
		const string ImageKey = "Image";
		const string PaletteKey = "Palette";
		const string RevealDelayKey = "RevealDelay";
		const string TickKey = "Tick";
		const string AnimationKey = "Animation";

		readonly Player launcher;
		readonly Animation anim;
		readonly string image;
		readonly string palette;
		readonly int revealDelay;
		WPos pos;
		int tick;

		public GpsSatellite(World world, WPos pos, string image, string sequence, string palette, int revealDelay, Player launcher)
		{
			this.image = image;
			this.palette = palette;
			this.pos = pos;
			this.launcher = launcher;
			this.revealDelay = revealDelay;

			anim = new Animation(world, image);
			anim.PlayRepeating(sequence);

			if (world.Map.Sequences.SpritesLoaded)
				world.ScreenMap.Add(this, pos, anim.Image);
		}

		internal GpsSatellite(World world, SnapshotReader r, MiniYaml yaml)
		{
			var nodes = yaml.ToDictionary();

			image = nodes[ImageKey].Value;
			palette = nodes[PaletteKey].Value;
			pos = FieldLoader.GetValue<WPos>(PosKey, nodes[PosKey].Value);
			revealDelay = FieldLoader.GetValue<int>(RevealDelayKey, nodes[RevealDelayKey].Value);
			tick = FieldLoader.GetValue<int>(TickKey, nodes[TickKey].Value);
			launcher = r.ResolvePlayer(nodes[LauncherKey].Value);

			var state = AnimationCodec.Load(nodes[AnimationKey]);
			anim = new Animation(world, image);
			anim.ResumeRepeating(state.Sequence, state);

			if (world.Map.Sequences.SpritesLoaded)
				world.ScreenMap.Add(this, pos, anim.Image);
		}

		bool IRequiresRestoredReferences.ReferencesRestored => launcher != null;

		List<MiniYamlNode> ISaveableEffect.SaveState(World world, SnapshotWriter w)
		{
			var animation = AnimationCodec.Save(anim);
			if (animation == null)
				return null;

			return
			[
				new(LauncherKey, w.PlayerRef(launcher)),
				new(PosKey, FieldSaver.FormatValue(pos)),
				new(ImageKey, image ?? ""),
				new(PaletteKey, palette ?? ""),
				new(RevealDelayKey, FieldSaver.FormatValue(revealDelay)),
				new(TickKey, FieldSaver.FormatValue(tick)),
				new(AnimationKey, new MiniYaml("", animation))
			];
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

			if (world.Map.Sequences.SpritesLoaded)
				world.ScreenMap.Update(this, pos, anim.Image);
		}

		public IEnumerable<IRenderable> Render(WorldRenderer wr)
		{
			return anim.Render(pos, wr.Palette(palette));
		}
	}
}
