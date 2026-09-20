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
using OpenRA.Mods.Cnc.Graphics;
using OpenRA.Mods.Cnc.Traits;
using OpenRA.Primitives;

namespace OpenRA.Mods.Cnc.Effects
{
	[SaveableEffect]
	sealed class ConyardChronoVortex : IEffect, ISpatiallyPartitionable, ISaveableEffect, IRequiresRestoredReferences
	{
		const string LauncherKey = "Launcher";
		const string CenterKey = "Center";
		const string PosKey = "Pos";
		const string AngleKey = "Angle";
		const string LoopsKey = "Loops";
		const string FrameKey = "Frame";

		static readonly Size Size = new(64, 64);
		static readonly WVec Offset = new(171, 0, 0);
		readonly ChronoVortexRenderer renderer;
		readonly WPos center;

		Actor launcher;
		WPos pos;
		WAngle angle;
		int loops = 3;
		int frame;

		public ConyardChronoVortex(Actor launcher)
		{
			this.launcher = launcher;
			renderer = launcher.World.WorldActor.Trait<ChronoVortexRenderer>();
			center = launcher.CenterPosition;
			pos = center + Offset.Rotate(WRot.FromYaw(angle));
			launcher.World.ScreenMap.Add(this, pos, Size);
		}

		internal ConyardChronoVortex(World world, SnapshotReader r, MiniYaml yaml)
		{
			var nodes = yaml.ToDictionary();

			renderer = world.WorldActor.Trait<ChronoVortexRenderer>();
			center = FieldLoader.GetValue<WPos>(CenterKey, nodes[CenterKey].Value);
			pos = FieldLoader.GetValue<WPos>(PosKey, nodes[PosKey].Value);
			angle = FieldLoader.GetValue<WAngle>(AngleKey, nodes[AngleKey].Value);
			loops = FieldLoader.GetValue<int>(LoopsKey, nodes[LoopsKey].Value);
			frame = FieldLoader.GetValue<int>(FrameKey, nodes[FrameKey].Value);

			r.DeferActor(nodes[LauncherKey].Value, a => launcher = a);

			world.ScreenMap.Add(this, pos, Size);
		}

		bool IRequiresRestoredReferences.ReferencesRestored => launcher != null;

		List<MiniYamlNode> ISaveableEffect.SaveState(World world, SnapshotWriter w)
		{
			return
			[
				new(LauncherKey, w.ActorRef(launcher)),
				new(CenterKey, FieldSaver.FormatValue(center)),
				new(PosKey, FieldSaver.FormatValue(pos)),
				new(AngleKey, FieldSaver.FormatValue(angle)),
				new(LoopsKey, FieldSaver.FormatValue(loops)),
				new(FrameKey, FieldSaver.FormatValue(frame))
			];
		}

		public void Tick(World world)
		{
			// First 16 frames are the vortex opening
			// Next 16 frames are loopable
			// Final 16 frames are the vortex closing
			if (++frame == 32 && --loops > 0)
				frame = 16;

			angle += new WAngle(42);
			pos = center + Offset.Rotate(WRot.FromYaw(angle));
			world.ScreenMap.Update(this, pos, Size);
			if (frame == 48)
			{
				world.AddFrameEndTask(w =>
				{
					w.Remove(this);
					w.ScreenMap.Remove(this);
					launcher.TraitOrDefault<ConyardChronoReturn>()?.VortexCompleted();
				});
			}
		}

		public IEnumerable<IRenderable> Render(WorldRenderer wr)
		{
			yield return new ChronoVortexRenderable(renderer, pos, frame);
		}
	}
}
