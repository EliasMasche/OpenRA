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
using OpenRA.Network;
using OpenRA.Snapshot;

namespace OpenRA
{
	public static partial class Game
	{
		// A save that cannot be read or loaded ends the game for this client.
		internal static void StartGameFromSnapshot(string uid, string saveFile)
		{
			var path = Path.Combine(Platform.SupportDir, "Saves", ModData.Manifest.Id, ModData.Manifest.Metadata.Version, Path.GetFileName(saveFile));

			SnapshotFile snapshot;
			GameSave save;
			try
			{
				save = new GameSave(path);
				snapshot = SnapshotFile.Read(save.ReadSnapshot());
			}
			catch (Exception e)
			{
				LeaveUnstartableGame($"the save {saveFile} cannot be read: {e.Message}");
				return;
			}

			// A replay starts at the first tick, and this game at a later one, so it is not recorded.
			if (OrderManager.Connection is NetworkConnection connection)
				connection.StopRecording();

			OrderManager.StartupActors = StartupActors.FromSnapshot;
			OrderManager.SavedSlotClients = save.SlotClients;
			StartGame(uid, WorldType.Regular);
			try
			{
				var world = OrderManager.World;
				SnapshotReader.Load(world, snapshot.Nodes());

				// The saved world and the loaded one have to agree on everything that the sync hash covers.
				if (world.SyncHash() != snapshot.SyncHash)
					throw new SnapshotLoadException($"the loaded world has the sync hash {world.SyncHash()}, and the saved one had {snapshot.SyncHash}.");
			}
			catch (SnapshotLoadException e)
			{
				LeaveUnstartableGame($"the save {saveFile} cannot be loaded: {e.Message}");
			}
		}

		static string startFailure = "";

		/// <summary>Why the last game left before it started, once, or empty.</summary>
		public static string TakeStartFailure()
		{
			var reason = startFailure;
			startFailure = "";
			return reason;
		}

		// Leaves a game that the server started and that this client cannot run.
		internal static void LeaveUnstartableGame(string reason)
		{
			Log.Write("debug", $"The game was left before it started: {reason}");
			var left = OrderManager;
			left.StartFailure = reason;
			startFailure = reason;
			Disconnect();
			if (Host.Profile.HasDisplay)
				LoadShellMap();
			else
			{
				left.World?.Dispose();
				left.World = null;
			}
		}
	}
}
