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
using OpenRA.Activities;
using OpenRA.GameSaves;
using OpenRA.Mods.Common.Traits;

namespace OpenRA.Mods.Common.Activities
{
	[SaveableActivity]
	public class SimpleTeleport : Activity
	{
		const string DestinationKey = "Destination";

		readonly CPos destination;

		public SimpleTeleport(CPos destination) { this.destination = destination; }

		internal SimpleTeleport(Actor _1, SnapshotReader _2, MiniYaml yaml)
		{
			destination = FieldLoader.GetValue<CPos>(DestinationKey, yaml.NodeWithKeyOrDefault(DestinationKey).Value.Value);
		}

		public override List<MiniYamlNode> SaveState(Actor self, SnapshotWriter w)
		{
			return [new(DestinationKey, FieldSaver.FormatValue(destination))];
		}

		public override bool Tick(Actor self)
		{
			self.Trait<IPositionable>().SetPosition(self, destination);
			self.Generation++;
			return true;
		}
	}
}
