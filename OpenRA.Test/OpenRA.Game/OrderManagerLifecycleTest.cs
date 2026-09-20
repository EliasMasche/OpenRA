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

using System.Collections.Generic;
using NUnit.Framework;
using OpenRA.Network;

namespace OpenRA.Test
{
	[TestFixture]
	sealed class OrderManagerLifecycleTest
	{
		sealed class NullConnection : IConnection
		{
			const int ClientId = 1;

			int IConnection.LocalClientId => ClientId;
			void IConnection.StartGame() { }
			void IConnection.Send(int frame, IEnumerable<Order> orders) { }
			void IConnection.SendSync(int frame, int syncHash, ulong defeatState) { }
			void IConnection.Receive(OrderManager orderManager) { }
			void IConnection.SendImmediate(IEnumerable<Order> orders) { }
			public void Dispose() { }
		}

		static OrderManager CreateOrderManager()
		{
			var connection = new NullConnection();
			var orderManager = new OrderManager(connection);
			orderManager.LobbyInfo.Clients.Add(new Session.Client { Index = ((IConnection)connection).LocalClientId });

			return orderManager;
		}

		static OrderPacket Packet() => new([]);

		[TestCase(TestName = "EndGame resets the local pacing but keeps the shared frame numbering")]
		public void EndGameKeepsTheFrameNumber()
		{
			using var orderManager = CreateOrderManager();

			orderManager.StartGame();
			var frameWhenStarted = orderManager.NetFrameNumber;

			Assert.That(frameWhenStarted, Is.Not.Zero, "The first StartGame did not open a timeline.");

			orderManager.EndGame();

			Assert.That(orderManager.LocalFrameNumber, Is.Zero,
				"EndGame left the local frame number addressing the discarded game.");

			Assert.That(orderManager.NetFrameNumber, Is.EqualTo(frameWhenStarted),
				"EndGame rewound the frame number, which the server does not do — the two would disagree " +
				"about which frame is being played for the rest of the session.");

			Assert.That(orderManager.GameStarted, Is.True,
				"EndGame left the session looking like a fresh connection, so a second StartGame would " +
				"reset the frame numbering the server is still using.");
		}

		[TestCase(TestName = "EndGame leaves the client able to take orders for the game that replaces it")]
		public void EndGameKeepsTheClientReceivable()
		{
			using var orderManager = CreateOrderManager();

			orderManager.StartGame();

			var localClientId = orderManager.Connection.LocalClientId;

			Assert.DoesNotThrow(() => orderManager.ReceiveOrders(localClientId, (1, Packet())),
				"A started game must accept an order packet from a client that is in the lobby.");

			orderManager.EndGame();

			Assert.DoesNotThrow(() => orderManager.ReceiveOrders(localClientId, (1, Packet())),
				"EndGame unregistered a client that is still in the session.");
		}
	}
}
