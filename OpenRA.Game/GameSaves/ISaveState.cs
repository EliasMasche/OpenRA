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
using OpenRA.Traits;

namespace OpenRA.GameSaves
{
	/// <summary>State that one trait contributes to a snapshot, for one actor.</summary>
	/// <remarks>
	/// A trait implements this interface when its state must survive a save. The writer keys each block
	/// by actor id, trait type name, and instance name.
	/// <para>
	/// This interface is not <see cref="ISync"/>. <see cref="ISync"/> marks the state that
	/// <see cref="World.SyncHash"/> hashes to detect a desync while the game runs.
	/// </para>
	/// </remarks>
	[RequireExplicitImplementation]
	public interface ISaveState
	{
		TraitInfo SaveStateInfo { get; }

		List<MiniYamlNode> SaveState(Actor self, SnapshotWriter w);

		void LoadState(Actor self, MiniYaml data, SnapshotReader r);
	}

	/// <summary>Called on a trait after the restore has loaded the state of every actor.</summary>
	/// <remarks>
	/// The world tick and the random number generator state are not restored yet when this runs, so a
	/// trait must not read them here.
	/// </remarks>
	[RequireExplicitImplementation]
	public interface INotifyStateRestored
	{
		void StateRestored(Actor self);
	}
}
