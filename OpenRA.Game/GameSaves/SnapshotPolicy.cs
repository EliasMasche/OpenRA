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
using System.Linq;
using OpenRA.Traits;

namespace OpenRA.GameSaves
{
	/// <summary>Decides which servers and which maps offer snapshot saves.</summary>
	/// <remarks>
	/// A dedicated server offers game saves only when its settings enable them. A scripted map needs the
	/// order stream, because its script state is part of the snapshot.
	/// </remarks>
	public static class SnapshotPolicy
	{
		public const string ScriptTraitName = "LuaScriptInfo";

		public static bool IsScripted(ActorInfo worldActor)
		{
			return worldActor.TraitInfos<TraitInfo>().Any(i => i.GetType().Name == ScriptTraitName);
		}

		public static bool UsesSnapshotSaves(ActorInfo worldActor)
		{
			ArgumentNullException.ThrowIfNull(worldActor);

			return true;
		}

		public static bool ServerOffersGameSaves(bool isDedicated, bool enabledForDedicated)
		{
			return !isDedicated || enabledForDedicated;
		}

		public static bool NeedsOrderStream(ActorInfo worldActor)
		{
			return IsScripted(worldActor);
		}
	}
}
