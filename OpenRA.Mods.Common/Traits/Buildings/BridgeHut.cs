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
using System.Collections.Immutable;
using System.Linq;
using OpenRA.GameSaves;
using OpenRA.Primitives;
using OpenRA.Traits;

namespace OpenRA.Mods.Common.Traits
{
	[Desc("Allows bridges to be targeted for demolition and repair.")]
	public class BridgeHutInfo : TraitInfo, IDemolishableInfo
	{
		[Desc("Bridge types to act on")]
		public readonly ImmutableArray<string> Types = ["GroundLevelBridge"];

		[Desc("Offsets to look for adjacent bridges to act on")]
		public readonly ImmutableArray<CVec> NeighbourOffsets = [];

		[Desc("Delay between each segment repair step")]
		public readonly int RepairPropagationDelay = 20;

		[Desc("Delay between each segment demolish step")]
		public readonly int DemolishPropagationDelay = 5;

		[Desc("Hide the repair cursor if the bridge is only damaged (not destroyed)")]
		public readonly bool RequireForceAttackForHeal = false;

		public bool IsValidTarget(ActorInfo actorInfo, Actor saboteur) { return false; } // TODO: bridges don't support frozen under fog

		public override object Create(ActorInitializer init) { return new BridgeHut(init.World, this); }
	}

	public class BridgeHut : INotifyCreated, IDemolishable, ITick, ISaveState
	{
		const string RepairStepKey = "RepairStep";
		const string RepairDelayKey = "RepairDelay";
		const string RepairRepairerKey = "RepairRepairer";
		const string DemolishStepKey = "DemolishStep";
		const string DemolishDelayKey = "DemolishDelay";
		const string DemolishSaboteurKey = "DemolishSaboteur";
		const string DemolishDamageTypesKey = "DemolishDamageTypes";
		const string DemolishTriggerKey = "DemolishTrigger";
		const string DemolishTriggerDelayKey = "DemolishTriggerDelay";
		const string DemolishTriggerDamageTypesKey = "DemolishTriggerDamageTypes";

		public readonly BridgeHutInfo Info;
		readonly BridgeLayer bridgeLayer;

		// Fixed at map load
		readonly List<CPos[]> segmentLocations = [];

		// Changes as segments are killed and repaired
		readonly Dictionary<CPos, IBridgeSegment> segments = [];
		readonly HashSet<CPos> dirtyLocations = [];

		// Enabled during a repair action
		int repairStep;
		int repairDelay;
		Actor repairRepairer;

		// Enabled during a demolish action
		int demolishStep;
		int demolishDelay;
		Actor demolishSaboteur;
		BitSet<DamageType> demolishDamageTypes;

		int demolishTriggerDelay = -1;
		Actor demolishTrigger;
		BitSet<DamageType> demolishTriggerDamageTypes;

		public BridgeHut(World world, BridgeHutInfo info)
		{
			Info = info;
			bridgeLayer = world.WorldActor.Trait<BridgeLayer>();
		}

		void INotifyCreated.Created(Actor self)
		{
			self.World.AddFrameEndTask(w =>
			{
				// Bridge segments and huts are expected to be placed in the map
				// editor or spawned during the normal actor loading
				//
				// The number and location of bridge segments are calculated here,
				// and assumed to not change for the remaining lifetime of the world
				//
				// Bridge segment footprints and neighbour offsets are assumed to remain
				// the same when a segment is destroyed or repaired.
				var seed = Info.NeighbourOffsets.Select(v => self.Location + v);
				var processed = new HashSet<CPos>();
				while (true)
				{
					var step = NextNeighbourStep(seed, processed).ToList();
					if (step.Count == 0)
						break;

					foreach (var s in step)
						segments[s.Location] = s;

					segmentLocations.Add(step.Select(s => s.Location).ToArray());
					seed = step.SelectMany(s => s.NeighbourOffsets.Select(n => s.Location + n)).ToList();
				}

				repairStep = demolishStep = segmentLocations.Count;
			});
		}

