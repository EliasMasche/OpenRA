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
using System.Numerics;
using OpenRA.GameRules;
using OpenRA.GameSaves;
using OpenRA.Graphics;
using OpenRA.Primitives;
using OpenRA.Scripting;
using OpenRA.Traits;

namespace OpenRA.Mods.Common.Projectiles
{
	[Desc("Projectile with customisable acceleration vector.")]
	public class GravityBombInfo : IProjectileInfo
	{
		public readonly string Image = null;

		[SequenceReference(nameof(Image), allowNullImage: true)]
		[Desc("Loop a randomly chosen sequence of Image from this list while falling.")]
		public readonly ImmutableArray<string> Sequences = ["idle"];

		[SequenceReference(nameof(Image), allowNullImage: true)]
		[Desc("Sequence to play when launched. Skipped if null or empty.")]
		public readonly string OpenSequence = null;

		[PaletteReference]
		[Desc("The palette used to draw this projectile.")]
		public readonly string Palette = "effect";

		[Desc("Palette is a player palette BaseName")]
		public readonly bool IsPlayerPalette = false;

		[Desc("Does this projectile have a shadow?")]
		public readonly bool Shadow = false;

		[Desc("Color to draw shadow if Shadow is true.")]
		public readonly Color ShadowColor = Color.FromArgb(140, 0, 0, 0);

		[Desc("Projectile movement vector per tick (forward, right, up), use negative values for opposite directions.")]
		public readonly WVec Velocity = WVec.Zero;

		[Desc("Value added to Velocity every tick.")]
		public readonly WVec Acceleration = new(0, 0, -15);

		public IProjectile Create(ProjectileArgs args) { return new GravityBomb(this, args); }
	}

	[SaveableEffect]
	public class GravityBomb : IProjectile, ISync, ISaveableEffect, IProjectileScriptInfo
	{
		const string ArgsKey = "Args";
		const string PosKey = "Pos";
		const string LastPosKey = "LastPos";
		const string VelocityKey = "Velocity";

		readonly GravityBombInfo info;
		readonly Animation anim;
		readonly ProjectileArgs args;
		readonly WVec acceleration;

		readonly Vector3 shadowColor;
		readonly float shadowAlpha;

		WVec velocity;

		[VerifySync]
		WPos pos, lastPos;

		public GravityBomb(GravityBombInfo info, ProjectileArgs args)
		{
			this.info = info;
			this.args = args;
			pos = args.Source;
			var convertedVelocity = new WVec(info.Velocity.Y, -info.Velocity.X, info.Velocity.Z);
			velocity = convertedVelocity.Rotate(WRot.FromYaw(args.Facing));
			acceleration = new WVec(info.Acceleration.Y, -info.Acceleration.X, info.Acceleration.Z);

			if (!string.IsNullOrEmpty(info.Image))
			{
				anim = new Animation(args.SourceActor.World, info.Image, () => args.Facing);

				if (!string.IsNullOrEmpty(info.OpenSequence))
					anim.PlayThen(info.OpenSequence, () => anim.PlayRepeating(info.Sequences.Random(args.SourceActor.World.SharedRandom)));
				else
					anim.PlayRepeating(info.Sequences.Random(args.SourceActor.World.SharedRandom));
			}

			var sColor = info.ShadowColor.ToVector4();
			shadowColor = sColor.AsVector3();
			shadowAlpha = sColor.W;
		}

		internal GravityBomb(World world, SnapshotReader r, MiniYaml yaml)
		{
			var nodes = yaml.ToDictionary();

			args = ProjectileArgsCodec.Load(nodes[ArgsKey], world, r);

			info = (GravityBombInfo)args.Weapon.Projectile;

			acceleration = new WVec(info.Acceleration.Y, -info.Acceleration.X, info.Acceleration.Z);

			pos = FieldLoader.GetValue<WPos>(PosKey, nodes[PosKey].Value);
			lastPos = FieldLoader.GetValue<WPos>(LastPosKey, nodes[LastPosKey].Value);
			velocity = FieldLoader.GetValue<WVec>(VelocityKey, nodes[VelocityKey].Value);

			if (!string.IsNullOrEmpty(info.Image))
			{
				anim = new Animation(world, info.Image, () => args.Facing);

				anim.PlayRepeating(info.Sequences[0]);
			}

			var restoredColor = info.ShadowColor.ToVector4();
			shadowColor = restoredColor.AsVector3();
			shadowAlpha = restoredColor.W;
		}

		List<MiniYamlNode> ISaveableEffect.SaveState(World world, SnapshotWriter w)
		{
			var argNodes = ProjectileArgsCodec.Save(args, world, w);
			if (argNodes == null)
				return null;

			return
			[
				new(ArgsKey, new MiniYaml("", argNodes)),
				new(PosKey, FieldSaver.FormatValue(pos)),
				new(LastPosKey, FieldSaver.FormatValue(lastPos)),
				new(VelocityKey, FieldSaver.FormatValue(velocity))
			];
		}

		public void Tick(World world)
		{
			lastPos = pos;
			pos += velocity;
			velocity += acceleration;

			if (pos.Z <= args.PassiveTarget.Z)
			{
				pos += new WVec(0, 0, args.PassiveTarget.Z - pos.Z);
				world.AddFrameEndTask(w => w.Remove(this));

				var warheadArgs = new WarheadArgs(args)
				{
					ImpactOrientation = new WRot(WAngle.Zero, Util.GetVerticalAngle(lastPos, pos), args.Facing),
					ImpactPosition = pos,
				};

				args.Weapon.Impact(Target.FromPos(pos), warheadArgs);
			}

			anim?.Tick();
		}

		public IEnumerable<IRenderable> Render(WorldRenderer wr)
		{
			if (anim == null)
				yield break;

			var world = args.World;
			if (!world.FogObscures(pos))
			{
				var paletteName = info.Palette;
				if (paletteName != null && info.IsPlayerPalette)
					paletteName += args.SourceOwner.InternalName;

				var palette = wr.Palette(paletteName);

				if (info.Shadow)
				{
					var dat = world.Map.DistanceAboveTerrain(pos);
					var shadowPos = pos - new WVec(0, 0, dat.Length);
					foreach (var r in anim.Render(shadowPos, palette))
						yield return ((IModifyableRenderable)r)
							.WithTint(shadowColor, ((IModifyableRenderable)r).TintModifiers | TintModifiers.ReplaceColor)
							.WithAlpha(shadowAlpha);
				}

				foreach (var r in anim.Render(pos, palette))
					yield return r;
			}
		}

		WPos IProjectileScriptInfo.Position => pos;
		WPos IProjectileScriptInfo.TargetPosition => args.PassiveTarget;
		ScriptProjectileInterface IProjectileScriptInfo.LuaInterface { get; set; }
		Actor IProjectileScriptInfo.SourceActor => args.SourceActor;
		WeaponInfo IProjectileScriptInfo.Weapon => args.Weapon;
	}
}
