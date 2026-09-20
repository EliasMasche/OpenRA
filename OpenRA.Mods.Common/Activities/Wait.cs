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
using OpenRA.Activities;
using OpenRA.GameSaves;

namespace OpenRA.Mods.Common.Activities
{
	[SaveableActivity]
	public class Wait : Activity
	{
		const string RemainingTicksKey = "RemainingTicks";

		int remainingTicks;

		public Wait(int period) { remainingTicks = period; }
		public Wait(int period, bool interruptible)
		{
			remainingTicks = period;
			IsInterruptible = interruptible;
		}

		protected Wait(Actor self, SnapshotReader r, MiniYaml yaml)
		{
			remainingTicks = FieldLoader.GetValue<int>(RemainingTicksKey, yaml.NodeWithKeyOrDefault(RemainingTicksKey)?.Value.Value);
		}

		public override bool Tick(Actor self)
		{
			if (IsCanceling)
				return true;

			return remainingTicks-- == 0;
		}

		public override List<MiniYamlNode> SaveState(Actor self, SnapshotWriter w)
		{
			return [new(RemainingTicksKey, FieldSaver.FormatValue(remainingTicks))];
		}
	}

	public class WaitFor : Activity
	{
		readonly Func<bool> f;

		public WaitFor(Func<bool> f) { this.f = f; }
		public WaitFor(Func<bool> f, bool interruptible)
		{
			this.f = f;
			IsInterruptible = interruptible;
		}

		public override bool Tick(Actor self)
		{
			if (IsCanceling)
				return true;

			return f == null || f();
		}
	}
}
