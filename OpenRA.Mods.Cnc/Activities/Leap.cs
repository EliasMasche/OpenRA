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
using OpenRA.Mods.Cnc.Traits;
using OpenRA.Mods.Common.Traits;
using OpenRA.Traits;

namespace OpenRA.Mods.Cnc.Activities
{
	[SaveableActivity]
	public class Leap : Activity
	{
		readonly Mobile mobile;
		readonly int speed;
		readonly AttackLeap attack;

		Mobile targetMobile;
		EdibleByLeap edible;
		Target target;

		CPos destinationCell;
		SubCell destinationSubCell = SubCell.Any;
		WPos destination, origin;
		int length;
		bool canceled = false;
		bool jumpComplete = false;
		int ticks = 0;
		WPos targetPosition;

		public Leap(in Target target, Mobile mobile, Mobile targetMobile, int speed, AttackLeap attack, EdibleByLeap edible)
		{
			this.mobile = mobile;
			this.targetMobile = targetMobile;
			this.attack = attack;
			this.target = target;
			this.edible = edible;
			this.speed = speed;
		}

		internal Leap(Actor self, SnapshotReader r, MiniYaml yaml)
		{
			mobile = self.Trait<Mobile>();
			attack = self.Trait<AttackLeap>();

			var n = yaml.ToDictionary();
			speed = FieldLoader.GetValue<int>("Speed", n["Speed"].Value);
			destinationCell = FieldLoader.GetValue<CPos>("DestinationCell", n["DestinationCell"].Value);
			destination = FieldLoader.GetValue<WPos>("Destination", n["Destination"].Value);
			origin = FieldLoader.GetValue<WPos>("Origin", n["Origin"].Value);
			length = FieldLoader.GetValue<int>("Length", n["Length"].Value);
			canceled = FieldLoader.GetValue<bool>("Canceled", n["Canceled"].Value);
			jumpComplete = FieldLoader.GetValue<bool>("JumpComplete", n["JumpComplete"].Value);
			ticks = FieldLoader.GetValue<int>("Ticks", n["Ticks"].Value);
			targetPosition = FieldLoader.GetValue<WPos>("TargetPosition", n["TargetPosition"].Value);
			destinationSubCell = FieldLoader.GetValue<SubCell>("DestinationSubCell", n["DestinationSubCell"].Value);

			r.DeferTarget(n["Target"].Value, t =>
			{
				target = t;
				var a = t.Type == TargetType.Actor ? t.Actor : null;
				targetMobile = a?.TraitOrDefault<Mobile>();
				edible = a?.TraitOrDefault<EdibleByLeap>();
			});
		}

		public override List<MiniYamlNode> SaveState(Actor self, SnapshotWriter w)
		{
			return
			[
				new("Target", w.TargetRef(target)),
				new("Speed", FieldSaver.FormatValue(speed)),
				new("DestinationCell", FieldSaver.FormatValue(destinationCell)),
				new("DestinationSubCell", FieldSaver.FormatValue(destinationSubCell)),
				new("Destination", FieldSaver.FormatValue(destination)),
				new("Origin", FieldSaver.FormatValue(origin)),
				new("Length", FieldSaver.FormatValue(length)),
				new("Canceled", FieldSaver.FormatValue(canceled)),
				new("JumpComplete", FieldSaver.FormatValue(jumpComplete)),
				new("Ticks", FieldSaver.FormatValue(ticks)),
				new("TargetPosition", FieldSaver.FormatValue(targetPosition))
			];
		}

		protected override void OnFirstRun(Actor self)
		{
			destinationCell = target.Actor.Location;
			if (targetMobile != null)
				destinationSubCell = targetMobile.ToSubCell;

			origin = self.CenterPosition;
			destination = self.World.Map.CenterOfSubCell(destinationCell, destinationSubCell);
			length = Math.Max((origin - destination).Length / speed, 1);

			// First check if we are still allowed to leap
			// We need an extra boolean as using Cancel() in OnFirstRun doesn't work
			canceled = !edible.GetLeapAtBy(self) || target.Type != TargetType.Actor;

			IsInterruptible = false;

			if (canceled)
				return;

			targetPosition = target.CenterPosition;
			attack.GrantLeapCondition(self);
		}

		public override bool Tick(Actor self)
		{
			// Correct the visual position after we jumped
			if (canceled || jumpComplete)
				return true;

			if (target.Type != TargetType.Invalid)
				targetPosition = target.CenterPosition;

			var position = length > 1 ? WPos.Lerp(origin, targetPosition, ticks, length - 1) : targetPosition;
			mobile.SetCenterPosition(self, position);

			// We are at the destination
			if (++ticks >= length)
			{
				// Revoke the run condition
				attack.IsAiming = false;

				// Move to the correct subcells, if our target actor uses subcells
				// (This does not update the visual position!)
				mobile.SetLocation(destinationCell, destinationSubCell, destinationCell, destinationSubCell);

				// Update movement which results in movementType set to MovementType.None.
				// This is needed to prevent the move animation from playing.
				mobile.UpdateMovement();

				// Revoke the condition before attacking, as it is usually used to pause the attack trait
				attack.RevokeLeapCondition(self);
				attack.DoAttack(self, target);

				jumpComplete = true;
				QueueChild(mobile.LocalMove(self, position, self.World.Map.CenterOfSubCell(destinationCell, destinationSubCell)));
			}

			return false;
		}

		protected override void OnLastRun(Actor self)
		{
			attack.RevokeLeapCondition(self);
			base.OnLastRun(self);
		}

		protected override void OnActorDispose(Actor self)
		{
			attack.RevokeLeapCondition(self);
			base.OnActorDispose(self);
		}

		public override IEnumerable<Target> GetTargets(Actor self)
		{
			yield return Target.FromPos(ticks < length / 2 ? origin : destination);
		}
	}
}
