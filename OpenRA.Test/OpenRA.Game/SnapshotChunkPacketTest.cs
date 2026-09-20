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
using NUnit.Framework;
using OpenRA.Network;

namespace OpenRA.Test
{
	[TestFixture]
	sealed class SnapshotChunkPacketTest
	{
		static int PacketLength(IEnumerable<Order> orders)
		{
			var length = 4;
			foreach (var o in orders)
				length += o.Serialize().Length;

			return length;
		}

		static List<Order> ChunkOrders(int count)
		{
			var chunk = new string('A', BlobTransfer.ChunkLength);
			var orders = new List<Order>();
			for (var i = 0; i < count; i++)
				orders.Add(Order.FromTargetString("SnapshotChunk", chunk, true, (uint)i));

			return orders;
		}

		[TestCase(TestName = "One chunk per packet fits, with room for the rest of the tick")]
		public void OneChunkPerPacketFits()
		{
			var end = new List<MiniYamlNode>
			{
				new("Count", "512"),
				new("Length", "26214400"),
				new("Hash", new string('0', 40)),
				new("Filename", "quicksave-2026-09-07T000000Z.orasav")
			};

			var orders = ChunkOrders(1);
			orders.Add(Order.FromTargetString("SnapshotChunkEnd", end.WriteToString(), true, 1));

			Assert.That(PacketLength(orders), Is.LessThanOrEqualTo(OpenRA.Server.Connection.MaxOrderLength / 2));
		}

		[TestCase(TestName = "Three chunks in one packet would exceed the limit")]
		public void ThreeChunksWouldNotFit()
		{
			Assert.That(
				PacketLength(ChunkOrders(3)),
				Is.GreaterThan(OpenRA.Server.Connection.MaxOrderLength),
				"Three chunks now fit one packet. Re-derive the pacing in OrderManager.SendImmediateOrders " +
				"before relaxing it: the limit is enforced by closing the socket, with no error.");
		}

		[TestCase(TestName = "No single chunk can overflow a packet on its own")]
		public void NoChunkOverflowsAlone()
		{
			var random = new Random(12345);
			var payload = new byte[4 * 1024 * 1024];
			random.NextBytes(payload);

			var parts = BlobTransfer.Split(payload);
			Assume.That(parts.Chunks.Count, Is.GreaterThan(2), "Payload must span several chunks.");

			foreach (var chunk in parts.Chunks)
			{
				var order = Order.FromTargetString("SnapshotChunk", chunk, true, 0);
				Assert.That(PacketLength([order]), Is.LessThanOrEqualTo(OpenRA.Server.Connection.MaxOrderLength));
			}
		}
	}
}
