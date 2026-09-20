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
using System.Collections.Generic;
using OpenRA.Activities;
using OpenRA.GameSaves;
using OpenRA.Mods.Common.Traits;
using OpenRA.Traits;

namespace OpenRA.Mods.Common.Activities
{
	[SaveableActivity]
	public class FallToEarth : Activity
	{
		readonly Aircraft aircraft;
		readonly FallsToEarthInfo info;

		readonly int acceleration;
		int spin;

		public FallToEarth(Actor self, FallsToEarthInfo info)
		{
			this.info = info;
			IsInterruptible = false;
			aircraft = self.Trait<Aircraft>();
			if (!info.MaximumSpinSpeed.HasValue || info.MaximumSpinSpeed.Value != WAngle.Zero)
				acceleration = self.World.SharedRandom.Next(2) * 2 - 1;
		}

		internal FallToEarth(Actor self, SnapshotReader _, MiniYaml yaml)
		{
			IsInterruptible = false;
			aircraft = self.Trait<Aircraft>();
			info = self.Info.TraitInfoOrDefault<FallsToEarthInfo>();

			var n = yaml.ToDictionary();
			spin = FieldLoader.GetValue<int>("Spin", n["Spin"].Value);

			acceleration = FieldLoader.GetValue<int>("Acceleration", n["Acceleration"].Value);
		}

		public override List<MiniYamlNode> SaveState(Actor self, SnapshotWriter w)
		{
			return
			[
				new("Spin", FieldSaver.FormatValue(spin)),
				new("Acceleration", FieldSaver.FormatValue(acceleration))
			];
		}

		public override bool Tick(Actor self)
		{
			if (self.World.Map.DistanceAboveTerrain(self.CenterPosition).Length <= 0)
			{
				// Use .FromPos since this actor is killed. Cannot use Target.FromActor
				info.ExplosionWeapon?.Impact(Target.FromPos(self.CenterPosition), self);

				self.Kill(self);
				Cancel(self);
				return true;
			}

			if (acceleration != 0)
			{
				if (!info.MaximumSpinSpeed.HasValue || Math.Abs(spin) < info.MaximumSpinSpeed.Value.Angle)
					spin += 4 * acceleration; // TODO: Possibly unhardcode this

				// Allow for negative spin values and convert from facing to angle units
				aircraft.Facing = new WAngle(aircraft.Facing.Angle + spin);
			}

			var move = info.Moves ? aircraft.FlyStep(aircraft.Facing) : WVec.Zero;
			move -= new WVec(WDist.Zero, WDist.Zero, info.Velocity);
			aircraft.SetPosition(self, aircraft.CenterPosition + move);

			return false;
		}
	}
}
