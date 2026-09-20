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

namespace OpenRA.Mods.Cnc.Effects
{
	[SaveableEffect]
	public class IonCannon : IProjectile, ISaveableEffect, IRequiresRestoredReferences
	{
		const string FiredByKey = "FiredBy";
		const string WeaponKey = "Weapon";
		const string TargetKey = "Target";
		const string EffectKey = "Effect";
		const string PaletteKey = "Palette";
		const string AnimationKey = "Animation";
		const string WeaponDelayKey = "WeaponDelay";
		const string ImpactedKey = "Impacted";

		Target target;
		readonly Animation anim;
		readonly Player firedBy;
		readonly string effect;
		readonly string palette;
		readonly WeaponInfo weapon;

		int weaponDelay;
		bool impacted = false;

		public IonCannon(Player firedBy, WeaponInfo weapon, World world, WPos launchPos, in Target target, string effect, string sequence, string palette, int delay)
		{
			this.target = target;
			this.firedBy = firedBy;
			this.weapon = weapon;
			this.effect = effect;
			this.palette = palette;
			weaponDelay = delay;
			anim = new Animation(world, effect);
			anim.PlayThen(sequence, () => Finish(world));

			if (weapon.Report != null && weapon.Report.Length > 0)
				Game.Sound.Play(SoundType.World, weapon.Report, world, launchPos);
		}

		internal IonCannon(World world, SnapshotReader r, MiniYaml yaml)
		{
			var nodes = yaml.ToDictionary();

			weapon = WeaponRefs.ResolveWeapon(world, nodes[WeaponKey].Value);
			effect = nodes[EffectKey].Value;
			palette = nodes[PaletteKey].Value;
			weaponDelay = FieldLoader.GetValue<int>(WeaponDelayKey, nodes[WeaponDelayKey].Value);
			impacted = FieldLoader.GetValue<bool>(ImpactedKey, nodes[ImpactedKey].Value);
			firedBy = r.ResolvePlayer(nodes[FiredByKey].Value);

			target = Target.Invalid;
			r.DeferTarget(nodes[TargetKey].Value, t => target = t);

			var state = AnimationCodec.Load(nodes[AnimationKey]);
			anim = new Animation(world, effect);
			anim.ResumeThen(state.Sequence, () => Finish(world), state);
		}

		bool IRequiresRestoredReferences.ReferencesRestored => firedBy != null;

		List<MiniYamlNode> ISaveableEffect.SaveState(World world, SnapshotWriter w)
		{
			var weaponKey = WeaponRefs.WeaponKeyOf(world, weapon);
			var animation = AnimationCodec.Save(anim);
			if (weaponKey == null || animation == null)
				return null;

			return
			[
				new(FiredByKey, w.PlayerRef(firedBy)),
				new(WeaponKey, weaponKey),
				new(TargetKey, w.TargetRef(target)),
				new(EffectKey, effect ?? ""),
				new(PaletteKey, palette ?? ""),
				new(AnimationKey, new MiniYaml("", animation)),
				new(WeaponDelayKey, FieldSaver.FormatValue(weaponDelay)),
				new(ImpactedKey, FieldSaver.FormatValue(impacted))
			];
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
					World = world,

					SourceOwner = firedBy,
					SourceActor = firedBy?.PlayerActor,
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
	}
}
