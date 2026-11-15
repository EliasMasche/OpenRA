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
		/// <summary>Why this client left the game before it started, as when it cannot load its save. Null otherwise.</summary>
		public string StartFailure { get; internal set; }

		/// <summary>
		/// Where the actors that the world of this game starts with come from. Whoever joins the game decides it,
		/// before the order that starts the game builds the world. That order decides it again when the server
		/// starts the game of a save that holds a snapshot.
		/// </summary>
		public StartupActors StartupActors { get; internal set; }

		/// <summary>
		/// Where the order manager is between two ticks, for a save. A local game gives everything, and so does
		/// a game on a server that one person plays. A replay gives its counters only, because its recording
		/// holds the orders. Any other connection holds orders this client cannot give back, so it throws a
		/// NotSupportedException.
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

		// A client keeps a packet that it has sent until the server acks it with the net frame it is applied on,
		// and then holds it in its queue until that frame. The server stamps the packets of a client in the order
		// they were sent, one net frame after the other. So the packets in transit follow each other from this net
		// frame: the acked ones, then the ones that wait for their ack. A position holds the packets of this client
		// only.
		OrderManagerPosition CapturePosition(NetworkConnection network)
		{
			// A position holds the orders in transit of this client only.
			if (LobbyInfo.NonBotClients.Count() > 1)
				throw new NotSupportedException("orders in transit cannot be saved from a game that more than one person plays.");

			// The world keeps the player of a person who left, and gives it no orders. A client takes note of the
			// disconnect on the same frame as every other client, so every copy names the same people.
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

		// Starts the game of the save that data of the order that starts a game names, and gives true. Gives false
		// for a game that does not go on from a snapshot.
		internal bool TryStartFromSnapshot(List<MiniYamlNode> data)
		{
			// A save that holds a snapshot holds no orders to apply. Its game starts where the snapshot was taken.
			var saveSnapshot = data.FirstOrDefault(n => n.Key == "SaveSnapshot");
			if (saveSnapshot == null)
				return false;

			// A save is a file of this player. A server that is not on this machine, or that others have
			// joined, has no say in which of them this client reads.
			if (Connection is NetworkConnection network && ServerMayNameSave(network.EndPoint, LobbyInfo))
				Game.StartGameFromSnapshot(LobbyInfo.GlobalSettings.Map, saveSnapshot.Value.Value);
			else
				Game.LeaveUnstartableGame("its server names a save, and it is not the server of this player's own game.");

			return true;
		}

		// The orders that are issued and not sent. Those that a restored game had not sent yet go out ahead of the
		// ones issued since.
		byte[] Unsent(OrderPacket restoredUnsent)
		{
			var unsent = new OrderPacket(localOrders);
			return (restoredUnsent != null ? OrderPacket.Combine([restoredUnsent, unsent]) : unsent).ToBytes();
		}

		/// <summary>
		/// Puts the order manager of a game that has just started where <see cref="CapturePosition()"/> found
		/// another. In a local game each net frame applies the packet of its own number, so the packets have to
		/// continue from the restored frame. A position without packets, as a replay gives, continues with
		/// an empty one, in place of the packet a new game starts with. A replay takes its orders from its
		/// recording, which then continues at the restored frame. A game on a server takes its packets from the
		/// server, which starts at the net frame of the position.
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

		// The server hands out the packets that were in transit, as the first packets of its game. They can be here
		// before the position is restored, so the queue stays as it is. Only the orders that were not sent go to
		// the connection, which sends them ahead of the next ones.
		void RestorePosition(NetworkConnection network, OrderManagerPosition position)
		{
			// A position holds the orders in transit of one client.
			if (LobbyInfo.NonBotClients.Count() > 1)
				throw new NotSupportedException("a position cannot be restored into a game that more than one person plays.");

			RestoreFrames(position, position.Packets);
			network.RestoreOrders(TakeUnsent(position));
		}

		// Sets the three counters of position. Each packet has to be of the client of the position, and they have
		// to follow each other from the net frame of the position. The client of a game that goes on from a save
		// can have another index than in the saved game.
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

		// Forgets the orders issued since, and gives the orders that the position had not sent, or null.
		OrderPacket TakeUnsent(OrderManagerPosition position)
		{
			localOrders.Clear();
			return position.LocalOrders.Length > 0 ? new OrderPacket(new MemoryStream(position.LocalOrders)) : null;
		}

		// The recording continues at the restored net frame. A packet of an earlier frame is dropped, also one that
		// was received before the restore: the game the position comes from has applied it. A client that left
		// before that frame sends nothing more, so no frame waits for it.
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
		/// Whether the server at <paramref name="server"/> may name a save for this client to load when it starts
		/// the game. A save is a file of this player, and it holds the game of one person. So only a server on this
		/// machine may, and only while nobody else is in its lobby. Any other server could make a client read a file
		/// of its own into a game that others play.
		/// </summary>
		public static bool ServerMayNameSave(IPEndPoint server, Session lobbyInfo)
		{
			return server != null && IPAddress.IsLoopback(server.Address) && lobbyInfo.NonBotClients.Count() == 1;
		}
	}
}
