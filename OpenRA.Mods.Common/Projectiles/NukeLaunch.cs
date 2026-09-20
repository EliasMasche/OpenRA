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
using System.Collections.Immutable;
using OpenRA.Effects;
using OpenRA.GameRules;
using OpenRA.GameSaves;
using OpenRA.Graphics;
using OpenRA.Traits;

namespace OpenRA.Mods.Common.Effects
{
	[SaveableEffect]
	public class NukeLaunch : IProjectile, ISpatiallyPartitionable, ISaveableEffect, IRequiresRestoredReferences
	{
		const string FiredByKey = "FiredBy";
		const string WeaponKey = "Weapon";
		const string ImageKey = "Image";
		const string WeaponPaletteKey = "WeaponPalette";
		const string UpSequenceKey = "UpSequence";
		const string DownSequenceKey = "DownSequence";
		const string AscendSourceKey = "AscendSource";
		const string AscendTargetKey = "AscendTarget";
		const string DescendSourceKey = "DescendSource";
		const string DescendTargetKey = "DescendTarget";
		const string DetonationAltitudeKey = "DetonationAltitude";
		const string RemoveOnDetonationKey = "RemoveOnDetonation";
		const string ImpactDelayKey = "ImpactDelay";
		const string TurnKey = "Turn";
		const string TrailImageKey = "TrailImage";
		const string TrailSequencesKey = "TrailSequences";
		const string TrailPaletteKey = "TrailPalette";
		const string TrailIntervalKey = "TrailInterval";
		const string TrailDelayKey = "TrailDelay";
		const string PosKey = "Pos";
		const string TicksKey = "Ticks";
		const string TrailTicksKey = "TrailTicks";
		const string LaunchDelayKey = "LaunchDelay";
		const string IsLaunchedKey = "IsLaunched";
		const string DetonatedKey = "Detonated";

		readonly Player firedBy;
		readonly Animation anim;
		readonly WeaponInfo weapon;
		readonly string image;
		readonly string weaponPalette;
		readonly string upSequence;
		readonly string downSequence;

		readonly WPos ascendSource;
		readonly WPos ascendTarget;
		readonly WPos descendSource;
		readonly WPos descendTarget;
		readonly WDist detonationAltitude;
		readonly bool removeOnDetonation;
		readonly int impactDelay;
		readonly int turn;
		readonly string trailImage;
		readonly ImmutableArray<string> trailSequences;
		readonly string trailPalette;
		readonly int trailInterval;
		readonly int trailDelay;

		WPos pos;
		int ticks, trailTicks;
		int launchDelay;
		bool isLaunched;
		bool detonated;

		public NukeLaunch(Player firedBy, string image, WeaponInfo weapon, string weaponPalette, string upSequence, string downSequence,
			WPos launchPos, WPos targetPos, WDist detonationAltitude, bool removeOnDetonation, WDist velocity, int launchDelay, int impactDelay,
			bool skipAscent,
			string trailImage, ImmutableArray<string> trailSequences, string trailPalette, bool trailUsePlayerPalette, int trailDelay, int trailInterval)
		{
			this.firedBy = firedBy;
			this.weapon = weapon;
			this.image = image;
			this.weaponPalette = weaponPalette;
			this.upSequence = upSequence;
			this.downSequence = downSequence;
			this.launchDelay = launchDelay;
			this.impactDelay = impactDelay;
			turn = skipAscent ? 0 : impactDelay / 2;
			this.trailImage = trailImage;
			this.trailSequences = trailSequences;
			this.trailPalette = trailPalette;
			if (trailUsePlayerPalette)
				this.trailPalette += firedBy.InternalName;

			this.trailInterval = trailInterval;
			this.trailDelay = trailDelay;
			trailTicks = trailDelay;

			var offset = new WVec(WDist.Zero, WDist.Zero, velocity * (impactDelay - turn));
			ascendSource = launchPos;
			ascendTarget = launchPos + offset;
			descendSource = targetPos + offset;
			descendTarget = targetPos;
			this.detonationAltitude = detonationAltitude;
			this.removeOnDetonation = removeOnDetonation;

			if (!string.IsNullOrEmpty(image))
				anim = new Animation(firedBy.World, image);

			pos = skipAscent ? descendSource : ascendSource;
		}

