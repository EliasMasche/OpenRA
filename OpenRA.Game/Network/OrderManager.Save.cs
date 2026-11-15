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
using System.Net;

namespace OpenRA.Network
{
	public sealed partial class OrderManager
	{
		[FluentReference("save")]
		const string NoCopyOfTheSave = "notification-no-copy-of-the-save";

		[FluentReference("save", "size")]
		const string AskingForTheSharedSave = "notification-asking-for-shared-save";

		[FluentReference("save", "size")]
		const string TookTheSharedSave = "notification-took-shared-save";

		[FluentReference("reason")]
		const string SharedSaveRefused = "notification-shared-save-refused";

		[FluentReference("size", "limit")]
		const string CopyTooLargeToShare = "notification-copy-too-large-to-share";

		// The shortest game time, in seconds, between two save orders of a game of more people.
		const int MinSecondsBetweenSaves = 5;

		/// <summary>Why this client left the game before it started, as when it cannot load its save. Null otherwise.</summary>
		public string StartFailure { get; internal set; }

		/// <summary>Where the actors of the world come from. The order that starts the game of a snapshot save sets it.</summary>
		public StartupActors StartupActors { get; internal set; }

		// Whether the last tick applied a save order of a game of more people. Every client applies it on the same net frame.
		bool savesAfterThisTick;

		int? lastSaveOrderTick;

		internal int SaveAllowedFromTick(World world)
		{
			return lastSaveOrderTick.HasValue ? lastSaveOrderTick.Value + TicksBetweenSaves(world) : 0;
		}

		static int TicksBetweenSaves(World world)
		{
			return MinSecondsBetweenSaves * 1000 / world.Timestep;
		}

		// The save that this client looked for its copy of, and the file that it reported, or "".
		string copyLookedFor = "";
		string copyReported = "";

		// Whether the copy that this client reported is the file that the server gave it, which another person wrote.
		bool reportedSharedFile;

		string sharedFileAskedFor = "";
		SharedSave sharedFile;

		// One chunk goes out for each call of TickImmediate, as a packet is at most Connection.MaxOrderLength.
		readonly Queue<string> chunksToUpload = [];

		/// <summary>The slots of the save, with the client of each in the saved game. The game start sets it.</summary>
		internal Dictionary<string, SlotClient> SavedSlotClients { get; set; }

		/// <summary>
		/// Where the order manager is between two ticks. A replay gives its counters only. A connection that holds orders
		/// this client cannot give back throws a NotSupportedException.
		/// </summary>
		public OrderManagerPosition CapturePosition()
		{
			if (Connection is ReplayConnection)
				return new OrderManagerPosition(Connection.LocalClientId, LocalFrameNumber, NetFrameNumber, sentOrdersFrame, [], [], []);

			if (Connection is NetworkConnection network)
				return CapturePosition(network);

			if (Connection is not EchoConnection echo)
				throw new NotSupportedException($"orders in transit cannot be saved from a {Connection.GetType().Name}.");

			var packets = new List<OrderManagerPosition.Packet>();
			foreach (var (clientId, queue) in pendingOrders)
				foreach (var (frame, orders) in queue)
					packets.Add(new OrderManagerPosition.Packet(clientId, frame, orders.ToBytes()));

			// The connection gives a packet back one frame after it sends the packet.
			foreach (var (frame, orders) in echo.SentOrders)
				packets.Add(new OrderManagerPosition.Packet(Connection.LocalClientId, frame + 1, orders.ToBytes()));

			return new OrderManagerPosition(Connection.LocalClientId, LocalFrameNumber, NetFrameNumber, sentOrdersFrame, [.. packets], Unsent(echo.UnsentOrders), []);
		}

		// The server stamps the packets of a client in order, one net frame after the other. So the packets in transit
		// follow each other from this net frame: first the acked ones, then those that wait for their ack. Each client
		// saves its own.
		OrderManagerPosition CapturePosition(NetworkConnection network)
		{
			// Writing a snapshot changes the layout of collections, so every client writes on the same tick or none does.
			if (LobbyInfo.NonBotClients.Count() > 1 && !savesAfterThisTick)
				throw new NotSupportedException("a game that more than one person plays is saved only after the tick of a save order, on which every client saves.");

			// Every client notes a disconnect on the same frame, so every copy names the same people.
			var left = LobbyInfo.Clients.Where(c => !c.IsBot && c.Slot != null && !pendingOrders.ContainsKey(c.Index)).Select(c => c.Index).Order();

			var localClient = Connection.LocalClientId;
			var packets = new List<OrderManagerPosition.Packet>();
			if (pendingOrders.TryGetValue(localClient, out var queue))
				foreach (var (frame, orders) in queue)
					packets.Add(new OrderManagerPosition.Packet(localClient, frame, orders.ToBytes()));

			var nextFrame = packets.Count > 0 ? packets[^1].Frame + 1 : NetFrameNumber;
			foreach (var (_, orders) in network.SentOrders)
				packets.Add(new OrderManagerPosition.Packet(localClient, nextFrame++, orders.ToBytes()));

			if (packets.Count > 0 && packets[0].Frame != NetFrameNumber)
				throw new InvalidOperationException($"The first packet in transit is for frame {packets[0].Frame}, and the game is at net frame {NetFrameNumber}.");

			return new OrderManagerPosition(localClient, LocalFrameNumber, NetFrameNumber, sentOrdersFrame, [.. packets], Unsent(network.UnsentOrders), [.. left]);
		}

