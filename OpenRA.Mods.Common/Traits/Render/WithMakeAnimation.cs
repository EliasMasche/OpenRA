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
using System.Collections.Immutable;
using System.Linq;
using OpenRA.Activities;
using OpenRA.Traits;

namespace OpenRA.Mods.Common.Traits.Render
{
	[Desc("Replaces the sprite during construction/deploy/undeploy.")]
	public class WithMakeAnimationInfo : TraitInfo, Requires<WithSpriteBodyInfo>, ISnapshotCreationInfo
	{
		[SequenceReference]
		[Desc("Sequence name to use.")]
		public readonly string Sequence = "make";

		[GrantedConditionReference]
		[Desc("The condition to grant to self while the make animation is playing.")]
		public readonly string Condition = null;

		[Desc("Apply to sprite bodies with these names.")]
		public readonly ImmutableArray<string> BodyNames = ["body"];

		public override object Create(ActorInitializer init) { return new WithMakeAnimation(init, this); }

		void ISnapshotCreationInfo.AddSnapshotInits(Primitives.TypeDictionary inits, Player owner, Func<string, string> saved, Func<string, Actor> savedActor)
		{
			if (!inits.Contains<SkipMakeAnimsInit>())
				inits.Add(new SkipMakeAnimsInit());
		}
	}

	public class WithMakeAnimation : INotifyCreated, INotifyDeployTriggered, IResumeAnimations
	{
		enum PendingMake { None, External, Created, QueueActivity, Deploy, Undeploy }

		readonly WithMakeAnimationInfo info;
		readonly WithSpriteBody[] wsbs;
		readonly bool skipMakeAnimation;
		WithMakeOverlay[] overlays;

		int token = Actor.InvalidConditionToken;

		PendingMake pending;
		Activity pendingActivity;
		bool pendingQueued;
		bool deployNotified;

		public WithMakeAnimation(ActorInitializer init, WithMakeAnimationInfo info)
		{
			this.info = info;
			var self = init.Self;
			wsbs = self.TraitsImplementing<WithSpriteBody>().Where(w => info.BodyNames.Contains(w.Info.Name)).ToArray();
			skipMakeAnimation = init.Contains<SkipMakeAnimsInit>(info);
		}

		void INotifyCreated.Created(Actor self)
		{
			overlays = self.TraitsImplementing<WithMakeOverlay>().ToArray();
			if (!skipMakeAnimation)
			{
				Forward(self, () => { });
				pending = PendingMake.Created;
			}
		}

		public void Forward(Actor self, Action onComplete)
		{
			if (token == Actor.InvalidConditionToken)
				token = self.GrantCondition(info.Condition);

			var wsb = wsbs.FirstEnabledConditionalTraitOrDefault();

			if (wsb == null)
				return;

			pending = PendingMake.External;
			wsb.PlayCustomAnimation(self, info.Sequence, () => MakeFinished(self, onComplete));

			foreach (var overlay in overlays)
				overlay.Forward();
		}

		public void Reverse(Actor self, Action onComplete)
		{
			if (token == Actor.InvalidConditionToken)
				token = self.GrantCondition(info.Condition);

			var wsb = wsbs.FirstEnabledConditionalTraitOrDefault();

			if (wsb == null)
				return;

			pending = PendingMake.External;
			wsb.PlayCustomAnimationBackwards(self, info.Sequence, () => MakeFinished(self, onComplete));

			foreach (var overlay in overlays)
				overlay.Reverse();
		}

		/// <summary>Attaches <paramref name="onComplete"/> again to a make animation that still plays.</summary>
		public void Continue(Actor self, Action onComplete)
		{
			var wsb = wsbs.FirstEnabledConditionalTraitOrDefault();
			if (wsb != null && PlaysMakeSequence(self, wsb))
				wsb.ContinueCustomAnimation(self, () => MakeFinished(self, onComplete));
		}

		// A callback that another animation took the place of does not come back after a load.
		bool PlaysMakeSequence(Actor self, WithSpriteBody wsb)
		{
			return wsb.DefaultAnimation.CurrentSequence?.Name == wsb.NormalizeSequence(self, info.Sequence);
		}

