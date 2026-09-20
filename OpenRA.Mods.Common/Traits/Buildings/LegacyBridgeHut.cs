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
using System.Linq;
using OpenRA.GameSaves;
using OpenRA.Primitives;
using OpenRA.Traits;

namespace OpenRA.Mods.Common.Traits
{
	[Desc("Allows bridges to be targeted for demolition and repair.")]
	public class LegacyBridgeHutInfo : TraitInfo, IDemolishableInfo
	{
		public bool IsValidTarget(ActorInfo actorInfo, Actor saboteur) { return false; } // TODO: bridges don't support frozen under fog

		public override object Create(ActorInitializer init) { return new LegacyBridgeHut(this, init); }
	}

	public class LegacyBridgeHut : IDemolishable, ITick, ISaveState
	{
		struct Walk
		{
			public Bridge Span;
			public int Direction;
			public int Delay;
		}

		const string ParentKey = "Parent";
		const string RepairWalksKey = "RepairWalks";
		const string RepairRepairerKey = "RepairRepairer";
		const string DemolishWalksKey = "DemolishWalks";
		const string DemolishSaboteurKey = "DemolishSaboteur";
		const string DemolishDamageTypesKey = "DemolishDamageTypes";
		const string DemolishTriggerKey = "DemolishTrigger";
		const string DemolishTriggerDelayKey = "DemolishTriggerDelay";
		const string DemolishTriggerDamageTypesKey = "DemolishTriggerDamageTypes";

		public readonly LegacyBridgeHutInfo Info;

		public Bridge FirstBridge { get; private set; }
		public Bridge Bridge { get; private set; }
		public DamageState BridgeDamageState => Bridge.AggregateDamageState();

		public bool Repairing => repairWalks.Count > 0;

		public IEnumerable<(Bridge Span, int Direction)> PendingRepairs =>
			repairWalks.Where(v => v.Span != null).Select(v => (v.Span, v.Direction));

		readonly List<Walk> repairWalks = [];
		readonly List<Walk> demolishWalks = [];
		Actor repairRepairer;
		Actor demolishSaboteur;
		BitSet<DamageType> demolishDamageTypes;

		int demolishTriggerDelay = -1;
		Actor demolishTrigger;
		BitSet<DamageType> demolishTriggerDamageTypes;

		public LegacyBridgeHut(LegacyBridgeHutInfo info, ActorInitializer init)
		{
			Info = info;

			var bridge = init.GetOrDefault<ParentActorInit>()?.Value;
			if (bridge == null)
				return;

			init.World.AddFrameEndTask(_ => LinkTo(bridge.Actor(init.World).Value));
		}

		void LinkTo(Actor bridge)
		{
			Bridge = bridge.Trait<Bridge>();
			Bridge.AddHut(this);
			FirstBridge = Bridge.Enumerate(0, true).Last();
		}

		public void Repair(Actor repairer)
		{
			repairRepairer = repairer;
			repairWalks.Clear();
			Bridge.Do((b, d) => repairWalks.Add(new Walk { Span = b, Direction = d, Delay = 0 }));

			StepWalks(repairWalks, RepairSpan);
		}

		void RepairSpan(Bridge span, int direction, out Bridge next, out int delay)
		{
			delay = span.RepairPropagationDelay(direction);
			next = span.RepairTerminatesHere(direction) ? null : span.NextSpan(direction);
			span.Repair(repairRepairer);
		}

		void DemolishSpan(Bridge span, int direction, out Bridge next, out int delay)
		{
			delay = span.DemolishPropagationDelay(direction);
			next = span.NextSpan(direction);
			span.Demolish(demolishSaboteur, demolishDamageTypes);
		}

		delegate void StepSpan(Bridge span, int direction, out Bridge next, out int delay);

		static void StepWalks(List<Walk> walks, StepSpan step)
		{
			for (var i = walks.Count - 1; i >= 0; i--)
			{
				if (walks[i].Delay > 0)
				{
					var waiting = walks[i];
					waiting.Delay--;
					walks[i] = waiting;
					continue;
				}

				step(walks[i].Span, walks[i].Direction, out var next, out var delay);

				if (next == null)
				{
					walks.RemoveAt(i);
					continue;
				}

				walks[i] = new Walk { Span = next, Direction = walks[i].Direction, Delay = delay };
			}
		}

