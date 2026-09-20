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

namespace OpenRA.Mods.Common.Activities
{
	[SaveableActivity]
	public class RemoveSelf : Activity
	{
		public RemoveSelf() { }

		internal RemoveSelf(Actor _1, SnapshotReader _2, MiniYaml _3) { }

		public override List<MiniYamlNode> SaveState(Actor self, SnapshotWriter w)
		{
			return [];
		}

		public override bool Tick(Actor self)
		{
			if (IsCanceling) return true;
			self.Dispose();
			Cancel(self);
			return true;
		}
	}
}