		internal NukeLaunch(World world, SnapshotReader r, MiniYaml yaml)
		{
			var nodes = yaml.ToDictionary();

			weapon = WeaponRefs.ResolveWeapon(world, nodes[WeaponKey].Value);
			image = nodes[ImageKey].Value;
			weaponPalette = nodes[WeaponPaletteKey].Value;
			upSequence = nodes[UpSequenceKey].Value;
			downSequence = nodes[DownSequenceKey].Value;

			ascendSource = FieldLoader.GetValue<WPos>(AscendSourceKey, nodes[AscendSourceKey].Value);
			ascendTarget = FieldLoader.GetValue<WPos>(AscendTargetKey, nodes[AscendTargetKey].Value);
			descendSource = FieldLoader.GetValue<WPos>(DescendSourceKey, nodes[DescendSourceKey].Value);
			descendTarget = FieldLoader.GetValue<WPos>(DescendTargetKey, nodes[DescendTargetKey].Value);
			detonationAltitude = FieldLoader.GetValue<WDist>(DetonationAltitudeKey, nodes[DetonationAltitudeKey].Value);
			removeOnDetonation = FieldLoader.GetValue<bool>(RemoveOnDetonationKey, nodes[RemoveOnDetonationKey].Value);
			impactDelay = FieldLoader.GetValue<int>(ImpactDelayKey, nodes[ImpactDelayKey].Value);

			turn = FieldLoader.GetValue<int>(TurnKey, nodes[TurnKey].Value);

			trailImage = nodes[TrailImageKey].Value;
			trailPalette = nodes[TrailPaletteKey].Value;
			trailInterval = FieldLoader.GetValue<int>(TrailIntervalKey, nodes[TrailIntervalKey].Value);
			trailDelay = FieldLoader.GetValue<int>(TrailDelayKey, nodes[TrailDelayKey].Value);

			var sequences = nodes[TrailSequencesKey].Value;
			trailSequences = string.IsNullOrEmpty(sequences)
				? []
				: [.. FieldLoader.GetValue<string[]>(TrailSequencesKey, sequences)];

			pos = FieldLoader.GetValue<WPos>(PosKey, nodes[PosKey].Value);
			ticks = FieldLoader.GetValue<int>(TicksKey, nodes[TicksKey].Value);
			trailTicks = FieldLoader.GetValue<int>(TrailTicksKey, nodes[TrailTicksKey].Value);
			launchDelay = FieldLoader.GetValue<int>(LaunchDelayKey, nodes[LaunchDelayKey].Value);
			isLaunched = FieldLoader.GetValue<bool>(IsLaunchedKey, nodes[IsLaunchedKey].Value);
			detonated = FieldLoader.GetValue<bool>(DetonatedKey, nodes[DetonatedKey].Value);

			firedBy = r.ResolvePlayer(nodes[FiredByKey].Value);

			if (!string.IsNullOrEmpty(image))
			{
				anim = new Animation(world, image);

				if (isLaunched)
				{
					anim.PlayRepeating(ticks >= turn ? downSequence : upSequence);

					if (world.Map.Sequences.SpritesLoaded)
						world.ScreenMap.Add(this, pos, anim.Image);
				}
			}
		}

		bool IRequiresRestoredReferences.ReferencesRestored => firedBy != null;