		void ITick.Tick(Actor self)
		{
			// Update any dead segments
			dirtyLocations.Clear();
			foreach (var kv in segments)
				if (!kv.Value.Valid)
					dirtyLocations.Add(kv.Key);

			foreach (var c in dirtyLocations)
				segments[c] = bridgeLayer[c].TraitOrDefault<IBridgeSegment>();

			if (demolishTriggerDelay >= 0 && --demolishTriggerDelay < 0)
				StartDemolition(self);

			if (repairStep < segmentLocations.Count && --repairDelay <= 0)
				RepairStep();

			if (demolishStep < segmentLocations.Count && --demolishDelay <= 0)
				DemolishStep();
		}

		IEnumerable<IBridgeSegment> NextNeighbourStep(IEnumerable<CPos> seed, HashSet<CPos> processed)
		{
			foreach (var c in seed)
			{
				var bridge = bridgeLayer[c];
				if (bridge == null)
					continue;

				var segment = bridge.TraitOrDefault<IBridgeSegment>();
				if (segment != null && Info.Types.Contains(segment.Type) && processed.Add(segment.Location))
					yield return segment;
			}
		}

		public void Repair(Actor repairer)
		{
			if (Info.RepairPropagationDelay > 0)
			{
				repairStep = 0;
				repairRepairer = repairer;
				RepairStep();
			}
			else
				foreach (var s in segments.Values)
					s.Repair(repairer);
		}

		public void RepairStep()
		{
			// Find the next segment that needs to be repaired
			while (repairStep < segmentLocations.Count)
			{
				var stepDamage = segmentLocations[repairStep]
					.Select(c => segments[c])
					.Max(s => s.DamageState);

				if (stepDamage > DamageState.Undamaged)
					break;

				repairStep++;
			}

			if (repairStep < segmentLocations.Count)
				foreach (var c in segmentLocations[repairStep])
					segments[c].Repair(repairRepairer);

			repairDelay = Info.RepairPropagationDelay;
		}

		bool IDemolishable.IsValidTarget(Actor self, Actor saboteur)
		{
			return true;
		}

		void IDemolishable.Demolish(Actor self, Actor saboteur, int delay, BitSet<DamageType> damageTypes)
		{
			demolishTriggerDelay = delay;
			demolishTrigger = saboteur;
			demolishTriggerDamageTypes = damageTypes;
		}

		void StartDemolition(Actor self)
		{
			var saboteur = demolishTrigger;
			var damageTypes = demolishTriggerDamageTypes;
			demolishTrigger = null;

			if (self.IsDead)
				return;

			var modifiers = self.TraitsImplementing<IDamageModifier>()
				.Concat(self.Owner.PlayerActor.TraitsImplementing<IDamageModifier>())
				.Select(t => t.GetDamageModifier(self, null));

			if (Util.ApplyPercentageModifiers(100, modifiers) <= 0)
				return;

			if (Info.DemolishPropagationDelay > 0)
			{
				demolishStep = 0;
				demolishSaboteur = saboteur;
				demolishDamageTypes = damageTypes;
				DemolishStep();
			}
			else
				foreach (var s in segments.Values)
					s.Demolish(saboteur, damageTypes);
		}

		public void DemolishStep()
		{
			// Find the next segment to demolish
			while (demolishStep < segmentLocations.Count)
			{
				var stepDamage = segmentLocations[demolishStep]
					.Select(c => segments[c])
					.Max(s => s.DamageState);

				if (stepDamage < DamageState.Dead)
					break;

				demolishStep++;
			}

			if (demolishStep < segmentLocations.Count)
				foreach (var c in segmentLocations[demolishStep])
					segments[c].Demolish(demolishSaboteur, demolishDamageTypes);

			demolishDelay = Info.DemolishPropagationDelay;

			// Always advance at least one step (prevents sticking on placeholders)
			demolishStep++;
		}

