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
using OpenRA.GameSaves;
using OpenRA.Traits;

namespace OpenRA.Mods.Common.Traits
{
	[TraitLocation(SystemActors.Player)]
	[Desc("This trait can be used to track player experience based on units killed with the `" + nameof(GivesExperience) + "` trait.",
		"It can also be used as a point score system in scripted maps, for example.",
		"Attach this to the player actor.")]
	public class PlayerExperienceInfo : TraitInfo
	{
		public override object Create(ActorInitializer init) { return new PlayerExperience(this); }
	}

	public class PlayerExperience : ISync, ISaveState
	{
		const string ExperienceKey = "Experience";

		readonly PlayerExperienceInfo info;

		[VerifySync]
		public int Experience { get; private set; }

		public PlayerExperience(PlayerExperienceInfo info)
		{
			this.info = info;
		}

		public void GiveExperience(int num)
		{
			Experience += num;
		}

		TraitInfo ISaveState.SaveStateInfo => info;

		List<MiniYamlNode> ISaveState.SaveState(Actor self, SnapshotWriter w)
		{
			if (Experience == 0)
				return null;

			return [new(ExperienceKey, FieldSaver.FormatValue(Experience))];
		}

		void ISaveState.LoadState(Actor self, MiniYaml data, SnapshotReader r)
		{
			var node = data.NodeWithKeyOrDefault(ExperienceKey);
			if (node != null)
				Experience = FieldLoader.GetValue<int>(ExperienceKey, node.Value.Value);
		}
	}
}
