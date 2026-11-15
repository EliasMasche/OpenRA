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

using OpenRA.Graphics;
using OpenRA.Traits;

namespace OpenRA.Mods.Common.Traits.Render
{
	[Desc("Displays a custom UI overlay relative to the actor's mouseover bounds.")]
	public class WithBuildingRepairDecorationInfo : WithDecorationInfo, Requires<RepairableBuildingInfo>
	{
		public override object Create(ActorInitializer init) { return new WithBuildingRepairDecoration(init.Self, this); }
	}

	public class WithBuildingRepairDecoration : WithDecoration, IResumeAnimations
	{
		readonly RepairableBuilding rb;
		readonly WithBuildingRepairDecorationInfo info;
		int shownPlayer = 0;

		public WithBuildingRepairDecoration(Actor self, WithBuildingRepairDecorationInfo info)
			: base(self, info)
		{
			this.info = info;
			rb = self.Trait<RepairableBuilding>();
			anim = new Animation(self.World, info.Image,
				() => !rb.RepairActive || rb.IsTraitDisabled || !ShouldRender(self));
			anim.PlayThen(info.Sequence, CycleRepairer);
			CycleRepairer();
		}

		protected override bool ShouldRender(Actor self)
		{
			if (rb.Repairers.Count == 0)
				return false;

			return base.ShouldRender(self);
		}

		protected override PaletteReference GetPalette(Actor self, WorldRenderer wr)
		{
			return wr.Palette(info.IsPlayerPalette ? info.Palette + rb.Repairers[shownPlayer % rb.Repairers.Count].InternalName : info.Palette);
		}

		void CycleRepairer()
		{
			anim.PlayThen(info.Sequence, CycleRepairer);

			if (++shownPlayer == rb.Repairers.Count)
				shownPlayer = 0;
		}

		// The save holds the repairer on display, and not the callback that plays the animation again. Without this,
		// the decoration stops at its last frame after a load, and never shows the next repairer.
		void IResumeAnimations.ResumeAnimations(Actor self)
		{
			anim.ContinueThen(CycleRepairer);
		}
	}
}
