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
using OpenRA.Mods.Common.Traits;
using OpenRA.Primitives;
using OpenRA.Traits;

namespace OpenRA.Mods.Common.Activities
{
	[SaveableActivity]
	sealed class DonateExperience : Enter
	{
		const string EnterActorKey = "EnterActor";

		readonly int level;
		readonly int playerExperience;

		Actor enterActor;
		GainsExperience enterGainsExperience;

		public DonateExperience(Actor self, in Target target, int level, int playerExperience, Color? targetLineColor)
			: base(self, target, targetLineColor)
		{
			this.level = level;
			this.playerExperience = playerExperience;
		}

		internal DonateExperience(Actor self, SnapshotReader r, MiniYaml yaml)
			: base(self, r, yaml)
		{
			var n = yaml.ToDictionary();
			level = FieldLoader.GetValue<int>("Level", n["Level"].Value);
			playerExperience = FieldLoader.GetValue<int>("PlayerExperience", n["PlayerExperience"].Value);

			r.DeferActor(yaml.NodeWithKeyOrDefault(EnterActorKey).Value.Value, a =>
			{
				enterActor = a;
				enterGainsExperience = a?.TraitOrDefault<GainsExperience>();
			});
		}

		public override List<MiniYamlNode> SaveState(Actor self, SnapshotWriter w)
		{
			var nodes = base.SaveState(self, w);
			nodes.AddRange(
			[
				new(EnterActorKey, w.ActorRef(enterActor)),
				new("Level", FieldSaver.FormatValue(level)),
				new("PlayerExperience", FieldSaver.FormatValue(playerExperience))
			]);
			return nodes;
		}

		protected override bool TryStartEnter(Actor self, Actor targetActor)
		{
			enterActor = targetActor;
			enterGainsExperience = targetActor.TraitOrDefault<GainsExperience>();

			if (enterGainsExperience == null || enterGainsExperience.Level == enterGainsExperience.MaxLevel)
			{
				Cancel(self, true);
				return false;
			}

			return true;
		}

		protected override void OnEnterComplete(Actor self, Actor targetActor)
		{
			// Make sure the target hasn't changed while entering
			// OnEnterComplete is only called if targetActor is alive
			if (targetActor != enterActor)
				return;

			if (enterGainsExperience.Level == enterGainsExperience.MaxLevel)
				return;

			enterGainsExperience.GiveLevels(level);

			var exp = self.Owner.PlayerActor.TraitOrDefault<PlayerExperience>();
			if (exp != null && enterActor.Owner != self.Owner)
				exp.GiveExperience(playerExperience);

			self.Dispose();
		}
	}
}
