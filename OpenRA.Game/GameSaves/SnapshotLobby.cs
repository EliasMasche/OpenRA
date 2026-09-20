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
using System.IO;
using System.Linq;
using OpenRA.Network;

namespace OpenRA.GameSaves
{
	public sealed class SnapshotLobby
	{
		const string GlobalSettingsKey = "GlobalSettings";
		const string SlotsKey = "Slots";
		const string SlotClientsKey = "SlotClients";
		const string MapGenerationArgsKey = "MapGenerationArgs";

		public Session.Global GlobalSettings { get; private set; }
		public Dictionary<string, Session.Slot> Slots { get; private set; }
		public Dictionary<string, SlotClient> SlotClients { get; private set; }
		public MapGenerationArgs MapGenerationArgs { get; private set; }

		SnapshotLobby() { }

		public static SnapshotLobby FromSession(Session lobbyInfo, MapPreview map)
		{
			ArgumentNullException.ThrowIfNull(lobbyInfo);
			ArgumentNullException.ThrowIfNull(map);

			var lobby = new SnapshotLobby
			{
				GlobalSettings = Session.Global.Deserialize(lobbyInfo.GlobalSettings.Serialize().Value),
				Slots = [],
				SlotClients = [],
				MapGenerationArgs = map.Class == MapClassification.Generated ? map.GenerationArgs : null
			};

			foreach (var s in lobbyInfo.Slots)
			{
				lobby.Slots[s.Key] = Session.Slot.Deserialize(s.Value.Serialize().Value);

				var playerReference = map.Players.Players[s.Value.PlayerReference];
				var client = lobbyInfo.ClientInSlot(s.Key);
				if (playerReference.Playable && client != null)
					lobby.SlotClients[s.Key] = new SlotClient(client);
			}

			return lobby;
		}

		public void Write(SnapshotWriter w)
		{
			ArgumentNullException.ThrowIfNull(w);

			var nodes = new List<MiniYamlNode>
			{
				new(GlobalSettingsKey, new MiniYaml("", [GlobalSettings.Serialize()])),
				new(SlotsKey, new MiniYaml("", Slots.Values.Select(s => s.Serialize()).ToList())),
				new(SlotClientsKey, new MiniYaml("", SlotClients.Select(kv => kv.Value.Serialize(kv.Key)).ToList()))
			};

			if (MapGenerationArgs != null)
				nodes.Add(new MiniYamlNode(MapGenerationArgsKey, FieldSaver.Save(MapGenerationArgs)));

			w.WriteYamlSection(WorldRestorer.LobbySection, nodes);
		}

		public static SnapshotLobby Read(SnapshotReader r)
		{
			ArgumentNullException.ThrowIfNull(r);

			if (!r.HasSection(WorldRestorer.LobbySection))
				return null;

			var nodes = new MiniYaml("", r.ReadYamlSection(WorldRestorer.LobbySection) ?? []);
			var lobby = new SnapshotLobby { Slots = [], SlotClients = [] };

			var globalSettings = nodes.NodeWithKeyOrDefault(GlobalSettingsKey);
			if (globalSettings == null || globalSettings.Value.Nodes.Length == 0)
				throw new InvalidDataException("Snapshot lobby section has no global settings.");

			lobby.GlobalSettings = Session.Global.Deserialize(globalSettings.Value.Nodes[0].Value);

			var slots = nodes.NodeWithKeyOrDefault(SlotsKey);
			if (slots != null)
			{
				foreach (var n in slots.Value.Nodes)
				{
					var slot = Session.Slot.Deserialize(n.Value);
					lobby.Slots[slot.PlayerReference] = slot;
				}
			}

			var slotClients = nodes.NodeWithKeyOrDefault(SlotClientsKey);
			if (slotClients != null)
			{
				foreach (var n in slotClients.Value.Nodes)
					lobby.SlotClients[n.Key[(n.Key.IndexOf('@') + 1)..]] = SlotClient.Deserialize(n.Value);
			}

			var mapGenerationArgs = nodes.NodeWithKeyOrDefault(MapGenerationArgsKey);
			if (mapGenerationArgs != null)
				lobby.MapGenerationArgs = FieldLoader.Load<MapGenerationArgs>(mapGenerationArgs.Value);

			return lobby;
		}
	}
}
