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
using System.Globalization;
using System.Linq;
using OpenRA.GameRules;
using OpenRA.Traits;

namespace OpenRA.GameSaves
{
	public static class ProjectileArgsCodec
	{
		public const string WeaponKey = "Weapon";
		public const string FacingKey = "Facing";
		public const string SourceKey = "Source";
		public const string PassiveTargetKey = "PassiveTarget";
		public const string DamageModifiersKey = "DamageModifiers";
		public const string InaccuracyModifiersKey = "InaccuracyModifiers";
		public const string RangeModifiersKey = "RangeModifiers";
		public const string SourceActorKey = "SourceActor";
		public const string SourceOwnerKey = "SourceOwner";
		public const string GuidedTargetKey = "GuidedTarget";
		public const string MuzzleSourceKey = "MuzzleSource";
		public const string MuzzleTraitKey = "Trait";
		public const string MuzzleBarrelKey = "Barrel";

		public static List<MiniYamlNode> Save(ProjectileArgs args, World world, SnapshotWriter w)
		{
			ArgumentNullException.ThrowIfNull(args);
			ArgumentNullException.ThrowIfNull(world);
			ArgumentNullException.ThrowIfNull(w);

			var weapon = WeaponRefs.WeaponKeyOf(world, args.Weapon);
			if (weapon == null)
				return null;

			var nodes = new List<MiniYamlNode>
			{
				new(WeaponKey, weapon),
				new(FacingKey, FieldSaver.FormatValue(args.Facing)),
				new(SourceKey, FieldSaver.FormatValue(args.Source)),
				new(PassiveTargetKey, FieldSaver.FormatValue(args.PassiveTarget)),
				new(DamageModifiersKey, FormatModifiers(args.DamageModifiers)),
				new(InaccuracyModifiersKey, FormatModifiers(args.InaccuracyModifiers)),
				new(RangeModifiersKey, FormatModifiers(args.RangeModifiers)),
				new(SourceActorKey, w.ActorRef(args.SourceActor)),
				new(SourceOwnerKey, w.PlayerRef(args.SourceOwner)),
				new(GuidedTargetKey, w.TargetRef(args.GuidedTarget))
			};

			var barrel = args.SourceProvider?.SaveBarrel() ?? -1;
			var owner = args.SourceProvider?.SaveOwner();
			if (barrel >= 0 && owner != null)
			{
				nodes.Add(new MiniYamlNode(MuzzleSourceKey, new MiniYaml("",
				[
					new MiniYamlNode(MuzzleTraitKey, owner.InstanceName ?? ""),
					new MiniYamlNode(MuzzleBarrelKey, barrel.ToStringInvariant())
				])));
			}

			return nodes;
		}

		public static ProjectileArgs Load(MiniYaml yaml, World world, SnapshotReader r)
		{
			ArgumentNullException.ThrowIfNull(yaml);
			ArgumentNullException.ThrowIfNull(world);
			ArgumentNullException.ThrowIfNull(r);

			var nodes = yaml.ToDictionary();

			var weapon = WeaponRefs.ResolveWeapon(world, Value(nodes, WeaponKey));

			var args = new ProjectileArgs
			{
				Weapon = weapon,
				Facing = Field<WAngle>(nodes, FacingKey),
				Source = Field<WPos>(nodes, SourceKey),
				PassiveTarget = Field<WPos>(nodes, PassiveTargetKey),
				DamageModifiers = ParseModifiers(nodes, DamageModifiersKey),
				InaccuracyModifiers = ParseModifiers(nodes, InaccuracyModifiersKey),
				RangeModifiers = ParseModifiers(nodes, RangeModifiersKey),
				GuidedTarget = Target.Invalid,
				World = world,
				SourceOwner = r.ResolvePlayer(Value(nodes, SourceOwnerKey))
			};

			args.SourceProvider = new FrozenProjectileSource(args.Source, args.Facing);
			args.CurrentSource = () => args.SourceProvider.CurrentSource();
			args.CurrentMuzzleFacing = () => args.SourceProvider.CurrentMuzzleFacing();

			var muzzle = nodes.TryGetValue(MuzzleSourceKey, out var m) ? m.ToDictionary() : null;
			r.DeferActor(Value(nodes, SourceActorKey), a =>
			{
				args.SourceActor = a;

				if (muzzle != null)
					RestoreMuzzle(args, a, muzzle);
			});

			r.DeferTarget(Value(nodes, GuidedTargetKey), t => args.GuidedTarget = t);

			return args;
		}

		static void RestoreMuzzle(ProjectileArgs args, Actor self, Dictionary<string, MiniYaml> muzzle)
		{
			if (self == null || self.Disposed)
				return;

			var instanceName = Value(muzzle, MuzzleTraitKey) ?? "";
			var owner = self.TraitsImplementing<IProvidesProjectileSource>()
				.FirstOrDefault(t => (t.InstanceName ?? "") == instanceName);

			if (owner == null)
				return;

			var barrelValue = Value(muzzle, MuzzleBarrelKey);
			if (!int.TryParse(barrelValue, NumberStyles.None, NumberFormatInfo.InvariantInfo, out var barrel))
				return;

			var source = owner.ProvideProjectileSource(self, barrel);
			if (source != null)
				args.SourceProvider = source;
		}

		static string FormatModifiers(int[] modifiers)
		{
			if (modifiers == null || modifiers.Length == 0)
				return "";

			return modifiers.Select(m => m.ToStringInvariant()).JoinWith(",");
		}

		static int[] ParseModifiers(Dictionary<string, MiniYaml> nodes, string key)
		{
			var value = Value(nodes, key);
			if (string.IsNullOrEmpty(value))
				return [];

			return FieldLoader.GetValue<int[]>(key, value);
		}

		static string Value(Dictionary<string, MiniYaml> nodes, string key)
		{
			return nodes.TryGetValue(key, out var node) ? node.Value : null;
		}

		static T Field<T>(Dictionary<string, MiniYaml> nodes, string key)
		{
			var value = Value(nodes, key);
			if (string.IsNullOrEmpty(value))
				return default;

			return FieldLoader.GetValue<T>(key, value);
		}
	}
}
