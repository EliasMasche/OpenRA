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

namespace OpenRA.Server
{
	public sealed partial class Server
	{
		[FluentReference]
		const string SaveOfOnePlayer = "notification-save-of-one-player";

		[FluentReference]
		const string SaveOfMorePlayers = "notification-save-of-more-players";

		[FluentReference]
		const string SaveCannotBeLoaded = "notification-save-cannot-be-loaded";

		[FluentReference]
		const string RequiresHost = "notification-requires-host";

		[FluentReference("player", "save")]
		const string LoadedSave = "notification-loaded-save";

		[FluentReference]
		const string SaveNotOnThisServer = "notification-save-not-on-this-server";

		// A new game starts at net frame 1 with empty packets. A game that goes on from a saved position starts
		// at the net frame of the position, with the packets that were in transit there.
		int startFrame = 1;
		byte[][] startPackets = [];

		// The file of the save that this game goes on from, when the save holds a snapshot. The client loads the
		// snapshot from it.
		string snapshotSave = "";

		/// <summary>
		/// Whether the lobby holds a save with a snapshot. Its slots and options are those of the save, and the
		/// game goes on from the save when it starts.
		/// </summary>
		public bool HasSnapshotSave => snapshotSave.Length > 0;

		// Whether the server takes the save order named by order from conn. Only the admin saves the game. Any
		// other client could write files into the folder of saves of this machine, as often as it sends the order.
		bool AcceptsSaveOrder(Connection conn, string order)
		{
			var client = GetClient(conn);
			if (client?.IsAdmin == true)
				return true;

			Log.Write("server", $"The order {order} of {client?.Name} is ignored: only the admin saves the game.");
			return false;
		}

		static string SanitizeFilename(string filename)
		{
			// Sanitize potentially malicious input
			int invalidIndex;
			var invalidChars = Path.GetInvalidFileNameChars();
			while ((invalidIndex = filename.IndexOfAny(invalidChars)) != -1)
				filename = filename.Remove(invalidIndex, 1);

			return filename;
		}

		// What the order that loads a save has read and checked, before the lobby changes.
		readonly record struct SaveToLoad(string Filename, GameSave Save, OrderManagerPosition Position);

		// Reads the save that a client names in the order that loads a save, and checks it. A save that this server
		// does not load tells the client why, and changes nothing.
		bool TryReadSaveToLoad(Connection conn, string target, out SaveToLoad load)
		{
			load = default;

			// A save changes the lobby of everybody in it, as a change of the map does.
			if (IsMultiplayer && !GetClient(conn).IsAdmin)
			{
				SendFluentMessageTo(conn, RequiresHost);
				return false;
			}

			// A dedicated server runs no world, so it has no folder of saves of a player.
			if (Type == ServerType.Dedicated)
			{
				SendFluentMessageTo(conn, SaveNotOnThisServer);
				return false;
			}

			var filename = SanitizeFilename(target);
			var savePath = Path.Combine(Platform.SupportDir, "Saves", ModData.Manifest.Id, ModData.Manifest.Metadata.Version, filename);

			// A save is a file that can be damaged, changed by hand, written by another build, or gone. The
			// server reads and checks each part that the load uses before the lobby changes. An exception
			// of this read means that the save cannot be loaded. The server then refuses the save and tells
			// the client. The lobby stays as it was, so the player can load another save or start a new game.
			GameSave save;
			OrderManagerPosition position = null;
			try
			{
				save = new GameSave(savePath);

				// The game of a save starts on the map of the save, so this server needs that map. A generated
				// map is made again from the arguments that the save holds, as the order GenerateMap makes it.
				var preview = ModData.MapCache[save.GlobalSettings.Map];
				if (preview.Status != MapStatus.Available && preview.Class != MapClassification.Generated
					&& save.MapGenerationArgs?.Uid == save.GlobalSettings.Map && LobbyInfo.GlobalSettings.EnableMapGeneration)
					preview.UpdateFromGenerationArgs(save.MapGenerationArgs);

				var isGenerated = preview.Class == MapClassification.Generated && preview.Status is MapStatus.Generatable or MapStatus.Generating;
				if (preview.Status != MapStatus.Available && !isGenerated)
					throw new InvalidDataException($"The map {save.GlobalSettings.Map} of the save is not installed.");

				// A save does not put the server on a map that its map pool leaves out.
				if (MapPool != null && !MapPool.Contains(save.GlobalSettings.Map))
					throw new InvalidDataException($"The map {save.GlobalSettings.Map} of the save is not in the map pool of this server.");

				// The start of the game reads the game speed and the slots of the lobby, and a value that it
				// does not know ends the server there. A save that was changed by hand can hold one.
				var gameSpeeds = ModData.GetOrCreate<GameSpeeds>();
				var gameSpeed = save.GlobalSettings.OptionOrDefault("gamespeed", gameSpeeds.DefaultSpeed);
				if (!gameSpeeds.Speeds.ContainsKey(gameSpeed))
					throw new InvalidDataException($"The game speed {gameSpeed} of the save is not a game speed of this mod.");

				if (preview.Status == MapStatus.Available)
				{
					var slot = save.Slots.Keys.FirstOrDefault(s => !preview.Players.Players.ContainsKey(s));
					if (slot != null)
						throw new InvalidDataException($"The map {save.GlobalSettings.Map} of the save has no slot {slot}.");
				}

				if (save.HasSnapshot)
				{
					var snapshot = SnapshotFile.Read(save.ReadSnapshot());
					position = OrdersSection.ReadPosition(snapshot.Section(SnapshotFormat.Sections.Orders));
				}

				if (position != null)
					CheckStartPosition(position);
			}
			catch (Exception e)
			{
				Log.Write("server", $"The save {filename} cannot be loaded: {e}");
				SendFluentMessageTo(conn, SaveCannotBeLoaded);
				return false;
			}

			// A save with a snapshot holds the packets in transit of one client, so it is the game of one person. The
			// server refuses it while another person is in the lobby, and changes nothing.
			if (save.HasSnapshot && LobbyInfo.NonBotClients.Count() > 1)
			{
				SendFluentMessageTo(conn, SaveOfOnePlayer);
				return false;
			}

			// Each person of the save takes the place of a person in the lobby.
			if (save.SlotClients.Values.Count(c => c.Bot == null) > LobbyInfo.NonBotClients.Count())
			{
				SendFluentMessageTo(conn, SaveOfMorePlayers);
				return false;
			}

			load = new SaveToLoad(filename, save, position);
			return true;
		}