		void ITick.Tick(Actor self)
		{
			if (demolishTriggerDelay >= 0 && --demolishTriggerDelay < 0)
				StartDemolition(self);

			if (repairWalks.Count > 0)
				StepWalks(repairWalks, RepairSpan);

			if (demolishWalks.Count > 0)
				StepWalks(demolishWalks, DemolishSpan);
		}

		bool IDemolishable.IsValidTarget(Actor self, Actor saboteur)
		{
			return BridgeDamageState != DamageState.Dead;
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

			demolishSaboteur = saboteur;
			demolishDamageTypes = damageTypes;
			demolishWalks.Clear();
			Bridge.Do((b, d) => demolishWalks.Add(new Walk { Span = b, Direction = d, Delay = 0 }));

			StepWalks(demolishWalks, DemolishSpan);
		}

		TraitInfo ISaveState.SaveStateInfo => Info;

		List<MiniYamlNode> ISaveState.SaveState(Actor self, SnapshotWriter w)
		{
			var nodes = new List<MiniYamlNode>
			{
				new(ParentKey, w.ActorRef(Bridge?.Actor))
			};

			if (repairWalks.Count > 0)
			{
				nodes.Add(new(RepairWalksKey, SaveWalks(repairWalks, w)));
				nodes.Add(new(RepairRepairerKey, w.ActorRef(repairRepairer)));
			}

			if (demolishWalks.Count > 0)
			{
				nodes.Add(new(DemolishWalksKey, SaveWalks(demolishWalks, w)));
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

		static string SaveWalks(List<Walk> walks, SnapshotWriter w)
		{
			return walks
				.Select(v => $"{w.ActorRef(v.Span.Actor)}:{v.Direction}:{v.Delay}")
				.JoinWith(", ");
		}

		void ISaveState.LoadState(Actor self, MiniYaml data, SnapshotReader r)
		{
			var nodes = data.ToDictionary();

			if (nodes.TryGetValue(ParentKey, out var parent))
				r.DeferActor(parent.Value, a =>
				{
					if (a != null)
						LinkTo(a);
				});

			if (nodes.TryGetValue(RepairRepairerKey, out var repairer))
				r.DeferActor(repairer.Value, a => repairRepairer = a);

			if (nodes.TryGetValue(DemolishSaboteurKey, out var saboteur))
				r.DeferActor(saboteur.Value, a => demolishSaboteur = a);

			if (nodes.TryGetValue(DemolishTriggerKey, out var trigger))
				r.DeferActor(trigger.Value, a => demolishTrigger = a);

			if (nodes.TryGetValue(DemolishTriggerDelayKey, out var triggerDelay))
				demolishTriggerDelay = FieldLoader.GetValue<int>(DemolishTriggerDelayKey, triggerDelay.Value);

			if (nodes.TryGetValue(DemolishTriggerDamageTypesKey, out var triggerTypes))
				demolishTriggerDamageTypes = FieldLoader.GetValue<BitSet<DamageType>>(DemolishTriggerDamageTypesKey, triggerTypes.Value);

			if (nodes.TryGetValue(DemolishDamageTypesKey, out var types))
				demolishDamageTypes = FieldLoader.GetValue<BitSet<DamageType>>(DemolishDamageTypesKey, types.Value);

			if (nodes.TryGetValue(RepairWalksKey, out var repairWalkNodes))
				LoadWalks(repairWalkNodes.Value, repairWalks, r);

			if (nodes.TryGetValue(DemolishWalksKey, out var demolishWalkNodes))
				LoadWalks(demolishWalkNodes.Value, demolishWalks, r);
		}

		static void LoadWalks(string value, List<Walk> walks, SnapshotReader r)
		{
			if (string.IsNullOrEmpty(value))
				return;

			foreach (var entry in value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
			{
				var parts = entry.Split(':');
				var direction = FieldLoader.GetValue<int>("Direction", parts[^2]);
				var delay = FieldLoader.GetValue<int>("Delay", parts[^1]);

				var index = walks.Count;
				walks.Add(new Walk { Span = null, Direction = direction, Delay = delay });

				r.DeferActor(string.Join(':', parts[..^2]), a =>
				{
					var span = a?.TraitOrDefault<Bridge>();
					if (span != null)
						walks[index] = new Walk { Span = span, Direction = direction, Delay = delay };
				});
			}

			r.DeferCompleted(() => walks.RemoveAll(v => v.Span == null));
		}
	}
}
