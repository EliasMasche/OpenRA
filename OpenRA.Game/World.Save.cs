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
		/// Whether a snapshot load writes into this world now. A script does not see the actors that it adds.
		/// </summary>
		public bool IsLoadingSnapshot { get; internal set; }

		/// <summary>Whether this world goes on with a saved game, and <see cref="INotifyGameLoaded"/> has not run yet.</summary>
		public bool IsResumingSave { get; private set; }

		internal uint NextActorId { get; set; }

		// Puts back the state of the world itself. It issues no order and notifies nobody: the saved world did both.
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
			// Each client of a game of more people saves its own copy after the tick of the order of the host.
			if (LobbyInfo.NonBotClients.Count() > 1)
			{
				if (OrderManager.LocalClient?.IsAdmin == true && LobbyInfo.GlobalSettings.EnableGameSaves && GameSaveCopy.IsCopyName(filename))
					IssueOrder(Order.FromTargetString("CreateGameSnapshot", filename, false, isAutosave ? 1u : 0u));
				else
					NotifyGameNotSaved(isAutosave);

				return;
			}

			if (!LobbyInfo.GlobalSettings.EnableGameSaves)
			{
				NotifyGameNotSaved(isAutosave);
				return;
			}

			SaveAfterThisTick(filename, isAutosave);
		}

		// A snapshot comes between two ticks, and a request for a save comes from inside one.
		internal void SaveAfterThisTick(string filename, bool isAutosave)
		{
			Game.RunAfterTick(() => SaveSnapshot(filename, isAutosave));
		}

		// A save that the writer refuses, or that cannot be written, is not taken, and the log says why. A refusal for the
		// state of a script falls back to the save of every order.
		void SaveSnapshot(string filename, bool isAutosave)
		{
			if (Disposing)
				return;

			// Frame-end tasks that wait here are local to this client, such as the marker of an order.
			while (FrameEndActions.Count != 0)
				FrameEndActions.Dequeue()(this);

			byte[] snapshot;
			try
			{
				snapshot = SnapshotFile.Write(SnapshotWriter.Write(this), this);
			}
			catch (ScriptStateRefusedException e) when (LobbyInfo.NonBotClients.Count() == 1)
			{
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
