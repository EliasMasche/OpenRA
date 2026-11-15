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
using OpenRA.Graphics;
using OpenRA.Traits;

namespace OpenRA.Mods.Cnc.Effects
{
	public class DropPodImpact : IProjectile, IResumeAnimations
	{
		// Keeps the arguments of the constructor, so that a loader can run it again on the saved fields. That builds
		// the animation, of which a save holds only the frame.
		readonly World world;
		readonly string entryEffect;
		readonly string entrySequence;
		readonly Target target;
		readonly Animation entryAnimation;
		readonly Player firedBy;
		readonly string entryPalette;
		readonly WeaponInfo weapon;
		readonly WPos launchPos;

		int weaponDelay;
		bool impacted = false;

		public DropPodImpact(Player firedBy, WeaponInfo weapon, World world, WPos launchPos, in Target target,
			int weaponDelay, string entryEffect, string entrySequence, string entryPalette)
		{
			this.target = target;
			this.firedBy = firedBy;
			this.weapon = weapon;
			this.entryPalette = entryPalette;
			this.weaponDelay = weaponDelay;
			this.world = world;
			this.launchPos = launchPos;
			this.entryEffect = entryEffect;
			this.entrySequence = entrySequence;

			entryAnimation = new Animation(this.world, this.entryEffect);
			entryAnimation.PlayThen(this.entrySequence, () => Finish(this.world));

			if (weapon.Report != null && weapon.Report.Length > 0)
				Game.Sound.Play(SoundType.World, weapon.Report, this.world, this.launchPos);
		}

		public void Tick(World world)
		{
			entryAnimation.Tick();

			if (!impacted && weaponDelay-- <= 0)
			{
				var warheadArgs = new WarheadArgs
				{
					Weapon = weapon,
					Source = target.CenterPosition,
					SourceActor = firedBy.PlayerActor,
					WeaponTarget = target
				};

				weapon.Impact(target, warheadArgs);
				impacted = true;
			}
		}

		public IEnumerable<IRenderable> Render(WorldRenderer wr)
		{
			return entryAnimation.Render(launchPos, wr.Palette(entryPalette));
		}

		void Finish(World world)
		{
			world.AddFrameEndTask(w => w.Remove(this));
		}

		void IResumeAnimations.ResumeAnimations(Actor self)
		{
			entryAnimation.ContinueThen(() => Finish(firedBy.World));
		}
	}
}
