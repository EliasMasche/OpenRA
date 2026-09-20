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
using OpenRA.Activities;
using OpenRA.GameSaves;
using OpenRA.Mods.Common.Traits;
using OpenRA.Primitives;
using OpenRA.Traits;

namespace OpenRA.Mods.Cnc.Traits
{
	[Desc("Actor has a visual turret used to attack.")]
	public class AttackTDGunboatTurretedInfo : AttackTurretedInfo, Requires<TDGunboatInfo>
	{
		public override object Create(ActorInitializer init) { return new AttackTDGunboatTurreted(init.Self, this); }
	}

	public class AttackTDGunboatTurreted : AttackTurreted
	{
		public AttackTDGunboatTurreted(Actor self, AttackTDGunboatTurretedInfo info)
			: base(self, info) { }

		public override Activity GetAttackActivity(Actor self, AttackSource source, in Target newTarget, bool allowMove, bool forceAttack, Color? targetLineColor)
		{
			return new AttackTDGunboatTurretedActivity(self, newTarget, forceAttack, targetLineColor);
		}

		[SaveableActivity]
		sealed class AttackTDGunboatTurretedActivity : Activity
		{
			readonly AttackTDGunboatTurreted attack;

			Target target;
			readonly bool forceAttack;
			readonly Color? targetLineColor;
			bool hasTicked;

			public AttackTDGunboatTurretedActivity(Actor self, in Target target, bool forceAttack, Color? targetLineColor = null)
			{
				attack = self.Trait<AttackTDGunboatTurreted>();
				this.target = target;
				this.forceAttack = forceAttack;
				this.targetLineColor = targetLineColor;
			}

			internal AttackTDGunboatTurretedActivity(Actor self, SnapshotReader r, MiniYaml yaml)
			{
				attack = self.Trait<AttackTDGunboatTurreted>();

				var n = yaml.ToDictionary();
				forceAttack = FieldLoader.GetValue<bool>("ForceAttack", n["ForceAttack"].Value);
				hasTicked = FieldLoader.GetValue<bool>("HasTicked", n["HasTicked"].Value);

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
					new("HasTicked", FieldSaver.FormatValue(hasTicked)),
					new("TargetLineColor", targetLineColor.HasValue ? FieldSaver.FormatValue(targetLineColor.Value) : "")
				];
			}

			public override bool Tick(Actor self)
			{
				if (IsCanceling || !target.IsValidFor(self))
					return true;

				if (attack.IsTraitDisabled)
					return false;

				var weapon = attack.ChooseArmamentsForTarget(target, forceAttack).FirstOrDefault();
				if (weapon != null)
				{
					// Check that AttackTDGunboatTurreted hasn't cancelled the target by modifying attack.Target
					// Having both this and AttackTDGunboatTurreted modify that field is a horrible hack.
					if (hasTicked && attack.RequestedTarget.Type == TargetType.Invalid)
						return true;

					attack.SetRequestedTarget(target);
					hasTicked = true;
				}

				return false;
			}

			public override IEnumerable<TargetLineNode> TargetLineNodes(Actor self)
			{
				if (targetLineColor != null)
					yield return new TargetLineNode(target, targetLineColor.Value);
			}
		}
	}
}
