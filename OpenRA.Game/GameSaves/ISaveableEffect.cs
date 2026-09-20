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
using OpenRA.Effects;

namespace OpenRA.GameSaves
{
	/// <summary>An effect that a save can write and a restore can rebuild.</summary>
	/// <remarks>
	/// <see cref="EffectSerializer"/> saves effects in world order, because <see cref="World.SyncHash"/>
	/// hashes a synced effect by position and not by identity. An unsaveable synced effect therefore
	/// changes the hash of every effect after it.
	/// </remarks>
	public interface ISaveableEffect : IEffect
	{
		List<MiniYamlNode> SaveState(World world, SnapshotWriter w);
	}

	/// <summary>Reports whether an effect can reach everything it needs.</summary>
	/// <remarks>
	/// The restore reads <see cref="ReferencesRestored"/> after the deferred references resolve. An
	/// effect that reports false at that point leaves the world, because it cannot run without them.
	/// </remarks>
	public interface IRequiresRestoredReferences
	{
		bool ReferencesRestored { get; }
	}
}
