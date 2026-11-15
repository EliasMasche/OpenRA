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
		// The snapshot of saveFile is loaded into the world before its first tick. A save that cannot be read or
		// loaded ends the game for this client.
		internal static void StartGameFromSnapshot(string uid, string saveFile)
		{
			// The file is in the folder that the server of this game reads its saves from.
			var path = Path.Combine(Platform.SupportDir, "Saves", ModData.Manifest.Id, ModData.Manifest.Metadata.Version, Path.GetFileName(saveFile));

			// The file can change after the server read it. As on the server, an exception of this read means that
			// the save cannot be loaded.
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

			// A replay runs a game from its first tick, and this game goes on from a later one. So it is not recorded.
			// The connection hands the order that starts the game to the recording after this call, so no file exists yet.
			if (OrderManager.Connection is NetworkConnection connection)
				connection.StopRecording();

			OrderManager.StartupActors = StartupActors.FromSnapshot;
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

		/// <summary>
		/// Why the last game left before it started, until the menu has told the player. Empty when there is nothing to tell. It
		/// gives the reason once.
		/// </summary>
		public static string TakeStartFailure()
		{
			var reason = startFailure;
			startFailure = "";
			return reason;
		}

		// Leaves a game that the server started and that this client cannot run. A host without a display has no
		// menu to go back to, so its world is disposed and the game is over. A player who sees the menu is told why.
		internal static void LeaveUnstartableGame(string reason)
		{
			Log.Write("debug", $"The game was left before it started: {reason}");
			var left = OrderManager;
			left.StartFailure = reason;
			startFailure = reason;
			Disconnect();
			LoadShellMap();
		}
	}
}
