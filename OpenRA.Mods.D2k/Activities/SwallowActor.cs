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
using OpenRA.GameRules;
using OpenRA.GameSaves;
using OpenRA.Mods.Common.Effects;
using OpenRA.Mods.Common.Traits;
using OpenRA.Mods.D2k.Traits;
using OpenRA.Primitives;
using OpenRA.Traits;

namespace OpenRA.Mods.D2k.Activities
{
	enum AttackState { Uninitialized, Burrowed, Attacking }

	[SaveableActivity]
	sealed class SwallowActor : Activity
	{
		const int NearEnough = 1;

		readonly Sandworm sandworm;

		readonly WeaponInfo weapon;
		readonly Armament armament;

		Target target;
		readonly AttackSwallow swallow;
		readonly IPositionable positionable;
		readonly IFacing facing;

		int countdown;
		CPos burrowLocation;
		AttackState stance;
		int attackingToken = Actor.InvalidConditionToken;

		public SwallowActor(Actor self, in Target target, Armament a, IFacing facing)
		{
			this.target = target;
			this.facing = facing;
			armament = a;
			weapon = a.Weapon;
			sandworm = self.Trait<Sandworm>();
			positionable = self.Trait<Mobile>();
			swallow = self.Trait<AttackSwallow>();
		}

		internal SwallowActor(Actor self, SnapshotReader r, MiniYaml yaml)
		{
			sandworm = self.Trait<Sandworm>();
			positionable = self.Trait<IPositionable>();
			swallow = self.Trait<AttackSwallow>();
			facing = self.Trait<IFacing>();

			var n = yaml.ToDictionary();
			countdown = FieldLoader.GetValue<int>("Countdown", n["Countdown"].Value);
			burrowLocation = FieldLoader.GetValue<CPos>("BurrowLocation", n["BurrowLocation"].Value);
			stance = FieldLoader.GetValue<AttackState>("Stance", n["Stance"].Value);

			var index = FieldLoader.GetValue<int>("Armament", n["Armament"].Value);
			var armaments = self.TraitsImplementing<Armament>().ToArray();
			armament = index >= 0 && index < armaments.Length ? armaments[index] : armaments.FirstOrDefault();
			weapon = armament?.Weapon;

			if (FieldLoader.GetValue<bool>("Attacking", n["Attacking"].Value))
				attackingToken = self.GrantCondition(swallow.Info.AttackingCondition);

			r.DeferTarget(n["Target"].Value, t => target = t);
		}

		public override List<MiniYamlNode> SaveState(Actor self, SnapshotWriter w)
		{
			return
			[
				new("Target", w.TargetRef(target)),
				new("Countdown", FieldSaver.FormatValue(countdown)),
				new("BurrowLocation", FieldSaver.FormatValue(burrowLocation)),
				new("Stance", FieldSaver.FormatValue(stance)),
				new("Armament", FieldSaver.FormatValue(self.TraitsImplementing<Armament>().ToList().IndexOf(armament))),
				new("Attacking", FieldSaver.FormatValue(attackingToken != Actor.InvalidConditionToken))
			];
		}

		bool AttackTargets(Actor self, IReadOnlyCollection<Actor> targets)
		{
			var targetLocation = target.Actor.Location;
			foreach (var t in targets)
			{
				var targetClose = t; // loop variable in closure hazard

				self.World.AddFrameEndTask(_ =>
				{
					// Don't use Kill() because we don't want any of its side-effects (husks, etc)
					targetClose.Dispose();

					// Harvester insurance
					if (targetClose.Info.HasTraitInfo<HarvesterInfo>())
					{
						var insurance = targetClose.Owner.PlayerActor.TraitOrDefault<HarvesterInsurance>();
						if (insurance != null)
							self.World.AddFrameEndTask(w => insurance.TryActivate());
					}
				});
			}

			positionable.SetPosition(self, targetLocation);

			var attackPosition = self.CenterPosition;
			var affectedPlayers = targets.Select(x => x.Owner).ToHashSet();
			Game.Sound.Play(SoundType.World, swallow.Info.WormAttackSound, self.CenterPosition);

			foreach (var player in affectedPlayers)
				self.World.AddFrameEndTask(w => w.Add(
					new MapNotificationEffect(player, "Speech", swallow.Info.WormAttackNotification, 25, true, attackPosition, Color.Red)));

			if (affectedPlayers.Contains(self.World.LocalPlayer))
				TextNotificationsManager.AddTransientLine(self.World.LocalPlayer, swallow.Info.WormAttackTextNotification);

			return armament.CheckFire(self, facing, target);
		}

		public override bool Tick(Actor self)
		{
			switch (stance)
			{
				case AttackState.Uninitialized:
					stance = AttackState.Burrowed;
					countdown = swallow.Info.AttackDelay;
					burrowLocation = self.Location;
					if (attackingToken == Actor.InvalidConditionToken)
						attackingToken = self.GrantCondition(swallow.Info.AttackingCondition);

					break;
				case AttackState.Burrowed:
					if (--countdown > 0)
						return false;

					var targetLocation = target.Actor.Location;

					// The target has moved too far away
					if ((burrowLocation - targetLocation).Length > NearEnough)
					{
						RevokeCondition(self);
						return true;
					}

					// The target reached solid ground
					if (!positionable.CanEnterCell(targetLocation, null, BlockedByActor.None))
					{
						RevokeCondition(self);
						return true;
					}

					var targets = self.World.ActorMap.GetActorsAt(targetLocation)
						.Where(t => !t.Equals(self) && weapon.IsValidAgainst(t, self))
						.ToList();

					if (targets.Count == 0)
					{
						RevokeCondition(self);
						return true;
					}

					stance = AttackState.Attacking;
					countdown = swallow.Info.ReturnDelay;
					sandworm.IsAttacking = true;
					AttackTargets(self, targets);

					break;
				case AttackState.Attacking:
					if (--countdown > 0)
						return false;

					sandworm.IsAttacking = false;

					// There is a chance that the worm would just go away after attacking
					if (self.World.SharedRandom.Next(100) <= sandworm.WormInfo.ChanceToDisappear)
					{
						self.CancelActivity();
						self.World.AddFrameEndTask(w => self.Dispose());
					}

					RevokeCondition(self);
					return true;
			}

			return false;
		}

		void RevokeCondition(Actor self)
		{
			if (attackingToken != Actor.InvalidConditionToken)
				attackingToken = self.RevokeCondition(attackingToken);
		}
	}
}
