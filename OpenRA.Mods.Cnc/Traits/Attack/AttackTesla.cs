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
using OpenRA.Mods.Common.Activities;
using OpenRA.Mods.Common.Traits;
using OpenRA.Primitives;
using OpenRA.Traits;

namespace OpenRA.Mods.Cnc.Traits
{
	[Desc("Implements the charge-then-burst attack logic specific to the RA Tesla coil.")]
	sealed class AttackTeslaInfo : AttackBaseInfo
	{
		[Desc("How many charges this actor has to attack with, once charged.")]
		public readonly int MaxCharges = 1;

		[Desc("Reload time for all charges (in ticks).")]
		public readonly int ReloadDelay = 120;

		[Desc("Delay for initial charge attack (in ticks).")]
		public readonly int InitialChargeDelay = 22;

		[Desc("Delay between charge attacks (in ticks).")]
		public readonly int ChargeDelay = 3;

		[Desc("Sound to play when actor charges.")]
		public readonly string ChargeAudio = null;

		public override object Create(ActorInitializer init) { return new AttackTesla(init.Self, this); }
	}

	sealed class AttackTesla : AttackBase, ITick, INotifyAttack
	{
		const string ChargesKey = "Charges";
		const string TimeToRechargeKey = "TimeToRecharge";

		readonly AttackTeslaInfo info;

		[VerifySync]
		int charges;

		[VerifySync]
		int timeToRecharge;

		public AttackTesla(Actor self, AttackTeslaInfo info)
			: base(self, info)
		{
			this.info = info;
			charges = info.MaxCharges;
		}

		void ITick.Tick(Actor self)
		{
			if (--timeToRecharge <= 0)
				charges = info.MaxCharges;
		}

		protected override bool CanAttack(Actor self, in Target target)
		{
			if (!IsReachableTarget(target, true))
				return false;

			return base.CanAttack(self, target);
		}

		void INotifyAttack.Attacking(Actor self, in Target target, Armament a, Barrel barrel)
		{
			--charges;
			timeToRecharge = info.ReloadDelay;
		}

		void INotifyAttack.PreparingAttack(Actor self, in Target target, Armament a, Barrel barrel) { }

		protected override List<MiniYamlNode> SaveState(SnapshotWriter w)
		{
			return
			[
				.. base.SaveState(w),
				new(ChargesKey, FieldSaver.FormatValue(charges)),
				new(TimeToRechargeKey, FieldSaver.FormatValue(timeToRecharge))
			];
		}

		protected override void LoadState(MiniYaml data, SnapshotReader r)
		{
			base.LoadState(data, r);

			var nodes = data.ToDictionary();
			if (nodes.TryGetValue(ChargesKey, out var c))
				charges = FieldLoader.GetValue<int>(ChargesKey, c.Value);

			if (nodes.TryGetValue(TimeToRechargeKey, out var t))
				timeToRecharge = FieldLoader.GetValue<int>(TimeToRechargeKey, t.Value);
		}

		public override Activity GetAttackActivity(
			Actor self, AttackSource source, in Target newTarget, bool allowMove, bool forceAttack, Color? targetLineColor = null)
		{
			return new ChargeAttack(this, newTarget, forceAttack, targetLineColor);
		}

		[SaveableActivity]
		sealed class ChargeAttack : Activity, IActivityNotifyStanceChanged
		{
			readonly AttackTesla attack;

			Target target;
			readonly bool forceAttack;
			readonly Color? targetLineColor;

			public ChargeAttack(AttackTesla attack, in Target target, bool forceAttack, Color? targetLineColor = null)
			{
				this.attack = attack;
				this.target = target;
				this.forceAttack = forceAttack;
				this.targetLineColor = targetLineColor;
			}

			internal ChargeAttack(Actor self, SnapshotReader r, MiniYaml yaml)
			{
				attack = self.Trait<AttackTesla>();

				var n = yaml.ToDictionary();
				forceAttack = FieldLoader.GetValue<bool>("ForceAttack", n["ForceAttack"].Value);

				var color = n["TargetLineColor"].Value;
				if (!string.IsNullOrEmpty(color))
					targetLineColor = FieldLoader.GetValue<Color>("TargetLineColor", color);

				r.DeferTarget(n["Target"].Value, t => target = t);
			}

			public override List<MiniYamlNode> SaveState(Actor self, SnapshotWriter w)
			{
				return
				[
					new("Target", w.TargetRef(target)),
					new("ForceAttack", FieldSaver.FormatValue(forceAttack)),
					new("TargetLineColor", targetLineColor.HasValue ? FieldSaver.FormatValue(targetLineColor.Value) : "")
				];
			}

			public override bool Tick(Actor self)
			{
				if (IsCanceling || !attack.CanAttack(self, target))
					return true;

				if (attack.charges == 0)
					return false;

				foreach (var notify in self.TraitsImplementing<INotifyTeslaCharging>())
					notify.Charging(self, target);

				if (!string.IsNullOrEmpty(attack.info.ChargeAudio))
					Game.Sound.Play(SoundType.World, attack.info.ChargeAudio, self.CenterPosition);

				QueueChild(new Wait(attack.info.InitialChargeDelay));
				QueueChild(new ChargeFire(attack, target));
				return false;
			}

			void IActivityNotifyStanceChanged.StanceChanged(Actor self, AutoTarget autoTarget, UnitStance oldStance, UnitStance newStance)
			{
				// Cancel non-forced targets when switching to a more restrictive stance if they are no longer valid for auto-targeting
				if (newStance > oldStance || forceAttack)
					return;

				if (target.Type == TargetType.Actor)
				{
					var a = target.Actor;
					if (!autoTarget.HasValidTargetPriority(self, a.Owner, a.GetEnabledTargetTypes()))
						Cancel(self, true);
				}
				else if (target.Type == TargetType.FrozenActor)
				{
					var fa = target.FrozenActor;
					if (!autoTarget.HasValidTargetPriority(self, fa.Owner, fa.TargetTypes))
						Cancel(self, true);
				}
			}

			public override IEnumerable<TargetLineNode> TargetLineNodes(Actor self)
			{
				if (targetLineColor != null)
					yield return new TargetLineNode(target, targetLineColor.Value);
			}
		}

		[SaveableActivity]
		sealed class ChargeFire : Activity
		{
			readonly AttackTesla attack;

			Target target;

			public ChargeFire(AttackTesla attack, in Target target)
			{
				this.attack = attack;
				this.target = target;
			}

			internal ChargeFire(Actor self, SnapshotReader r, MiniYaml yaml)
			{
				attack = self.Trait<AttackTesla>();
				r.DeferTarget(yaml.NodeWithKeyOrDefault("Target").Value.Value, t => target = t);
			}

			public override List<MiniYamlNode> SaveState(Actor self, SnapshotWriter w)
			{
				return [new("Target", w.TargetRef(target))];
			}

			public override bool Tick(Actor self)
			{
				if (IsCanceling || !attack.CanAttack(self, target))
					return true;

				if (attack.charges == 0)
					return true;

				attack.DoAttack(self, target);

				QueueChild(new Wait(attack.info.ChargeDelay));
				return false;
			}
		}
	}
}
