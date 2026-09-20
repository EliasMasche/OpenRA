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
using System.Globalization;
using System.IO;
using OpenRA.GameRules;
using OpenRA.Traits;

namespace OpenRA.GameSaves
{
	public static class WeaponRefs
	{
		const char Separator = ':';

		public static string WeaponKeyOf(World world, WeaponInfo weapon)
		{
			ArgumentNullException.ThrowIfNull(world);

			if (weapon == null)
				return null;

			foreach (var kv in world.Map.Rules.Weapons)
				if (kv.Value == weapon)
					return kv.Key;

			return null;
		}

		public static WeaponInfo ResolveWeapon(World world, string key)
		{
			ArgumentNullException.ThrowIfNull(world);

			if (string.IsNullOrEmpty(key) || !world.Map.Rules.Weapons.TryGetValue(key, out var weapon))
				throw new InvalidDataException($"Snapshot names an unknown weapon '{key}'.");

			return weapon;
		}

		public static string WarheadRef(World world, WeaponInfo weapon, IWarhead warhead)
		{
			ArgumentNullException.ThrowIfNull(world);

			var key = WeaponKeyOf(world, weapon);
			if (key == null || warhead == null)
				return null;

			var index = weapon.Warheads.IndexOf(warhead);
			if (index < 0)
				return null;

			return key + Separator + index.ToStringInvariant();
		}

		public static IWarhead ResolveWarhead(World world, string value)
		{
			ArgumentNullException.ThrowIfNull(world);

			if (string.IsNullOrEmpty(value))
				return null;

			var split = value.LastIndexOf(Separator);
			if (split <= 0)
				throw new InvalidDataException($"Malformed warhead reference '{value}'.");

			var key = value[..split];
			if (!int.TryParse(value[(split + 1)..], NumberStyles.None, NumberFormatInfo.InvariantInfo, out var index))
				throw new InvalidDataException($"Malformed warhead reference '{value}'.");

			if (!world.Map.Rules.Weapons.TryGetValue(key, out var weapon) || index >= weapon.Warheads.Length)
				return null;

			return weapon.Warheads[index];
		}
	}
}
