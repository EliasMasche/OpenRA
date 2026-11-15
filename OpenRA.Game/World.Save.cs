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

using OpenRA.Traits;

namespace OpenRA
{
	public sealed partial class World
	{
		/// <summary>
		/// Whether a snapshot is being written into this world. The load adds each actor to the world again, which is no
		/// event of the game, so what a script waits for does not run then.
		/// </summary>
		public bool IsLoadingSnapshot { get; internal set; }

		/// <summary>
		/// Whether this world goes on with a saved game, and <see cref="INotifyGameLoaded"/> has not run yet. The world
		/// replays the orders of the save, or a snapshot is loaded into it before its first tick. A trait that greets a
		/// new game greets this one when the load is complete.
		/// </summary>
		public bool IsResumingSave { get; private set; }

		// The ID that the next actor gets. A snapshot holds it, and a load sets it.
		internal uint NextActorId { get; set; }

		// Puts back the state that belongs to the world itself and to no trait. It issues no order and notifies
		// nobody, because the world that was saved has done both. A pause order would only arrive a net frame
		// later, and SetPauseState issues none once the game is over.
		internal void RestoreRunState(int worldTick, bool paused, bool predictedPaused, bool isGameOver)
		{
			WorldTick = worldTick;
			Paused = paused;
			PredictedPaused = predictedPaused;
			IsGameOver = isGameOver;

			// A game that is over stands still, so the tick it ended on is the tick it is at.
			if (isGameOver)
				gameInfo.FinalGameTick = worldTick;
		}
	}
}
