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

using System.Linq;
using OpenRA.Primitives;
using OpenRA.Traits;

namespace OpenRA.Mods.Common.Traits
{
	[Desc("Allows bridges to be targeted for demolition and repair.")]
	public class LegacyBridgeHutInfo : TraitInfo, IDemolishableInfo, ISnapshotCreationInfo
	{
		public bool IsValidTarget(ActorInfo actorInfo, Actor saboteur) { return false; } // TODO: bridges don't support frozen under fog

		public override object Create(ActorInitializer init) { return new LegacyBridgeHut(init); }

		// The hut cannot start without its bridge, and its saved state names the bridge.
		void ISnapshotCreationInfo.AddSnapshotInits(TypeDictionary inits, Player owner, System.Func<string, string> saved, System.Func<string, Actor> savedActor)
		{
			var bridge = savedActor(nameof(LegacyBridgeHut.Bridge));
			if (bridge != null)
				inits.Add(new ParentActorInit(bridge));
		}
	}

	public class LegacyBridgeHut : IDemolishable, IDelayedDemolition
	{
		public Bridge FirstBridge { get; private set; }
		public Bridge Bridge { get; private set; }
		public DamageState BridgeDamageState => Bridge.AggregateDamageState();
		public bool Repairing => repairDirections > 0;
		int repairDirections = 0;

		public LegacyBridgeHut(ActorInitializer init)
		{
			var bridge = init.Get<ParentActorInit>().Value;
			init.World.AddFrameEndTask(_ =>
			{
				Bridge = bridge.Actor(init.World).Value.Trait<Bridge>();
				Bridge.AddHut(this);
				FirstBridge = Bridge.Enumerate(0, true).Last();
			});
		}

		public void Repair(Actor repairer)
		{
			repairDirections = Bridge.GetHut(0) != this && Bridge.GetHut(1) != this ? 2 : 1;
			Bridge.Do((b, d) => b.Repair(repairer, d, this));
		}

		// The bridge calls this when the repair gets to the end of one direction.
		internal void RepairDirectionCompleted()
		{
			repairDirections--;
		}

		bool IDemolishable.IsValidTarget(Actor self, Actor saboteur)
		{
			return BridgeDamageState != DamageState.Dead;
		}

		void IDemolishable.Demolish(Actor self, Actor saboteur, int delay, BitSet<DamageType> damageTypes)
		{
			// TODO: Handle using ITick
			self.World.Add(new DelayedDemolition(this, self, saboteur, damageTypes, delay));
		}

		void IDelayedDemolition.Demolish(Actor self, Actor saboteur, BitSet<DamageType> damageTypes)
		{
			if (self.IsDead)
				return;

			var modifiers = self.TraitsImplementing<IDamageModifier>()
				.Concat(self.Owner.PlayerActor.TraitsImplementing<IDamageModifier>())
				.Select(t => t.GetDamageModifier(self, null));

			if (Util.ApplyPercentageModifiers(100, modifiers) > 0)
				Bridge.Do((b, d) => b.Demolish(saboteur, d, damageTypes));
		}
	}
}
