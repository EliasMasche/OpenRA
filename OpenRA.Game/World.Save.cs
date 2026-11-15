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
using System.IO;
using System.Linq;
using OpenRA.Network;
using OpenRA.Snapshot;
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

		public void RequestGameSave(string filename, bool isAutosave)
		{
			// The server decides which games save. A game of one person saves on a server that is not dedicated.
			if (!LobbyInfo.GlobalSettings.EnableGameSaves)
			{
				NotifyGameNotSaved(isAutosave);
				return;
			}

			SaveAfterThisTick(filename, isAutosave);
		}

		// Saves this world after the tick that runs now. A snapshot is taken between two ticks, and a save is asked
		// for from inside one, as by an autosave.
		internal void SaveAfterThisTick(string filename, bool isAutosave)
		{
			Game.RunAfterTick(() => SaveSnapshot(filename, isAutosave));
		}

		// Writes a save that holds a snapshot of this world and the lobby of its game. This client writes the file,
		// because it holds the world. A save that the writer refuses, or whose file cannot be written, is not
		// taken, and the log says why. A save that the writer refuses for the state of the script is taken as a
		// save of every order.
		void SaveSnapshot(string filename, bool isAutosave)
		{
			if (Disposing)
				return;

			// The UI queues frame-end tasks between ticks, as for the marker of an order. Each tick runs the tasks of
			// the simulation, so a task that waits here is local to this client. It only adds what a save leaves out.
			while (FrameEndActions.Count != 0)
				FrameEndActions.Dequeue()(this);

			byte[] snapshot;
			try
			{
				snapshot = SnapshotFile.Write(SnapshotWriter.Write(this), this);
			}
			catch (ScriptStateRefusedException e) when (LobbyInfo.NonBotClients.Count() == 1)
			{
				// The save of every order runs the script again from its start, so the player still gets a save.
				// The server keeps the orders of a game of one person only.
				Log.Write("debug", $"The game is saved to {filename} as every order: {e.Message}");
				RequestOrderSave(filename, isAutosave);
				return;
			}
			catch (SnapshotRefusedException e)
			{
				Log.Write("debug", $"The game was not saved to {filename}: {e.Message}");
				NotifyGameNotSaved(isAutosave);
				return;
			}

			var invalidChars = Path.GetInvalidFileNameChars();
			filename = new string(filename.Where(c => !invalidChars.Contains(c)).ToArray());

			// A file that cannot be written, as on a full disk or over a file that is locked, is no reason to end the game.
			var folder = Path.Combine(Platform.SupportDir, "Saves", modData.Manifest.Id, modData.Manifest.Metadata.Version);
			try
			{
				Directory.CreateDirectory(folder);
				GameSave.FromSnapshot(LobbyInfo, modData.MapCache[Map.Uid], snapshot, OrderManager.NetFrameNumber).Save(Path.Combine(folder, filename));
			}
			catch (Exception e) when (e is IOException or UnauthorizedAccessException)
			{
				Log.Write("debug", $"The game was not saved to {filename}: {e.Message}");
				NotifyGameNotSaved(isAutosave);
				return;
			}

			foreach (var nsr in WorldActor.TraitsImplementing<INotifyGameSaved>())
				nsr.GameSaved(this, isAutosave);
		}

		internal void NotifyGameNotSaved(bool isAutosave)
		{
			foreach (var nsr in WorldActor.TraitsImplementing<INotifyGameNotSaved>())
				nsr.GameNotSaved(this, isAutosave);
		}
	}
}