		void MakeFinished(Actor self, Action onComplete)
		{
			pending = PendingMake.None;
			self.World.AddFrameEndTask(w =>
			{
				if (token != Actor.InvalidConditionToken)
					token = self.RevokeCondition(token);

				// TODO: Rewrite this to use a trait notification for save game support
				onComplete();
			});
		}

		public void Reverse(Actor self, Activity activity, bool queued = true)
		{
			pendingActivity = activity;
			pendingQueued = queued;
			Reverse(self, QueuePendingActivity(self));
			pending = PendingMake.QueueActivity;

			foreach (var overlay in overlays)
				overlay.Reverse();
		}

		Action QueuePendingActivity(Actor self)
		{
			var activity = pendingActivity;
			var queued = pendingQueued;
			return () => QueueAfterReverse(self, activity, queued);
		}

		void QueueAfterReverse(Actor self, Activity activity, bool queued)
		{
			pendingActivity = null;

			// HACK: The actor remains alive and active for one tick before the followup activity
			// (sell/transform/etc) runs. This causes visual glitches that we attempt to minimize
			// by forcing the animation to frame 0 and regranting the make condition.
			// These workarounds will break the actor if the followup activity doesn't dispose it!
			wsbs.FirstEnabledConditionalTraitOrDefault()?.DefaultAnimation.PlayFetchIndex(info.Sequence, () => 0);

			token = self.GrantCondition(info.Condition);

			self.QueueActivity(queued, activity);
		}

		// TODO: Make this use Forward instead
		void INotifyDeployTriggered.Deploy(Actor self, bool skipMakeAnim) { PlayDeploy(self, skipMakeAnim, false); }

		// TODO: Make this use Reverse instead
		void INotifyDeployTriggered.Undeploy(Actor self, bool skipMakeAnim) { PlayDeploy(self, skipMakeAnim, true); }

		void PlayDeploy(Actor self, bool skipMakeAnim, bool undeploy)
		{
			if (skipMakeAnim)
			{
				foreach (var n in self.TraitsImplementing<INotifyDeployComplete>())
					NotifyDeployComplete(n, self, undeploy);

				return;
			}

			deployNotified = false;
			pending = undeploy ? PendingMake.Undeploy : PendingMake.Deploy;
			foreach (var wsb in wsbs)
			{
				if (wsb.IsTraitDisabled)
					continue;

				if (undeploy)
					wsb.PlayCustomAnimationBackwards(self, info.Sequence, () => DeployFinished(self, true));
				else
					wsb.PlayCustomAnimation(self, info.Sequence, () => DeployFinished(self, false));
			}

			foreach (var overlay in overlays)
			{
				if (undeploy)
					overlay.Reverse();
				else
					overlay.Forward();
			}
		}

		void DeployFinished(Actor self, bool undeploy)
		{
			pending = PendingMake.None;
			if (deployNotified)
				return;

			foreach (var n in self.TraitsImplementing<INotifyDeployComplete>())
			{
				NotifyDeployComplete(n, self, undeploy);
				deployNotified = true;
			}
		}

		static void NotifyDeployComplete(INotifyDeployComplete notify, Actor self, bool undeploy)
		{
			if (undeploy)
				notify.FinishedUndeploy(self);
			else
				notify.FinishedDeploy(self);
		}

		void IResumeAnimations.ResumeAnimations(Actor self)
		{
			switch (pending)
			{
				case PendingMake.Created:
					Continue(self, () => { });
					break;
				case PendingMake.QueueActivity:
					Continue(self, QueuePendingActivity(self));
					break;
				case PendingMake.Deploy:
				case PendingMake.Undeploy:
					var undeploy = pending == PendingMake.Undeploy;
					foreach (var wsb in wsbs)
						if (!wsb.IsTraitDisabled && PlaysMakeSequence(self, wsb))
							wsb.ContinueCustomAnimation(self, () => DeployFinished(self, undeploy));
					break;
			}
		}
	}
}
