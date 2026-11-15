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
using System.Linq;
using OpenRA.Mods.Cnc.Traits.Render;
using OpenRA.Mods.Common.Traits;
using OpenRA.Mods.Common.Traits.Render;
using OpenRA.Traits;

namespace OpenRA.Mods.Cnc.Traits
{
	[Desc("Actor's turret rises from the ground before attacking.")]
	sealed class AttackPopupTurretedInfo : AttackTurretedInfo, Requires<BuildingInfo>, Requires<WithEmbeddedTurretSpriteBodyInfo>, ISnapshotCreationInfo
	{
		[Desc("How many game ticks should pass before closing the actor's turret.")]
		public readonly int CloseDelay = 125;

		public readonly WAngle DefaultFacing = WAngle.Zero;

		[Desc("The percentage of damage that is received while this actor is closed.")]
		public readonly int ClosedDamageMultiplier = 50;

		[SequenceReference]
		[Desc("Sequence to play when opening.")]
		public readonly string OpeningSequence = "opening";

		[SequenceReference]
		[Desc("Sequence to play when closing.")]
		public readonly string ClosingSequence = "closing";

		[SequenceReference]
		[Desc("Idle sequence to play when closed.")]
		public readonly string ClosedIdleSequence = "closed-idle";

		[Desc("Which sprite body to play the animation on.")]
		public readonly string Body = "body";

		public override object Create(ActorInitializer init) { return new AttackPopupTurreted(init, this); }

		void ISnapshotCreationInfo.AddSnapshotInits(Primitives.TypeDictionary inits, Player owner, Func<string, string> saved, Func<string, Actor> savedActor)
		{
			if (!inits.Contains<SkipMakeAnimsInit>())
				inits.Add(new SkipMakeAnimsInit());
		}
	}

	sealed class AttackPopupTurreted : AttackTurreted, INotifyIdle, IDamageModifier, IResumeAnimations
	{
		enum PopupState { Open, Rotating, Transitioning, Closed }

		readonly AttackPopupTurretedInfo info;
		readonly WithSpriteBody wsb;
		readonly Turreted turret;

		int idleTicks = 0;
		PopupState state = PopupState.Open;

		// While transitioning: towards open rather than closed.
		bool opening;
		readonly bool skippedMakeAnimation;

		public AttackPopupTurreted(ActorInitializer init, AttackPopupTurretedInfo info)
			: base(init.Self, info)
		{
			this.info = info;
			turret = turrets.FirstOrDefault();
			wsb = init.Self.TraitsImplementing<WithSpriteBody>().Single(w => w.Info.Name == info.Body);
			skippedMakeAnimation = init.Contains<SkipMakeAnimsInit>(info);
		}

		protected override void Created(Actor self)
		{
			base.Created(self);

			// Map placed actors are created in the closed state
			if (skippedMakeAnimation)
			{
				state = PopupState.Closed;
				wsb.PlayCustomAnimationRepeating(self, info.ClosedIdleSequence);
				turret.FaceTarget(self, Target.Invalid);
			}
		}

		protected override bool CanAttack(Actor self, in Target target)
		{
			if (IsTraitPaused)
				return false;

			if (state == PopupState.Closed)
			{
				state = PopupState.Transitioning;
				opening = true;
				wsb.PlayCustomAnimation(self, info.OpeningSequence, TransitionFinished(self));

				idleTicks = 0;
			}

			if (state == PopupState.Transitioning || !base.CanAttack(self, target))
				return false;

			idleTicks = 0;
			return true;
		}

		void INotifyIdle.TickIdle(Actor self)
		{
			if (IsTraitDisabled || IsTraitPaused)
				return;

			if (state == PopupState.Open && idleTicks++ > info.CloseDelay)
			{
				var facingOffset = new WVec(0, -1024, 0).Rotate(WRot.FromYaw(info.DefaultFacing));
				turret.FaceTarget(self, Target.FromPos(self.CenterPosition + facingOffset));
				state = PopupState.Rotating;
			}
			else if (state == PopupState.Rotating && turret.HasAchievedDesiredFacing)
			{
				state = PopupState.Transitioning;
				opening = false;
				wsb.PlayCustomAnimation(self, info.ClosingSequence, TransitionFinished(self));
			}
		}

		// The callback that ends the current transition. Playing and resuming both attach it.
		Action TransitionFinished(Actor self)
		{
			return opening ? () => OpeningFinished(self) : () => ClosingFinished(self);
		}

		void OpeningFinished(Actor self)
		{
			state = PopupState.Open;
			wsb.PlayCustomAnimationRepeating(self, wsb.Info.Sequence);
		}

		void ClosingFinished(Actor self)
		{
			state = PopupState.Closed;
			wsb.PlayCustomAnimationRepeating(self, info.ClosedIdleSequence);
			turret.FaceTarget(self, Target.Invalid);
		}

		void IResumeAnimations.ResumeAnimations(Actor self)
		{
			if (state == PopupState.Transitioning)
				wsb.ContinueCustomAnimation(self, TransitionFinished(self));
		}

		int IDamageModifier.GetDamageModifier(Actor attacker, Damage damage)
		{
			return state == PopupState.Closed ? info.ClosedDamageMultiplier : 100;
		}
	}
}
