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

namespace OpenRA.GameSaves
{
	/// <summary>Where a load puts the clients that are already connected.</summary>
	public enum SeatingPlan
	{
		FromSave,

		PreserveCurrent,
	}

	/// <summary>Decides the seating for a load.</summary>
	/// <remarks>
	/// A load into a running match keeps the current seating. A load from the lobby takes the seating
	/// from the save.
	/// </remarks>
	public static class LoadSeating
	{
		public static SeatingPlan Plan(bool gameRunning, int humanCount)
		{
			ArgumentOutOfRangeException.ThrowIfNegative(humanCount);

			if (gameRunning)
				return SeatingPlan.PreserveCurrent;

			return SeatingPlan.FromSave;
		}
	}

	/// <summary>The conditions that make a load impossible.</summary>
	public enum LoadRefusal
	{
		None,

		MapUnavailable,

		DifferentMap,
	}

	/// <summary>Decides whether a snapshot can be loaded into the current world.</summary>
	/// <remarks>
	/// A save that names a map that the client does not have is refused. A save from a different map is
	/// refused, unless the current map is not known yet.
	/// </remarks>
	public static class LoadPolicy
	{
		public static LoadRefusal CanRestoreInto(string saveMapUid, string currentMapUid, bool saveMapAvailable)
		{
			ArgumentNullException.ThrowIfNull(saveMapUid);

			if (!saveMapAvailable)
				return LoadRefusal.MapUnavailable;

			if (string.IsNullOrEmpty(currentMapUid))
				return LoadRefusal.None;

			return string.Equals(saveMapUid, currentMapUid, StringComparison.Ordinal)
				? LoadRefusal.None
				: LoadRefusal.DifferentMap;
		}
	}
}
