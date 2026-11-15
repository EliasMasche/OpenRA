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
	public class IonCannon : IProjectile, IResumeAnimations
	{
		// Keeps the arguments of the constructor, so that a loader can run it again on the saved fields. That builds
		// the animation, of which a save holds only the frame.
		readonly World world;
		readonly WPos launchPos;
		readonly string effect;
		readonly string sequence;
		readonly Target target;
		readonly Animation anim;
		readonly Player firedBy;
		readonly string palette;
		readonly WeaponInfo weapon;

		int weaponDelay;
		bool impacted = false;

		public IonCannon(Player firedBy, WeaponInfo weapon, World world, WPos launchPos, in Target target,
			string effect, string sequence, string palette, int weaponDelay)
		{
			this.target = target;
			this.firedBy = firedBy;
			this.weapon = weapon;
			this.palette = palette;
			this.weaponDelay = weaponDelay;
			this.world = world;
			this.launchPos = launchPos;
			this.effect = effect;
			this.sequence = sequence;
			anim = new Animation(this.world, this.effect);
			anim.PlayThen(this.sequence, () => Finish(this.world));

			if (weapon.Report != null && weapon.Report.Length > 0)
				Game.Sound.Play(SoundType.World, weapon.Report, this.world, this.launchPos);
		}

		public void Tick(World world)
		{
			anim.Tick();

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
			return anim.Render(target.CenterPosition, wr.Palette(palette));
		}

		void Finish(World world)
		{
			world.AddFrameEndTask(w => w.Remove(this));
		}

		void IResumeAnimations.ResumeAnimations(Actor self)
		{
			anim.ContinueThen(() => Finish(firedBy.World));
		}
	}
}
