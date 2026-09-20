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
using System.Linq;
using OpenRA.GameRules;
using OpenRA.Traits;

namespace OpenRA.GameSaves
{
	/// <summary>Converts a <see cref="WarheadArgs"/> to YAML, and back.</summary>
	/// <remarks>
	/// The actor, the player, and the target become references through the writer, because the objects
	/// they name may not exist yet when the restore reads the block. <see cref="Load"/> therefore defers
	/// the source actor and the weapon target.
	/// </remarks>
	public static class WarheadArgsCodec
	{
		public const string WeaponKey = "Weapon";
		public const string DamageModifiersKey = "DamageModifiers";
		public const string SourceKey = "Source";
		public const string ImpactOrientationKey = "ImpactOrientation";
		public const string ImpactPositionKey = "ImpactPosition";
		public const string SourceActorKey = "SourceActor";
		public const string SourceOwnerKey = "SourceOwner";
		public const string WeaponTargetKey = "WeaponTarget";

		public static List<MiniYamlNode> Save(WarheadArgs args, World world, SnapshotWriter w)
		{
			ArgumentNullException.ThrowIfNull(args);
			ArgumentNullException.ThrowIfNull(world);
			ArgumentNullException.ThrowIfNull(w);

			var weapon = WeaponRefs.WeaponKeyOf(world, args.Weapon);
			if (weapon == null)
				return null;

			return
			[
				new(WeaponKey, weapon),
				new(DamageModifiersKey, FormatModifiers(args.DamageModifiers)),
				new(SourceKey, args.Source.HasValue ? FieldSaver.FormatValue(args.Source.Value) : ""),
				new(ImpactOrientationKey, FieldSaver.FormatValue(args.ImpactOrientation)),
				new(ImpactPositionKey, FieldSaver.FormatValue(args.ImpactPosition)),
				new(SourceActorKey, w.ActorRef(args.SourceActor)),
				new(SourceOwnerKey, w.PlayerRef(args.SourceOwner)),
				new(WeaponTargetKey, w.TargetRef(args.WeaponTarget))
			];
		}

		public static WarheadArgs Load(MiniYaml yaml, World world, SnapshotReader r)
		{
			ArgumentNullException.ThrowIfNull(yaml);
			ArgumentNullException.ThrowIfNull(world);
			ArgumentNullException.ThrowIfNull(r);

			var nodes = yaml.ToDictionary();
			var source = Value(nodes, SourceKey);

			var args = new WarheadArgs
			{
				Weapon = WeaponRefs.ResolveWeapon(world, Value(nodes, WeaponKey)),
				DamageModifiers = ParseModifiers(nodes, DamageModifiersKey),
				Source = string.IsNullOrEmpty(source) ? null : FieldLoader.GetValue<WPos>(SourceKey, source),
				ImpactOrientation = Field<WRot>(nodes, ImpactOrientationKey),
				ImpactPosition = Field<WPos>(nodes, ImpactPositionKey),
				WeaponTarget = Target.Invalid,
				World = world,
				SourceOwner = r.ResolvePlayer(Value(nodes, SourceOwnerKey))
			};

			r.DeferActor(Value(nodes, SourceActorKey), a => args.SourceActor = a);
			r.DeferTarget(Value(nodes, WeaponTargetKey), t => args.WeaponTarget = t);

			return args;
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
