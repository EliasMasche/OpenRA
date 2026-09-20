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
using System.Linq;
using NUnit.Framework;
using OpenRA.Network;

namespace OpenRA.Test
{
	[TestFixture]
	sealed class SnapshotUploadOrderTest
	{
		sealed class RecordingConnection : IConnection
		{
			public readonly List<List<Order>> Packets = [];

			int IConnection.LocalClientId => 1;
			void IConnection.StartGame() { }
			void IConnection.Send(int frame, IEnumerable<Order> orders) { }
			void IConnection.SendSync(int frame, int syncHash, ulong defeatState) { }
			void IConnection.Receive(OrderManager orderManager) { }
			public void Dispose() { }

			void IConnection.SendImmediate(IEnumerable<Order> orders)
			{
				Packets.Add([.. orders]);
			}
		}

		static byte[] Incompressible(int length)
		{
			var payload = new byte[length];
			new System.Random(12345).NextBytes(payload);
			return payload;
		}

		static List<List<Order>> Upload(int payloadLength)
		{
			var connection = new RecordingConnection();
			using var orderManager = new OrderManager(connection);

			orderManager.UploadSnapshotThenLoad(Incompressible(payloadLength), "save.orasav");

			for (var i = 0; i < 64; i++)
				orderManager.TickImmediate();

			return connection.Packets;
		}

		[TestCase(TestName = "The load is issued after every chunk of its own upload")]
		public void LoadFollowsTheChunks()
		{
			var sent = Upload(4 * BlobTransfer.ChunkLength).SelectMany(p => p).ToList();

			Assert.That(sent.Count, Is.GreaterThan(3));
			Assert.That(sent[^1].OrderString, Is.EqualTo("LoadGameSave"), "The load must be the last order sent.");
			Assert.That(sent[^2].OrderString, Is.EqualTo("SnapshotChunkEnd"), "The load must follow the end marker.");
			Assert.That(sent.Take(sent.Count - 2).All(o => o.OrderString == "SnapshotChunk"), Is.True);
		}

		[TestCase(TestName = "Each chunk is sent in a packet of its own")]
		public void ChunksAreNotBatched()
		{
			var packets = Upload(4 * BlobTransfer.ChunkLength);

			foreach (var packet in packets)
				Assert.That(packet.Count(o => o.OrderString == "SnapshotChunk"), Is.LessThanOrEqualTo(1));
		}
	}
}
