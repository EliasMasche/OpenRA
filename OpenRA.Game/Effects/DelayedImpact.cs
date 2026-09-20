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
using OpenRA.GameRules;
using OpenRA.GameSaves;
using OpenRA.Graphics;
using OpenRA.Traits;

namespace OpenRA.Effects
{
	[SaveableEffect]
	public class DelayedImpact : IEffect, ISaveableEffect
	{
		const string DelayKey = "Delay";
		const string WarheadKey = "Warhead";
		const string TargetKey = "Target";
		const string ArgsKey = "Args";

		Target target;
		readonly IWarhead wh;
		readonly WarheadArgs args;

		int delay;

		public DelayedImpact(int delay, IWarhead wh, Target target, WarheadArgs args)
		{
			this.wh = wh;
			this.delay = delay;
			this.target = target;
			this.args = args;
		}

		internal DelayedImpact(World world, SnapshotReader r, MiniYaml yaml)
		{
			var nodes = yaml.ToDictionary();

			delay = FieldLoader.GetValue<int>(DelayKey, nodes[DelayKey].Value);
			wh = WeaponRefs.ResolveWarhead(world, nodes[WarheadKey].Value);
			args = WarheadArgsCodec.Load(nodes[ArgsKey], world, r);

			target = Target.Invalid;
			r.DeferTarget(nodes[TargetKey].Value, t => target = t);
		}

		List<MiniYamlNode> ISaveableEffect.SaveState(World world, SnapshotWriter w)
		{
			var warhead = WeaponRefs.WarheadRef(world, args?.Weapon, wh);
			if (warhead == null)
				return null;

			var argNodes = WarheadArgsCodec.Save(args, world, w);
			if (argNodes == null)
				return null;

			return
			[
				new(DelayKey, FieldSaver.FormatValue(delay)),
				new(WarheadKey, warhead),
				new(TargetKey, w.TargetRef(target)),
				new(ArgsKey, new MiniYaml("", argNodes))
			];
		}

		public bool IsArmed => wh != null && args != null && args.SourceOwner != null;

		public void Tick(World world)
		{
			if (--delay > 0)
				return;

			world.AddFrameEndTask(w =>
			{
				w.Remove(this);

				if (IsArmed)
					wh.DoImpact(target, args);
			});
		}

		public IEnumerable<IRenderable> Render(WorldRenderer wr) { yield break; }
	}
}
