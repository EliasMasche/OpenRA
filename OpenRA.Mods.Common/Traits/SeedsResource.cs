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
using OpenRA.GameSaves;
using OpenRA.Traits;

namespace OpenRA.Mods.Common.Traits
{
	[Desc("Lets the actor spread resources around it in a circle.")]
	sealed class SeedsResourceInfo : ConditionalTraitInfo
	{
		public readonly int Interval = 75;
		public readonly string ResourceType = "Ore";
		public readonly int MaxRange = 100;

		public override object Create(ActorInitializer init) { return new SeedsResource(init.Self, this); }
	}

	sealed class SeedsResource : ConditionalTrait<SeedsResourceInfo>, ITick, ISeedableResource, ISaveState
	{
		const string TicksKey = "Ticks";

		readonly SeedsResourceInfo info;
		readonly IResourceLayer resourceLayer;

		public SeedsResource(Actor self, SeedsResourceInfo info)
			: base(info)
		{
			this.info = info;
			resourceLayer = self.World.WorldActor.Trait<IResourceLayer>();
		}

		int ticks;

		TraitInfo ISaveState.SaveStateInfo => info;

		List<MiniYamlNode> ISaveState.SaveState(Actor self, SnapshotWriter w)
		{
			return [new(TicksKey, FieldSaver.FormatValue(ticks))];
		}

		void ISaveState.LoadState(Actor self, MiniYaml data, SnapshotReader r)
		{
			var node = data.NodeWithKeyOrDefault(TicksKey);
			if (node != null)
				ticks = FieldLoader.GetValue<int>(TicksKey, node.Value.Value);
		}

		void ITick.Tick(Actor self)
		{
			if (IsTraitDisabled)
				return;

			if (--ticks <= 0)
			{
				Seed(self);
				ticks = info.Interval;
			}
		}

		public void Seed(Actor self)
		{
			var cell = Util.RandomWalk(self.Location, self.World.SharedRandom)
				.Take(info.MaxRange)
				.SkipWhile(p => resourceLayer.GetResource(p).Type == info.ResourceType && !resourceLayer.CanAddResource(info.ResourceType, p))
				.Cast<CPos?>().FirstOrDefault();

			if (cell != null && resourceLayer.CanAddResource(info.ResourceType, cell.Value))
				resourceLayer.AddResource(info.ResourceType, cell.Value);
		}
	}
}