		List<MiniYamlNode> ISaveableEffect.SaveState(World world, SnapshotWriter w)
		{
			var weaponKey = WeaponRefs.WeaponKeyOf(world, weapon);
			if (weaponKey == null)
				return null;

			return
			[
				new(FiredByKey, w.PlayerRef(firedBy)),
				new(WeaponKey, weaponKey),
				new(ImageKey, image ?? ""),
				new(WeaponPaletteKey, weaponPalette ?? ""),
				new(UpSequenceKey, upSequence ?? ""),
				new(DownSequenceKey, downSequence ?? ""),
				new(AscendSourceKey, FieldSaver.FormatValue(ascendSource)),
				new(AscendTargetKey, FieldSaver.FormatValue(ascendTarget)),
				new(DescendSourceKey, FieldSaver.FormatValue(descendSource)),
				new(DescendTargetKey, FieldSaver.FormatValue(descendTarget)),
				new(DetonationAltitudeKey, FieldSaver.FormatValue(detonationAltitude)),
				new(RemoveOnDetonationKey, FieldSaver.FormatValue(removeOnDetonation)),
				new(ImpactDelayKey, FieldSaver.FormatValue(impactDelay)),
				new(TurnKey, FieldSaver.FormatValue(turn)),
				new(TrailImageKey, trailImage ?? ""),
				new(TrailSequencesKey, trailSequences.JoinWith(",")),
				new(TrailPaletteKey, trailPalette ?? ""),
				new(TrailIntervalKey, FieldSaver.FormatValue(trailInterval)),
				new(TrailDelayKey, FieldSaver.FormatValue(trailDelay)),
				new(PosKey, FieldSaver.FormatValue(pos)),
				new(TicksKey, FieldSaver.FormatValue(ticks)),
				new(TrailTicksKey, FieldSaver.FormatValue(trailTicks)),
				new(LaunchDelayKey, FieldSaver.FormatValue(launchDelay)),
				new(IsLaunchedKey, FieldSaver.FormatValue(isLaunched)),
				new(DetonatedKey, FieldSaver.FormatValue(detonated))
			];
		}

		public void Tick(World world)
		{
			if (launchDelay-- > 0)
				return;

			if (!isLaunched)
			{
				if (weapon.Report != null && weapon.Report.Length > 0)
					Game.Sound.Play(SoundType.World, weapon.Report, world, pos);

				if (anim != null)
				{
					anim.PlayRepeating(upSequence);

					if (world.Map.Sequences.SpritesLoaded)
						world.ScreenMap.Add(this, pos, anim.Image);
				}

				isLaunched = true;
			}

			if (anim != null)
			{
				anim.Tick();

				if (ticks == turn)
					anim.PlayRepeating(downSequence);
			}

			var isDescending = ticks >= turn;
			if (!isDescending)
				pos = WPos.LerpQuadratic(ascendSource, ascendTarget, WAngle.Zero, ticks, turn);
			else
				pos = WPos.LerpQuadratic(descendSource, descendTarget, WAngle.Zero, ticks - turn, impactDelay - turn);

			if (!string.IsNullOrEmpty(trailImage) && --trailTicks < 0)
			{
				var trailPos = !isDescending ? WPos.LerpQuadratic(ascendSource, ascendTarget, WAngle.Zero, ticks - trailDelay, turn)
					: WPos.LerpQuadratic(descendSource, descendTarget, WAngle.Zero, ticks - turn - trailDelay, impactDelay - turn);

				world.AddFrameEndTask(w => w.Add(new SpriteEffect(trailPos, w, trailImage, trailSequences.Random(world.SharedRandom),
					trailPalette)));

				trailTicks = trailInterval;
			}

			var dat = world.Map.DistanceAboveTerrain(pos);
			if (ticks == impactDelay || (isDescending && dat <= detonationAltitude))
				Explode(world, ticks == impactDelay || removeOnDetonation);

			if (anim != null && world.Map.Sequences.SpritesLoaded)
				world.ScreenMap.Update(this, pos, anim.Image);

			ticks++;
		}

		void Explode(World world, bool removeProjectile)
		{
			if (removeProjectile)
				world.AddFrameEndTask(w => { w.Remove(this); w.ScreenMap.Remove(this); });

			if (detonated)
				return;

			var target = Target.FromPos(pos);
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

			detonated = true;
		}

		public IEnumerable<IRenderable> Render(WorldRenderer wr)
		{
			if (!isLaunched || anim == null)
				return [];

			return anim.Render(pos, wr.Palette(weaponPalette));
		}

		public float FractionComplete => ticks * 1f / impactDelay;
	}
}