		internal void ApplySaveOrder()
		{
			savesAfterThisTick = true;
		}

		// Every client applies the save order of the host on the same net frame, and every check reads shared state.
		internal void HandleSaveGameOrder(World world, Order order, int clientId)
		{
			var client = LobbyInfo.ClientWithIndex(clientId);
			if (world == null || world.IsReplay || client == null || !client.IsAdmin || !LobbyInfo.GlobalSettings.EnableGameSaves)
				return;

			if (!GameSaveCopy.IsCopyName(order.TargetString))
				return;

			// One save in MinSecondsBetweenSaves of game time at most. Every client drops the same order.
			if (world.WorldTick < SaveAllowedFromTick(world))
			{
				Log.Write("debug", $"The save order {order.TargetString} is dropped: the last one was at tick {lastSaveOrderTick}.");
				if (clientId != Connection.LocalClientId)
					return;

				world.NotifyGameNotSaved(order.ExtraData != 0);
				return;
			}

			lastSaveOrderTick = world.WorldTick;
			ApplySaveOrder();
			world.SaveAfterThisTick(order.TargetString, order.ExtraData != 0);
		}

		// Starts the game of the save that the start order names, and gives true. Gives false for any other game.
		internal bool TryStartFromSnapshot(List<MiniYamlNode> data)
		{
			var saveSnapshot = data.FirstOrDefault(n => n.Key == "SaveSnapshot");
			if (saveSnapshot == null)
				return false;

			// A server that is not on this machine, or that has other people, can only name the save of its lobby.
			if (ServerMayNameSave(saveSnapshot.Value.Value))
				Game.StartGameFromSnapshot(LobbyInfo.GlobalSettings.Map, copyReported.Length > 0 ? copyReported : saveSnapshot.Value.Value);
			else
				Game.LeaveUnstartableGame("its server names a save, and it is not the server of this player's own game.");

			return true;
		}

		// Orders that a restored game had not sent go out ahead of the ones issued since.
		byte[] Unsent(OrderPacket restoredUnsent)
		{
			var unsent = new OrderPacket(localOrders);
			return (restoredUnsent != null ? OrderPacket.Combine([restoredUnsent, unsent]) : unsent).ToBytes();
		}

		/// <summary>
		/// Puts the order manager of a game that has just started where <see cref="CapturePosition()"/> found another.
		/// A local game continues its packets from the restored frame, with an empty one when there are none. A replay
		/// continues its recording there. A server starts at the net frame of the position.
		/// </summary>
		public void RestorePosition(OrderManagerPosition position)
		{
			if (position.NetFrameNumber < 1 || position.LocalFrameNumber < 0)
				throw new InvalidDataException($"A game is never at net frame {position.NetFrameNumber} and local frame {position.LocalFrameNumber}.");

			if (Connection is ReplayConnection replay)
			{
				RestorePosition(replay, position);
				return;
			}

			if (Connection is NetworkConnection network)
			{
				RestorePosition(network, position);
				return;
			}

			if (Connection is not EchoConnection echo)
				throw new NotSupportedException($"orders in transit cannot be restored into a {Connection.GetType().Name}.");

			var localClient = Connection.LocalClientId;
			var packets = position.Packets;
			if (packets.Length == 0)
				packets = [new OrderManagerPosition.Packet(localClient, position.NetFrameNumber, [])];

			RestoreFrames(position, packets);
			echo.RestoreOrders(TakeUnsent(position));
			var queue = pendingOrders[localClient];
			queue.Clear();
			foreach (var packet in packets)
				queue.Enqueue((packet.Frame, new OrderPacket(new MemoryStream(packet.Orders))));
		}

		// The server hands out the packets in transit, maybe before this. Only the unsent orders go to the connection.
		void RestorePosition(NetworkConnection network, OrderManagerPosition position)
		{
			if (LobbyInfo.NonBotClients.Count() > 1 && copyReported.Length == 0)
				throw new NotSupportedException("a position cannot be restored into a game that more than one person plays, "
					+ "unless it is the copy that this client reported.");

			// A person who does not sit where the writer sat starts as in a new game, with no orders in transit.
			if (copyReported.Length > 0 && !SitsInWriterSeat(position))
				position = position with { Packets = [], LocalOrders = [] };

			RestoreFrames(position, position.Packets);
			network.RestoreOrders(TakeUnsent(position));
		}