		public DamageState BridgeDamageState
		{
			get
			{
				if (segments.Count == 0)
					return DamageState.Undamaged;

				return segments.Values.Max(s => s.DamageState);
			}
		}

		public bool Repairing => repairStep < segmentLocations.Count;

		TraitInfo ISaveState.SaveStateInfo => Info;

		List<MiniYamlNode> ISaveState.SaveState(Actor self, SnapshotWriter w)
		{
			var repairing = repairStep < segmentLocations.Count;
			var demolishing = demolishStep < segmentLocations.Count;
			if (!repairing && !demolishing && demolishTrigger == null)
				return null;

			var nodes = new List<MiniYamlNode>();

			if (repairing)
			{
				nodes.Add(new(RepairStepKey, FieldSaver.FormatValue(repairStep)));
				nodes.Add(new(RepairDelayKey, FieldSaver.FormatValue(repairDelay)));
				nodes.Add(new(RepairRepairerKey, w.ActorRef(repairRepairer)));
			}

			if (demolishing)
			{
				nodes.Add(new(DemolishStepKey, FieldSaver.FormatValue(demolishStep)));
				nodes.Add(new(DemolishDelayKey, FieldSaver.FormatValue(demolishDelay)));
				nodes.Add(new(DemolishSaboteurKey, w.ActorRef(demolishSaboteur)));
				nodes.Add(new(DemolishDamageTypesKey, FieldSaver.FormatValue(demolishDamageTypes)));
			}

			if (demolishTrigger != null)
			{
				nodes.Add(new(DemolishTriggerKey, w.ActorRef(demolishTrigger)));
				nodes.Add(new(DemolishTriggerDelayKey, FieldSaver.FormatValue(demolishTriggerDelay)));
				nodes.Add(new(DemolishTriggerDamageTypesKey, FieldSaver.FormatValue(demolishTriggerDamageTypes)));
			}

			return nodes;
		}

		void ISaveState.LoadState(Actor self, MiniYaml data, SnapshotReader r)
		{
			var nodes = data.ToDictionary();

			if (nodes.TryGetValue(RepairStepKey, out var repair))
				repairStep = FieldLoader.GetValue<int>(RepairStepKey, repair.Value);

			if (nodes.TryGetValue(RepairDelayKey, out var repairWait))
				repairDelay = FieldLoader.GetValue<int>(RepairDelayKey, repairWait.Value);

			if (nodes.TryGetValue(RepairRepairerKey, out var repairer))
				r.DeferActor(repairer.Value, a => repairRepairer = a);

			if (nodes.TryGetValue(DemolishStepKey, out var demolish))
				demolishStep = FieldLoader.GetValue<int>(DemolishStepKey, demolish.Value);

			if (nodes.TryGetValue(DemolishDelayKey, out var demolishWait))
				demolishDelay = FieldLoader.GetValue<int>(DemolishDelayKey, demolishWait.Value);

			if (nodes.TryGetValue(DemolishSaboteurKey, out var saboteur))
				r.DeferActor(saboteur.Value, a => demolishSaboteur = a);

			if (nodes.TryGetValue(DemolishDamageTypesKey, out var types))
				demolishDamageTypes = FieldLoader.GetValue<BitSet<DamageType>>(DemolishDamageTypesKey, types.Value);

			if (nodes.TryGetValue(DemolishTriggerKey, out var trigger))
				r.DeferActor(trigger.Value, a => demolishTrigger = a);

			if (nodes.TryGetValue(DemolishTriggerDelayKey, out var triggerDelay))
				demolishTriggerDelay = FieldLoader.GetValue<int>(DemolishTriggerDelayKey, triggerDelay.Value);

			if (nodes.TryGetValue(DemolishTriggerDamageTypesKey, out var triggerTypes))
				demolishTriggerDamageTypes = FieldLoader.GetValue<BitSet<DamageType>>(DemolishTriggerDamageTypesKey, triggerTypes.Value);
		}
	}
}
