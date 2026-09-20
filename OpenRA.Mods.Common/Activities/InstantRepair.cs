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
	public sealed class InstantRepair : Enter
	{
		const string EnterActorKey = "EnterActor";

		readonly InstantlyRepairsInfo info;

		Actor enterActor;
		IHealth enterHealth;
		InstantlyRepairable enterInstantlyRepariable;

		public InstantRepair(Actor self, in Target target, InstantlyRepairsInfo info, Color? targetLineColor)
			: base(self, target, targetLineColor)
		{
			this.info = info;
		}

		internal InstantRepair(Actor self, SnapshotReader r, MiniYaml yaml)
			: base(self, r, yaml)
		{
			info = self.Info.TraitInfoOrDefault<InstantlyRepairsInfo>();

			r.DeferActor(yaml.NodeWithKeyOrDefault(EnterActorKey).Value.Value, a =>
			{
				enterActor = a;
				enterHealth = a?.TraitOrDefault<IHealth>();
				enterInstantlyRepariable = a?.TraitOrDefault<InstantlyRepairable>();
			});
		}

		public override List<MiniYamlNode> SaveState(Actor self, SnapshotWriter w)
		{
			var nodes = base.SaveState(self, w);
			nodes.AddRange(
			[
				new(EnterActorKey, w.ActorRef(enterActor))
			]);
			return nodes;
		}

		protected override bool TryStartEnter(Actor self, Actor targetActor)
		{
			enterActor = targetActor;
			enterHealth = targetActor.TraitOrDefault<IHealth>();
			enterInstantlyRepariable = targetActor.TraitOrDefault<InstantlyRepairable>();

			// Make sure we can still repair the target before entering
			// (but not before, because this may stop the actor in the middle of nowhere)
			var stance = self.Owner.RelationshipWith(enterActor.Owner);
			if (enterHealth == null || enterHealth.DamageState == DamageState.Undamaged ||
				enterInstantlyRepariable == null || enterInstantlyRepariable.IsTraitDisabled ||
				!info.ValidRelationships.HasRelationship(stance))
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

			if (enterInstantlyRepariable.IsTraitDisabled)
				return;

			if (enterHealth.DamageState == DamageState.Undamaged)
				return;

			var stance = self.Owner.RelationshipWith(enterActor.Owner);
			if (!info.ValidRelationships.HasRelationship(stance))
				return;

			if (enterHealth.DamageState == DamageState.Undamaged)
				return;

			enterActor.InflictDamage(self, new Damage(-enterHealth.MaxHP));
			if (!string.IsNullOrEmpty(info.RepairSound))
				Game.Sound.Play(SoundType.World, info.RepairSound, enterActor.CenterPosition);

			if (info.EnterBehaviour == EnterBehaviour.Dispose)
				self.Dispose();
			else if (info.EnterBehaviour == EnterBehaviour.Suicide)
				self.Kill(self);
		}
	}
}