		// Sets the three counters. The packets are of the client of the position and follow each other from its net frame.
		void RestoreFrames(OrderManagerPosition position, ImmutableArray<OrderManagerPosition.Packet> packets)
		{
			for (var i = 0; i < packets.Length; i++)
				if (packets[i].Client != position.Client || packets[i].Frame != position.NetFrameNumber + i)
					throw new InvalidDataException($"Packet {i} is of client {packets[i].Client} for frame {packets[i].Frame}. "
						+ $"A game at net frame {position.NetFrameNumber} expects client {position.Client} and frame {position.NetFrameNumber + i}.");

			LocalFrameNumber = position.LocalFrameNumber;
			NetFrameNumber = position.NetFrameNumber;
			sentOrdersFrame = position.SentOrdersFrame;
		}

		OrderPacket TakeUnsent(OrderManagerPosition position)
		{
			localOrders.Clear();
			return position.LocalOrders.Length > 0 ? new OrderPacket(new MemoryStream(position.LocalOrders)) : null;
		}

		// Whether this person sits where the writer of the copy sat: in its slot, or among the spectators. As on the server.
		bool SitsInWriterSeat(OrderManagerPosition position)
		{
			if (SavedSlotClients == null)
				return false;

			var slot = LocalClient?.Slot;
			if (slot != null)
				return SavedSlotClients.TryGetValue(slot, out var saved) && saved.Index == position.Client;

			return !reportedSharedFile && !SavedSlotClients.Values.Any(c => c.Index == position.Client);
		}

		// The recording continues at the restored net frame. This drops the earlier packets, and a client that left
		// sends no more.
		void RestorePosition(ReplayConnection replay, OrderManagerPosition position)
		{
			if (position.HasOrders)
				throw new InvalidDataException("The position holds orders, and a replay takes its orders from its recording.");

			RestoreFrames(position, []);

			var leftClients = replay.ContinueAt(NetFrameNumber).ToHashSet();
			foreach (var (clientId, queue) in pendingOrders)
			{
				var packets = queue.ToArray();
				queue.Clear();
				foreach (var packet in packets)
				{
					if (packet.Frame >= NetFrameNumber)
						queue.Enqueue(packet);
					else if (packet.Orders == ClientDisconnected)
						leftClients.Add(clientId);
				}
			}

			foreach (var clientId in leftClients)
			{
				pendingOrders.Remove(clientId);
				var client = LobbyInfo.ClientWithIndex(clientId);
				if (client != null)
					client.State = Session.ClientState.Disconnected;
			}
		}

		/// <summary>
		/// Whether a server may name a save for this client to load: only a server on this machine, while nobody else is in
		/// its lobby. Any other server could make a client read its own file into a game that others play.
		/// </summary>
		public static bool ServerMayNameSave(IPEndPoint server, Session lobbyInfo)
		{
			return server != null && IPAddress.IsLoopback(server.Address) && lobbyInfo.NonBotClients.Count() == 1;
		}

		// Also the save of the lobby of a game of more people, when this client reported a copy of it.
		internal bool ServerMayNameSave(string save)
		{
			if (copyReported.Length > 0)
				return save == LobbyInfo.GlobalSettings.SnapshotSave;

			return Connection is NetworkConnection network && ServerMayNameSave(network.EndPoint, LobbyInfo);
		}

		// The admin tells the server of its copy of the save that the lobby holds, once for each save. Every other client
		// asks the server for the file of the admin, and reports that file when it has it. So every client loads the same
		// bytes. The server does not start the game of the save while this client is in the lobby without a copy.
		internal void ReportSaveCopy()
		{
			var settings = LobbyInfo.GlobalSettings;
			var save = string.IsNullOrEmpty(settings.SnapshotSave) ? ""
				: $"{settings.GameUid} {settings.SnapshotSave} {settings.SnapshotSaveTick} {settings.SnapshotSaveSyncHash}";

			if (save != copyLookedFor)
			{
				copyLookedFor = save;
				copyReported = "";
				reportedSharedFile = false;
				sharedFileAskedFor = "";
				sharedFile = null;
				chunksToUpload.Clear();
				if (save.Length > 0 && LocalClient?.IsAdmin == true)
				{
					var report = GameSaveCopy.Report(settings, settings.SnapshotSave);
					if (report == null)
						TextNotificationsManager.AddSystemLine(NoCopyOfTheSave, "save", Path.GetFileNameWithoutExtension(settings.SnapshotSave));
					else
					{
						copyReported = settings.SnapshotSave;
						IssueOrder(Order.FromTargetString(GameSaveCopy.OrderString, report, true));
					}
				}
			}

			RequestSharedFile();
		}

