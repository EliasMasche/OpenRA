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
using System.Collections.Generic;
using System.Collections.Immutable;
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

		[FluentReference]
		const string CopyOfAnotherSave = "notification-copy-of-another-save";

		[FluentReference("players")]
		const string SaveNeedsEachPlayer = "notification-save-needs-each-player";

		[FluentReference("player", "save")]
		const string LoadedSave = "notification-loaded-save";

		[FluentReference]
		const string SaveNotOnThisServer = "notification-save-not-on-this-server";

		[FluentReference("reason")]
		const string SharedSaveNotTaken = "notification-shared-save-not-taken";

		// A game that goes on from a saved position starts at its net frame, with the packets in transit there.
		int startFrame = 1;
		byte[][] startPackets = [];

		string snapshotSave = "";

		/// <summary>Whether the lobby holds a save with a snapshot, with the slots and options of the save.</summary>
		public bool HasSnapshotSave => snapshotSave.Length > 0;

		/// <summary>Whether the save loads from copies. Each person takes a saved slot with the lobby command slot.</summary>
		public bool LoadsFromCopies => LobbyInfo.GlobalSettings.SnapshotSave != null;

		// The people of the saved slots, by their client index in the saved game, also a person who left.
		readonly Dictionary<int, SlotClient> savedPeople = [];

		// The copy that each connection reported: its client index in the saved game and its packets in transit. The admin
		// reports its own file. Each other person reports the file of the admin, which the server gave it.
		readonly Dictionary<int, (int SavedIndex, byte[][] Packets, bool IsShared)> copies = [];

		ImmutableArray<int> savedLeft = [];

		// The file of the admin, which the server hands to each other person.
		byte[] sharedSave;

		SharedSave upload;
		int uploadFrom = -1;

		int fileRequestedFrom = -1;

		readonly HashSet<int> sentFileTo = [];

		// Only the admin saves: any other client could write files into the folder of saves of this machine.
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

		readonly record struct SaveToLoad(string Filename, GameSave Save, OrderManagerPosition Position, int Tick, int SyncHash, bool FromCopies);

		// Reads and checks the save that the load order names. A refused save tells the client why and changes nothing.
		bool TryReadSaveToLoad(Connection conn, string target, out SaveToLoad load)
		{
			load = default;

			// A save changes the lobby of everybody in it, as a change of the map does.
			if (IsMultiplayer && !GetClient(conn).IsAdmin)
			{
				SendFluentMessageTo(conn, RequiresHost);
				return false;
			}

			if (Type == ServerType.Dedicated)
			{
				SendFluentMessageTo(conn, SaveNotOnThisServer);
				return false;
			}

			var filename = SanitizeFilename(target);
			var savePath = Path.Combine(Platform.SupportDir, "Saves", ModData.Manifest.Id, ModData.Manifest.Metadata.Version, filename);

			// A save can be damaged, changed by hand, of another build, or gone. An exception here refuses it.
			GameSave save;
			OrderManagerPosition position = null;
			var (tick, syncHash) = (0, 0);
			try
			{
				save = new GameSave(savePath);

				// Makes a generated map again from the arguments in the save, as GenerateMap does.
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

				// A value that the start does not know ends the server there.
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
					(tick, syncHash) = (snapshot.Tick, snapshot.SyncHash);
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

			// A multiplayer server loads a snapshot save from copies, and any copy loads for any person. A server of this
			// machine loads the save of one person: its people share one folder of saves.
			var fromCopies = save.HasSnapshot && Type == ServerType.Multiplayer;
			if (!fromCopies)
			{
				// A save with a snapshot holds the packets in transit of one client.
				if (save.HasSnapshot && LobbyInfo.NonBotClients.Count() > 1)
				{
					SendFluentMessageTo(conn, SaveOfOnePlayer);
					return false;
				}

				if (save.SlotClients.Values.Count(c => c.Bot == null) > LobbyInfo.NonBotClients.Count())
				{
					SendFluentMessageTo(conn, SaveOfMorePlayers);
					return false;
				}
			}

			load = new SaveToLoad(filename, save, position, tick, syncHash, fromCopies);
			return true;
		}

		void HoldSaveInLobby(SaveToLoad load)
		{
			var (filename, _, position, tick, syncHash, fromCopies) = load;

			if (position != null)
			{
				ContinueFrom(position);
				snapshotSave = filename;
				LobbyInfo.GlobalSettings.HoldsSnapshotSave = true;
				GameSave = null;
			}

			if (fromCopies)
			{
				LobbyInfo.GlobalSettings.SnapshotSave = filename;
				LobbyInfo.GlobalSettings.SnapshotSaveTick = tick;
				LobbyInfo.GlobalSettings.SnapshotSaveSyncHash = syncHash;
				savedLeft = position.Left;

				LobbyInfo.GlobalSettings.SnapshotSaveSlots = savedPeople.Values.Select(p => p.Slot).Where(s => s != null).ToImmutableArray();
			}
		}

		// The game starts at the net frame of position, with its packets in transit. It keeps no order log, which would
		// start mid-game.
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

		/// <summary>Forgets the save of the lobby, as when the map changes.</summary>
		public void ForgetGameSave()
		{
			lock (LobbyInfoLock)
			{
				GameSave = null;
				startFrame = 1;
				startPackets = [];
				snapshotSave = "";

				LobbyInfo.GlobalSettings.HoldsSnapshotSave = false;
				LobbyInfo.GlobalSettings.SnapshotSave = null;
				LobbyInfo.GlobalSettings.SnapshotSaveTick = 0;
				LobbyInfo.GlobalSettings.SnapshotSaveSyncHash = 0;
				LobbyInfo.GlobalSettings.SnapshotSaveSlots = [];
				savedPeople.Clear();
				copies.Clear();
				savedLeft = [];
				fileRequestedFrom = -1;
				ForgetSharedFile();
				foreach (var c in LobbyInfo.Clients)
					c.HasSaveCopy = false;
			}
		}

		void ForgetSharedFile()
		{
			sharedSave = null;
			upload = null;
			uploadFrom = -1;
			sentFileTo.Clear();
			LobbyInfo.GlobalSettings.SnapshotSaveFileHash = null;
			LobbyInfo.GlobalSettings.SnapshotSaveFileSize = 0;
		}

		/// <summary>Asks the admin once for the file of its copy, while the lobby loads from copies and holds no file.</summary>
		public void RequestSharedFile()
		{
			lock (LobbyInfoLock)
			{
				if (State != ServerState.WaitingPlayers || !LoadsFromCopies || sharedSave != null)
					return;

				var admin = LobbyInfo.Clients.FirstOrDefault(c => c.IsAdmin && c.Bot == null);
				var conn = admin == null ? null : Conns.FirstOrDefault(c => c.PlayerIndex == admin.Index);
				if (conn == null || !admin.HasSaveCopy || fileRequestedFrom == conn.PlayerIndex)
					return;

				fileRequestedFrom = conn.PlayerIndex;
				SendOrderTo(conn, SharedSave.UploadRequestOrder, SharedSave.MaxSize.ToStringInvariant());
			}
		}

		void ForgetSharedFileClient(int index)
		{
			sentFileTo.Remove(index);
			if (uploadFrom == index)
			{
				upload = null;
				uploadFrom = -1;
			}

			if (fileRequestedFrom == index)
				fileRequestedFrom = -1;
		}

		// The server takes a chunk from the admin only, before the start, and checks the whole file when it is here.
		void ReceiveSharedFileChunk(Connection conn, string chunk)
		{
			var client = GetClient(conn);
			var ignored = State >= ServerState.GameStarted ? "the game has started"
				: !LoadsFromCopies ? "the lobby holds no save that loads from copies"
				: sharedSave != null ? "the server holds the file"
				: client?.IsAdmin != true ? "the sender is not the admin"
				: null;

			if (ignored != null)
			{
				Log.Write("server", $"A chunk of a file from {client?.Name} is ignored: {ignored}");
				return;
			}

			if (upload == null || uploadFrom != conn.PlayerIndex)
			{
				upload = new SharedSave(SharedSave.MaxSize);
				uploadFrom = conn.PlayerIndex;
			}

			if (!upload.TryAdd(chunk, out var refusal))
			{
				RefuseSharedFile(conn, refusal);
				return;
			}

			if (!upload.IsComplete)
				return;

			var file = upload.Bytes;
			upload = null;
			uploadFrom = -1;
			try
			{
				GameSaveCopy.CheckedSnapshot(LobbyInfo.GlobalSettings, GameSave.FromBytes(file, "the file of the admin"));
			}
			catch (Exception e)
			{
				RefuseSharedFile(conn, e.Message);
				return;
			}

			sharedSave = file;
			LobbyInfo.GlobalSettings.SnapshotSaveFileHash = SharedSave.Hash(file);
			LobbyInfo.GlobalSettings.SnapshotSaveFileSize = file.Length;
			SyncLobbyGlobalSettings();
		}

		void RefuseSharedFile(Connection conn, string reason)
		{
			upload = null;
			uploadFrom = -1;
			Log.Write("server", $"The file of {GetClient(conn)?.Name} is refused: {reason}");
			SendFluentMessageTo(conn, SharedSaveNotTaken, ["reason", reason]);
		}

		// The server sends the file once to each connection, while it holds the file with that hash.
		void SendSharedFile(Connection conn, string hash)
		{
			if (State >= ServerState.GameStarted || sharedSave == null || hash != LobbyInfo.GlobalSettings.SnapshotSaveFileHash || !sentFileTo.Add(conn.PlayerIndex))
			{
				Log.Write("server", $"The request for a file from {GetClient(conn)?.Name} is ignored.");
				return;
			}

			foreach (var chunk in SharedSave.Split(sharedSave))
				SendOrderTo(conn, SharedSave.ChunkOrder, chunk);
		}

		// A copy places no person in a slot. Its packets in transit go to the person in the slot of its writer. Every client
		// loads the bytes of one file: the admin its own, each other person the file of the admin.
		void TakeCopy(Connection conn, string report)
		{
			var client = GetClient(conn);
			var isShared = sentFileTo.Contains(conn.PlayerIndex);
			if (!client.IsAdmin && !isShared)
			{
				Log.Write("server", $"The copy of {client.Name} is ignored: a person who is not the admin loads the file of the admin.");
				return;
			}

			OrderManagerPosition copy;
			try
			{
				copy = GameSaveCopy.PositionIn(report, LobbyInfo.GlobalSettings);
				if (copy.NetFrameNumber != startFrame)
					throw new InvalidDataException($"The copy is at net frame {copy.NetFrameNumber}, and the save at {startFrame}.");

				CheckStartPosition(copy);
				if (!copy.Left.SequenceEqual(savedLeft))
					throw new InvalidDataException($"The copy names the people {string.Join(", ", copy.Left)} who had left, and the save {string.Join(", ", savedLeft)}.");
			}
			catch (Exception e)
			{
				Log.Write("server", $"The copy of {client.Name} is not a copy of the save {snapshotSave}: {e.Message}");
				SendFluentMessageTo(conn, CopyOfAnotherSave);
				return;
			}

			client.HasSaveCopy = true;
			copies[conn.PlayerIndex] = (copy.Client, copy.Packets.Select(p => p.Orders).ToArray(), isShared);
			SyncLobbyClients();
			RequestSharedFile();
		}

		/// <summary>Puts <paramref name="client"/> in a free saved slot. It keeps its name, index and admin flag.</summary>
		public void SeatInSavedSlot(Session.Client client, string slot)
		{
			lock (LobbyInfoLock)
			{
				var isAdmin = client.IsAdmin;
				savedPeople.Values.First(p => p.Slot == slot).ApplyTo(client);
				client.IsAdmin = isAdmin;
			}
		}

		// The packets of its copy when the person sits where the writer sat, and none otherwise.
		internal byte[][] PacketsInTransit(Connection from)
		{
			if (!LoadsFromCopies)
				return startPackets;

			return SitsInWriterSeat(from) ? copies[from.PlayerIndex].Packets : [];
		}

		// In the slot of the writer, or among the spectators when the writer watched. The packets of a spectator carry the
		// orders of its bots, so a spectator takes those of its own copy only. The client applies the same rule.
		bool SitsInWriterSeat(Connection from)
		{
			var client = GetClient(from);
			if (client == null || !copies.TryGetValue(from.PlayerIndex, out var copy))
				return false;

			if (client.Slot != null)
				return savedPeople.TryGetValue(copy.SavedIndex, out var writer) && writer.Slot == client.Slot;

			return !copy.IsShared && !savedPeople.ContainsKey(copy.SavedIndex);
		}

		// The saved client index of the slot of client, or of the writer whose seat a spectator holds, else int.MaxValue.
		// The clients of the loaded game apply the orders of a net frame in this order, as the saved game did.
		int SavedIndexOf(Session.Client client)
		{
			var slot = client.Slot;
			if (slot != null)
				return savedPeople.Values.FirstOrDefault(p => p.Slot == slot) is { } person ? person.Index : int.MaxValue;

			var conn = Conns.FirstOrDefault(c => c.PlayerIndex == client.Index);
			return conn != null && SitsInWriterSeat(conn) ? copies[conn.PlayerIndex].SavedIndex : int.MaxValue;
		}

		static void CheckStartPosition(OrderManagerPosition position)
		{
			if (position.NetFrameNumber < 1)
				throw new InvalidDataException($"A game is never at net frame {position.NetFrameNumber}.");

			for (var i = 0; i < position.Packets.Length; i++)
				if (position.Packets[i].Frame != position.NetFrameNumber + i)
					throw new InvalidDataException($"Packet {i} is for frame {position.Packets[i].Frame}. "
						+ $"A game at net frame {position.NetFrameNumber} expects frame {position.NetFrameNumber + i}.");
		}

		bool SaveAllowsStart()
		{
			// Each saved slot needs a person, and each person needs a copy.
			if (LoadsFromCopies)
			{
				var missing = LobbyInfo.NonBotClients.Where(c => !c.HasSaveCopy).Select(c => c.Name)
					.Concat(LobbyInfo.EmptySavedSlots())
					.ToArray();

				if (missing.Length > 0)
				{
					SendFluentMessage(SaveNeedsEachPlayer, "players", string.Join(", ", missing));
					return false;
				}
			}

			// Any other saved position holds the packets of one client, which each connection would get.
			else if ((startFrame != 1 || startPackets.Length > 0 || snapshotSave.Length > 0) && LobbyInfo.NonBotClients.Count() > 1)
			{
				SendFluentMessage(SaveOfOnePlayer);
				return false;
			}

			return true;
		}
	}
}