		void HoldSaveInLobby(SaveToLoad load)
		{
			var (filename, _, position) = load;

			// A save that holds a snapshot holds no orders to send. Its game goes on from the position of
			// the snapshot, and the client loads the snapshot from the same file.
			if (position != null)
			{
				ContinueFrom(position);
				snapshotSave = filename;
				LobbyInfo.GlobalSettings.HoldsSnapshotSave = true;
				GameSave = null;
			}
		}

		// Lets the game go on from position in place of a new start. The game begins at the net frame of the
		// position, and its client gets the packets that were in transit there. Such a game keeps no order log,
		// because the log would start in the middle of the game.
		internal void ContinueFrom(OrderManagerPosition position)
		{
			lock (LobbyInfoLock)
			{
				if (State >= ServerState.GameStarted)
					throw new InvalidOperationException("A game goes on from a saved position only before it starts.");

				CheckStartPosition(position);
				startFrame = position.NetFrameNumber;
				startPackets = position.Packets.Select(p => p.Orders).ToArray();
			}
		}

		/// <summary>
		/// Forgets the save that the lobby holds, as when the map changes. The game that starts next is a new game.
		/// </summary>
		public void ForgetGameSave()
		{
			lock (LobbyInfoLock)
			{
				GameSave = null;
				startFrame = 1;
				startPackets = [];
				snapshotSave = "";

				LobbyInfo.GlobalSettings.HoldsSnapshotSave = false;
			}
		}

		// Refuses a position that a game cannot start from: a net frame before the first one, or packets that are
		// not for the frames that follow the net frame of the position.
		static void CheckStartPosition(OrderManagerPosition position)
		{
			if (position.NetFrameNumber < 1)
				throw new InvalidDataException($"A game is never at net frame {position.NetFrameNumber}.");

			for (var i = 0; i < position.Packets.Length; i++)
				if (position.Packets[i].Frame != position.NetFrameNumber + i)
					throw new InvalidDataException($"Packet {i} is for frame {position.Packets[i].Frame}. "
						+ $"A game at net frame {position.NetFrameNumber} expects frame {position.NetFrameNumber + i}.");
		}

		// Whether the saved game that the lobby holds can start. If not, the people in the lobby learn why.
		bool SaveAllowsStart()
		{
			// A saved position holds the packets of one client, and each connection would get them. So the game of such a
			// position does not start while a person who joined after it was taken is in the lobby.
			if ((startFrame != 1 || startPackets.Length > 0 || snapshotSave.Length > 0) && LobbyInfo.NonBotClients.Count() > 1)
			{
				SendFluentMessage(SaveOfOnePlayer);
				return false;
			}

			return true;
		}
	}
}
