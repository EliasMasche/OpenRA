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

using System.Collections.Immutable;
using System.Linq;

namespace OpenRA.Network
{
	/// <summary>
	/// Where an order manager is between two ticks: its frame counters, and the issued orders that did not apply yet.
	/// The orders stay bytes, and find their actors by ID when they apply, as in a game that never saved.
	/// <see cref="Client"/> is the client of the order manager. In a game on a server, each client holds only its own
	/// packets. <see cref="Left"/> holds the client index of each person who left a slot. The world keeps that player,
	/// and a load holds the slot for a new person.
	/// </summary>
	public sealed record OrderManagerPosition(
		int Client,
		int LocalFrameNumber,
		int NetFrameNumber,
		int SentOrdersFrame,
		ImmutableArray<OrderManagerPosition.Packet> Packets,
		byte[] LocalOrders,
		ImmutableArray<int> Left)
	{
		/// <summary>Whether any issued order did not apply yet. An empty packet only paces the frames.</summary>
		public bool HasOrders => LocalOrders.Length > 0 || Packets.Any(p => p.Orders.Length > 0);

		public readonly record struct Packet(int Client, int Frame, byte[] Orders);
	}
}
