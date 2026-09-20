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
	sealed class SatelliteLaunch : IEffect, ISpatiallyPartitionable, ISaveableEffect, IRequiresRestoredReferences
	{
		const string LauncherKey = "Launcher";
		const string PosKey = "Pos";
		const string FrameKey = "Frame";
		const string DoorImageKey = "DoorImage";
		const string AnimationKey = "Animation";

		GpsPowerInfo info;

		Actor launcher;
		readonly Animation doors;
		readonly WPos pos;
		int frame = 0;

		public SatelliteLaunch(Actor launcher, GpsPowerInfo info)
		{
			this.info = info;
			this.launcher = launcher;

			doors = new Animation(launcher.World, info.DoorImage);
			doors.PlayThen(info.DoorSequence,
				() => launcher.World.AddFrameEndTask(w => { w.Remove(this); w.ScreenMap.Remove(this); }));

			pos = launcher.CenterPosition;

			if (launcher.World.Map.Sequences.SpritesLoaded)
				launcher.World.ScreenMap.Add(this, pos, doors.Image);
		}

		internal SatelliteLaunch(World world, SnapshotReader r, MiniYaml yaml)
		{
			var nodes = yaml.ToDictionary();

			pos = FieldLoader.GetValue<WPos>(PosKey, nodes[PosKey].Value);
			frame = FieldLoader.GetValue<int>(FrameKey, nodes[FrameKey].Value);

			r.DeferActor(nodes[LauncherKey].Value, a =>
			{
				launcher = a;
				if (a != null)
					info = a.Info.TraitInfoOrDefault<GpsPowerInfo>();
			});

			var state = AnimationCodec.Load(nodes[AnimationKey]);
			doors = new Animation(world, nodes[DoorImageKey].Value);
			doors.ResumeThen(state.Sequence,
				() => world.AddFrameEndTask(w => { w.Remove(this); w.ScreenMap.Remove(this); }), state);

			if (world.Map.Sequences.SpritesLoaded)
				world.ScreenMap.Add(this, pos, doors.Image);
		}

		bool IRequiresRestoredReferences.ReferencesRestored => launcher != null && info != null;

		List<MiniYamlNode> ISaveableEffect.SaveState(World world, SnapshotWriter w)
		{
			var animation = AnimationCodec.Save(doors);
			if (animation == null)
				return null;

			return
			[
				new(LauncherKey, w.ActorRef(launcher)),
				new(PosKey, FieldSaver.FormatValue(pos)),
				new(FrameKey, FieldSaver.FormatValue(frame)),
				new(DoorImageKey, info.DoorImage ?? ""),
				new(AnimationKey, new MiniYaml("", animation))
			];
		}

		public void Tick(World world)
		{
			doors.Tick();

			if (world.Map.Sequences.SpritesLoaded)
				world.ScreenMap.Update(this, pos, doors.Image);

			if (++frame == 19)
			{
				var palette = info.SatellitePaletteIsPlayerPalette ? info.SatellitePalette + launcher.Owner.InternalName : info.SatellitePalette;
				world.AddFrameEndTask(w => w.Add(new GpsSatellite(world, pos, info.SatelliteImage, info.SatelliteSequence, palette, info.RevealDelay, launcher.Owner)));
			}
		}

		public IEnumerable<IRenderable> Render(WorldRenderer wr)
		{
			var palette = info.DoorPaletteIsPlayerPalette ? info.DoorPalette + launcher.Owner.InternalName : info.DoorPalette;
			return doors.Render(pos, wr.Palette(palette));
		}
	}
}
