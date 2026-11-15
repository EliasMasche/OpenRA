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
using System.Linq;
using OpenRA.Network;

namespace OpenRA.Snapshot
{
	// The frame counters of the order manager and the orders in transit, which wait up to two net frames.
	static class OrdersSection
	{
		/// <summary>
		/// Writes the orders as the bytes that the engine sends. Refuses a game whose orders it cannot give back.
		/// </summary>
		public static List<MiniYamlNode> Write(OrderManager orderManager)
		{
			OrderManagerPosition position;
			try
			{
				position = orderManager.CapturePosition();
			}
			catch (NotSupportedException e)
			{
				throw new SnapshotRefusedException(e.Message);
			}

			var packets = position.Packets.Select((packet, i) => new MiniYamlNode(FieldSaver.FormatValue(i), new MiniYaml("",
			[
				new MiniYamlNode(SnapshotFormat.Keys.Client, FieldSaver.FormatValue(packet.Client)),
				new MiniYamlNode(SnapshotFormat.Keys.PacketFrame, FieldSaver.FormatValue(packet.Frame)),
				new MiniYamlNode(SnapshotFormat.Keys.PacketOrders, Convert.ToBase64String(packet.Orders)),
			])));

			return
			[
				new(SnapshotFormat.Keys.Client, FieldSaver.FormatValue(position.Client)),
				new(SnapshotFormat.Keys.LocalFrame, FieldSaver.FormatValue(position.LocalFrameNumber)),
				new(SnapshotFormat.Keys.NetFrame, FieldSaver.FormatValue(position.NetFrameNumber)),
				new(SnapshotFormat.Keys.SentOrdersFrame, FieldSaver.FormatValue(position.SentOrdersFrame)),
				new(SnapshotFormat.Keys.Packets, new MiniYaml("", packets.ToList())),
				new(SnapshotFormat.Keys.LocalOrders, Convert.ToBase64String(position.LocalOrders)),
				new(SnapshotFormat.Keys.Left, FieldSaver.FormatValue(position.Left.ToArray())),
			];
		}

		/// <summary>Puts the order manager where the saved one was. The orders stay bytes.</summary>
		public static void Read(OrderManager orderManager, MiniYaml section)
		{
			orderManager.RestorePosition(ReadPosition(section));
		}

		/// <summary>The position in the save file <paramref name="snapshot"/>. PERF: reads only this section.</summary>
		public static OrderManagerPosition PositionIn(byte[] snapshot)
		{
			return ReadPosition(SnapshotFile.Read(snapshot).Section(SnapshotFormat.Sections.Orders));
		}

		internal static OrderManagerPosition ReadPosition(MiniYaml section)
		{
			var packets = section.NodeWithKey(SnapshotFormat.Keys.Packets).Value.Nodes.Select(node =>
			{
				var path = $"{SnapshotFormat.Sections.Orders}.{SnapshotFormat.Keys.Packets}[{node.Key}]";
				return new OrderManagerPosition.Packet(
					SnapshotReader.Parse<int>(node.Value.NodeWithKey(SnapshotFormat.Keys.Client).Value.Value, path),
					SnapshotReader.Parse<int>(node.Value.NodeWithKey(SnapshotFormat.Keys.PacketFrame).Value.Value, path),
					Convert.FromBase64String(node.Value.NodeWithKey(SnapshotFormat.Keys.PacketOrders).Value.Value ?? ""));
			});

			var left = section.NodeWithKeyOrDefault(SnapshotFormat.Keys.Left)?.Value.Value;
			return new OrderManagerPosition(
				Number(section, SnapshotFormat.Keys.Client),
				Number(section, SnapshotFormat.Keys.LocalFrame),
				Number(section, SnapshotFormat.Keys.NetFrame),
				Number(section, SnapshotFormat.Keys.SentOrdersFrame),
				[.. packets],
				Convert.FromBase64String(section.NodeWithKey(SnapshotFormat.Keys.LocalOrders).Value.Value ?? ""),
				string.IsNullOrEmpty(left) ? [] : [.. SnapshotReader.Parse<int[]>(left, $"{SnapshotFormat.Sections.Orders}.{SnapshotFormat.Keys.Left}")]);
		}

		static int Number(MiniYaml section, string key)
		{
			return SnapshotReader.Parse<int>(section.NodeWithKey(key).Value.Value, $"{SnapshotFormat.Sections.Orders}.{key}");
		}
	}
}