		void RequestSharedFile()
		{
			var settings = LobbyInfo.GlobalSettings;
			var hash = settings.SnapshotSaveFileHash ?? "";
			if (copyLookedFor.Length == 0 || copyReported.Length > 0 || hash == sharedFileAskedFor)
				return;

			sharedFileAskedFor = hash;
			sharedFile = null;
			if (hash.Length == 0)
				return;

			var size = settings.SnapshotSaveFileSize;
			if (!SharedSave.IsHash(hash) || size <= 0 || size > SharedSave.MaxSize)
			{
				TextNotificationsManager.AddSystemLine(SharedSaveRefused, "reason", "the lobby names a file that no server shares.");
				return;
			}

			sharedFile = new SharedSave(size);
			TextNotificationsManager.AddSystemLine(AskingForTheSharedSave,
				"save", Path.GetFileNameWithoutExtension(settings.SnapshotSave), "size", (size + 1023) / 1024);
			IssueOrder(Order.FromTargetString(SharedSave.RequestOrder, hash, true));
		}

		// Checks the file when it is complete. The file is then the copy of this client.
		internal void ReceiveSharedFileChunk(string chunk)
		{
			if (sharedFile == null)
				return;

			if (!sharedFile.TryAdd(chunk, out var refusal))
			{
				sharedFile = null;
				TextNotificationsManager.AddSystemLine(SharedSaveRefused, "reason", refusal);
				return;
			}

			if (!sharedFile.IsComplete)
				return;

			var file = sharedFile.Bytes;
			sharedFile = null;
			var settings = LobbyInfo.GlobalSettings;
			try
			{
				SharedSave.Check(file, settings.SnapshotSaveFileHash, settings);
				StoreSharedFile(file, settings.SnapshotSaveFileHash);
			}
			catch (Exception e)
			{
				Log.Write("client", $"The file of the save {settings.SnapshotSave} from the server is refused: {e}");
				TextNotificationsManager.AddSystemLine(SharedSaveRefused, "reason", e.Message);
			}
		}

		void StoreSharedFile(byte[] file, string hash)
		{
			var settings = LobbyInfo.GlobalSettings;
			if (disposed || settings.SnapshotSaveFileHash != hash)
				return;

			try
			{
				// Written beside its name and then moved, as a save is.
				var name = SharedSave.FileName(settings.SnapshotSaveFileHash);
				var path = GameSaveCopy.SavePath(name);
				Directory.CreateDirectory(Path.GetDirectoryName(path));
				File.WriteAllBytes(path + ".tmp", file);
				File.Move(path + ".tmp", path, true);

				var report = GameSaveCopy.Report(settings, name);
				if (report == null)
				{
					File.Delete(path);
					throw new InvalidDataException("the file is not a copy of the save of the lobby.");
				}

				SharedSave.DeleteOlderFiles(Path.GetDirectoryName(path), name);
				copyReported = name;
				reportedSharedFile = true;
				IssueOrder(Order.FromTargetString(GameSaveCopy.OrderString, report, true));
				TextNotificationsManager.AddSystemLine(TookTheSharedSave,
					"save", Path.GetFileNameWithoutExtension(settings.SnapshotSave), "size", (file.Length + 1023) / 1024);
			}
			catch (Exception e)
			{
				Log.Write("client", $"The file of the save {settings.SnapshotSave} from the server is refused: {e}");
				TextNotificationsManager.AddSystemLine(SharedSaveRefused, "reason", e.Message);
			}
		}

		// The server asks for the file of the copy of the admin. One chunk goes out for each call of TickImmediate.
		internal void UploadCopy(string limitText)
		{
			chunksToUpload.Clear();
			if (GameStarted || copyReported.Length == 0 || !Exts.TryParseInt32Invariant(limitText, out var limit))
				return;

			var path = GameSaveCopy.SavePath(copyReported);
			try
			{
				var size = new FileInfo(path).Length;
				if (size == 0 || size > Math.Min(limit, SharedSave.MaxSize))
				{
					TextNotificationsManager.AddSystemLine(CopyTooLargeToShare, "size", (size + 1023) / 1024, "limit", (limit + 1023) / 1024);
					return;
				}

				var file = File.ReadAllBytes(path);
				foreach (var chunk in SharedSave.Split(file))
					chunksToUpload.Enqueue(chunk);
			}
			catch (Exception e) when (e is IOException or UnauthorizedAccessException)
			{
				Log.Write("client", $"The copy {copyReported} cannot be read to share it: {e.Message}");
			}
		}

		void SendNextUploadChunk()
		{
			if (!GameStarted && chunksToUpload.TryDequeue(out var chunk))
				IssueOrder(Order.FromTargetString(SharedSave.UploadOrder, chunk, true));
		}
	}
}
