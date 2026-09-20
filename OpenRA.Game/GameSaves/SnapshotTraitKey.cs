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

namespace OpenRA.GameSaves
{
	/// <summary>Identifies one trait on one actor: actor id, trait name, and instance name.</summary>
	/// <remarks>
	/// The text form is <c>actorId/traitName</c>, with <c>@instanceName</c> appended where the trait has
	/// an instance name. A trait name cannot contain either separator, so a key parses back to the same
	/// three parts.
	/// </remarks>
	public readonly struct SnapshotTraitKey : IEquatable<SnapshotTraitKey>
	{
		const char ActorSeparator = '/';
		const char InstanceSeparator = '@';

		public readonly uint ActorID;

		public readonly string TraitName;

		public readonly string InstanceName;

		public SnapshotTraitKey(uint actorID, string traitName, string instanceName = null)
		{
			if (string.IsNullOrEmpty(traitName))
				throw new ArgumentException("Trait name must not be empty.", nameof(traitName));

			if (traitName.Contains(ActorSeparator) || traitName.Contains(InstanceSeparator))
				throw new ArgumentException($"Trait name '{traitName}' must not contain '{ActorSeparator}' or '{InstanceSeparator}'.", nameof(traitName));

			if (instanceName != null && instanceName.Contains(InstanceSeparator))
				throw new ArgumentException($"Instance name '{instanceName}' must not contain '{InstanceSeparator}'.", nameof(instanceName));

			ActorID = actorID;
			TraitName = traitName;

			InstanceName = string.IsNullOrEmpty(instanceName) ? null : instanceName;
		}

		public override string ToString()
		{
			var key = ActorID.ToStringInvariant() + ActorSeparator + TraitName;
			if (InstanceName != null)
				key += InstanceSeparator + InstanceName;

			return key;
		}

		public static SnapshotTraitKey Parse(string key)
		{
			if (!TryParse(key, out var result))
				throw new InvalidDataException($"Malformed snapshot trait key '{key}'.");

			return result;
		}

		public static bool TryParse(string key, out SnapshotTraitKey result)
		{
			result = default;
			if (string.IsNullOrEmpty(key))
				return false;

			var actorEnd = key.IndexOf(ActorSeparator);
			if (actorEnd <= 0)
				return false;

			if (!uint.TryParse(key[..actorEnd], NumberStyles.None, NumberFormatInfo.InvariantInfo, out var actorID))
				return false;

			var rest = key[(actorEnd + 1)..];
			if (rest.Length == 0)
				return false;

			if (rest.Contains(ActorSeparator))
				return false;

			var instanceStart = rest.IndexOf(InstanceSeparator);
			if (instanceStart < 0)
			{
				result = new SnapshotTraitKey(actorID, rest);
				return true;
			}

			var traitName = rest[..instanceStart];
			var instanceName = rest[(instanceStart + 1)..];
			if (traitName.Length == 0 || instanceName.Length == 0 || instanceName.Contains(InstanceSeparator))
				return false;

			result = new SnapshotTraitKey(actorID, traitName, instanceName);
			return true;
		}

		public bool Equals(SnapshotTraitKey other)
		{
			return ActorID == other.ActorID && TraitName == other.TraitName && InstanceName == other.InstanceName;
		}

		public override bool Equals(object obj)
		{
			return obj is SnapshotTraitKey other && Equals(other);
		}

		public override int GetHashCode()
		{
			return HashCode.Combine(ActorID, TraitName, InstanceName);
		}

		public static bool operator ==(SnapshotTraitKey a, SnapshotTraitKey b) { return a.Equals(b); }
		public static bool operator !=(SnapshotTraitKey a, SnapshotTraitKey b) { return !a.Equals(b); }
	}
}
