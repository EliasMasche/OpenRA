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
using System.IO;
using System.Linq;
using OpenRA.Traits;

namespace OpenRA.GameSaves
{
	/// <summary>A named block of state for the world actor or a player actor.</summary>
	/// <remarks>
	/// Use this interface for state that is too large or too structured for the per-actor YAML. The trait
	/// supplies <see cref="SectionName"/>, and the writer stores the block as its own compressed section.
	/// </remarks>
	[RequireExplicitImplementation]
	public interface IWorldSaveState
	{
		string SectionName { get; }

		void SaveState(Actor self, Stream s, SnapshotWriter w);

		void LoadState(Actor self, Stream s, SnapshotReader r);
	}

	/// <summary>Runs a map script that a restore must delay.</summary>
	/// <remarks>
	/// A map script reads the map actors at its top level. The restore calls this after it creates the
	/// actors, so the script does not read an empty list.
	/// </remarks>
	[RequireExplicitImplementation]
	public interface IDeferScriptUntilRestored
	{
		void RunDeferredScript();
	}

	/// <summary>Loads a bulk section before the restore runs the deferred map script.</summary>
	/// <remarks>
	/// The script reads this state, so the restore loads it first.
	/// </remarks>
	[RequireExplicitImplementation]
	public interface ILoadBeforeDeferredScript
	{
	}

	/// <summary>The actors that the map spawned, with the id range that covers them.</summary>
	public sealed record MapActorRoster(IReadOnlyList<KeyValuePair<string, uint>> Names, uint FirstID, uint LastID)
	{
		public static readonly MapActorRoster None = new([], 0, 0);

		public bool IsEmpty => FirstID == 0;

		public bool Contains(uint actorID)
		{
			return !IsEmpty && actorID >= FirstID && actorID <= LastID;
		}

		public HashSet<uint> ActorIds()
		{
			return Names.Select(kv => kv.Value).ToHashSet();
		}
	}

	/// <summary>Holds the list of actors that the map spawned, so a restore can rebuild the list.</summary>
	/// <remarks>
	/// An actor in the list receives a <c>SpawnedByMapInit</c> when the restore recreates it, which is
	/// how a trait tells a map actor from an actor that a player built.
	/// </remarks>
	[RequireExplicitImplementation]
	public interface IMapActorRoster
	{
		MapActorRoster SaveRoster { get; }

		void LoadRoster(MapActorRoster roster, SnapshotReader reader);
	}
}
