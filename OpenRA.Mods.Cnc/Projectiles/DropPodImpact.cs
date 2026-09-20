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
	public class DropPodImpact : IProjectile, ISaveableEffect, IRequiresRestoredReferences
	{
		const string FiredByKey = "FiredBy";
		const string WeaponKey = "Weapon";
		const string TargetKey = "Target";
		const string LaunchPosKey = "LaunchPos";
		const string EntryEffectKey = "EntryEffect";
		const string EntryPaletteKey = "EntryPalette";
		const string AnimationKey = "Animation";
		const string WeaponDelayKey = "WeaponDelay";
		const string ImpactedKey = "Impacted";

		Target target;
		readonly Animation entryAnimation;
		readonly Player firedBy;
		readonly string entryEffect;
		readonly string entryPalette;
		readonly WeaponInfo weapon;
		readonly WPos launchPos;

		int weaponDelay;
		bool impacted = false;

		public DropPodImpact(Player firedBy, WeaponInfo weapon, World world, WPos launchPos, in Target target,
			int delay, string entryEffect, string entrySequence, string entryPalette)
		{
			this.target = target;
			this.firedBy = firedBy;
			this.weapon = weapon;
			this.entryEffect = entryEffect;
			this.entryPalette = entryPalette;
			weaponDelay = delay;
			this.launchPos = launchPos;

			entryAnimation = new Animation(world, entryEffect);
			entryAnimation.PlayThen(entrySequence, () => Finish(world));

			if (weapon.Report != null && weapon.Report.Length > 0)
				Game.Sound.Play(SoundType.World, weapon.Report, world, launchPos);
		}

		internal DropPodImpact(World world, SnapshotReader r, MiniYaml yaml)
		{
			var nodes = yaml.ToDictionary();

			weapon = WeaponRefs.ResolveWeapon(world, nodes[WeaponKey].Value);
			entryEffect = nodes[EntryEffectKey].Value;
			entryPalette = nodes[EntryPaletteKey].Value;
			launchPos = FieldLoader.GetValue<WPos>(LaunchPosKey, nodes[LaunchPosKey].Value);
			weaponDelay = FieldLoader.GetValue<int>(WeaponDelayKey, nodes[WeaponDelayKey].Value);
			impacted = FieldLoader.GetValue<bool>(ImpactedKey, nodes[ImpactedKey].Value);
			firedBy = r.ResolvePlayer(nodes[FiredByKey].Value);

			target = Target.Invalid;
			r.DeferTarget(nodes[TargetKey].Value, t => target = t);

			var state = AnimationCodec.Load(nodes[AnimationKey]);
			entryAnimation = new Animation(world, entryEffect);
			entryAnimation.ResumeThen(state.Sequence, () => Finish(world), state);
		}

		bool IRequiresRestoredReferences.ReferencesRestored => firedBy != null;

		List<MiniYamlNode> ISaveableEffect.SaveState(World world, SnapshotWriter w)
		{
			var weaponKey = WeaponRefs.WeaponKeyOf(world, weapon);
			var animation = AnimationCodec.Save(entryAnimation);
			if (weaponKey == null || animation == null)
				return null;

			return
			[
				new(FiredByKey, w.PlayerRef(firedBy)),
				new(WeaponKey, weaponKey),
				new(TargetKey, w.TargetRef(target)),
				new(LaunchPosKey, FieldSaver.FormatValue(launchPos)),
				new(EntryEffectKey, entryEffect ?? ""),
				new(EntryPaletteKey, entryPalette ?? ""),
				new(AnimationKey, new MiniYaml("", animation)),
				new(WeaponDelayKey, FieldSaver.FormatValue(weaponDelay)),
				new(ImpactedKey, FieldSaver.FormatValue(impacted))
			];
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
			return entryAnimation.Render(launchPos, wr.Palette(entryPalette));
		}

		void Finish(World world)
		{
			world.AddFrameEndTask(w => w.Remove(this));
		}
	}
}
