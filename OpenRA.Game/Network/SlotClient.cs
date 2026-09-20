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

using OpenRA.Primitives;

namespace OpenRA.Network
{
	public class SlotClient
	{
		public readonly Color Color;
		public readonly string Faction;
		public readonly int SpawnPoint;
		public readonly int Team;
		public readonly int Handicap;
		public readonly string Slot;
		public readonly string Bot;
		public readonly bool IsAdmin;

		public readonly string BotName;

		public SlotClient() { }

		public SlotClient(Session.Client client)
		{
			Color = client.Color;
			Faction = client.Faction;
			SpawnPoint = client.SpawnPoint;
			Team = client.Team;
			Handicap = client.Handicap;
			Slot = client.Slot;
			Bot = client.Bot;
			IsAdmin = client.IsAdmin;

			if (client.Bot != null)
				BotName = client.Name;
		}

		public void ApplyTo(Session.Client client)
		{
			client.Color = Color;
			client.Faction = Faction;
			client.SpawnPoint = SpawnPoint;
			client.Team = Team;
			client.Handicap = Handicap;
			client.Slot = Slot;
			client.Bot = Bot;
			client.IsAdmin = IsAdmin;

			if (Bot != null)
				client.Name = BotName;
		}

		public static SlotClient Deserialize(MiniYaml data)
		{
			return FieldLoader.Load<SlotClient>(data);
		}

		public MiniYamlNode Serialize(string key)
		{
			return new MiniYamlNode($"SlotClient@{key}", FieldSaver.Save(this));
		}
	}
}
