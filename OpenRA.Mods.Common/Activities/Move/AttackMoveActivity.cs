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
using OpenRA.Mods.Common.Traits;
using OpenRA.Traits;

namespace OpenRA.Mods.Common.Activities
{
	[SaveableActivity]
	public class AttackMoveActivity : Activity
	{
		const string SpecKey = "Spec";
		const string IsAssaultMoveKey = "IsAssaultMove";
		const string RunningMoveActivityKey = "RunningMoveActivity";
		const string TargetKey = "Target";

		readonly bool isAssaultMove;
		readonly AutoTarget autoTarget;
		readonly AttackMove attackMove;
		readonly IMove move;

		readonly MoveSpec spec;
		readonly Func<Activity> getMove;

		bool runningMoveActivity = false;
		int token = Actor.InvalidConditionToken;
		Target target = Target.Invalid;

		public AttackMoveActivity(Actor self, MoveSpec spec, bool assaultMoving = false)
		{
			this.spec = spec;
			move = self.Trait<IMove>();
			autoTarget = self.TraitOrDefault<AutoTarget>();
			attackMove = self.TraitOrDefault<AttackMove>();
			isAssaultMove = assaultMoving;
			ChildHasPriority = false;
		}

		public AttackMoveActivity(Actor self, Func<Activity> getMove, bool assaultMoving = false)
		{
			this.getMove = getMove;
			move = self.Trait<IMove>();
			autoTarget = self.TraitOrDefault<AutoTarget>();
			attackMove = self.TraitOrDefault<AttackMove>();
			isAssaultMove = assaultMoving;
			ChildHasPriority = false;
		}

		internal AttackMoveActivity(Actor self, SnapshotReader r, MiniYaml yaml)
		{
			move = self.Trait<IMove>();
			autoTarget = self.TraitOrDefault<AutoTarget>();
			attackMove = self.TraitOrDefault<AttackMove>();
			ChildHasPriority = false;

			var nodes = yaml.ToDictionary();
			isAssaultMove = FieldLoader.GetValue<bool>(IsAssaultMoveKey, nodes[IsAssaultMoveKey].Value);
			runningMoveActivity = FieldLoader.GetValue<bool>(RunningMoveActivityKey, nodes[RunningMoveActivityKey].Value);
			spec = MoveSpec.LoadState(nodes[SpecKey], r);

			if (attackMove != null && autoTarget != null)
				GrantMoveCondition(self);

			r.DeferTarget(nodes[TargetKey].Value, t => target = t);
		}

		Activity NextMove(Actor self)
		{
			return spec != null ? spec.Resolve(self, move) : getMove();
		}

		protected override void OnFirstRun(Actor self)
		{
			if (attackMove == null || autoTarget == null)
			{
				QueueChild(NextMove(self));
				return;
			}

			GrantMoveCondition(self);
		}

		public override bool Tick(Actor self)
		{
			if (IsCanceling || attackMove == null || autoTarget == null)
				return TickChild(self);

			// We are currently not attacking, so scan for new targets.
			if (ChildActivity == null || runningMoveActivity)
			{
				// Use the standard ScanForTarget rate limit while we are running the move activity to save performance.
				// Override the rate limit if our attack activity has completed so we can immediately acquire a new target instead of moving.
				target = autoTarget.ScanForTarget(self, autoTarget.AllowMove, true, !runningMoveActivity);

				// Cancel the current move activity and queue attack activities if we find a new target.
				if (target.Type != TargetType.Invalid)
				{
					runningMoveActivity = false;
					ChildActivity?.Cancel(self);

					foreach (var ab in autoTarget.ActiveAttackBases)
						QueueChild(ab.GetAttackActivity(self, AttackSource.AttackMove, target, autoTarget.AllowMove, false));
				}

				// Continue with the move activity (or queue a new one) when there are no targets.
				if (ChildActivity == null)
				{
					runningMoveActivity = true;
					QueueChild(NextMove(self));
				}
			}

			// If the move activity finished, we have reached our destination and there are no more enemies on our path.
			return TickChild(self) && runningMoveActivity;
		}

		protected override void OnLastRun(Actor self)
		{
			if (token != Actor.InvalidConditionToken)
				token = self.RevokeCondition(token);
		}

		public override IEnumerable<Target> GetTargets(Actor self)
		{
			if (ChildActivity != null)
				return ChildActivity.GetTargets(self);

			return Target.None;
		}

		public override IEnumerable<TargetLineNode> TargetLineNodes(Actor self)
		{
			foreach (var n in NextMove(self).TargetLineNodes(self))
				yield return n;
		}

		void GrantMoveCondition(Actor self)
		{
			token = self.GrantCondition(isAssaultMove
				? attackMove.Info.AssaultMoveCondition
				: attackMove.Info.AttackMoveCondition);
		}

		public override List<MiniYamlNode> SaveState(Actor self, SnapshotWriter w)
		{
			if (spec == null)
				return null;

			return
			[
				new(SpecKey, spec.SaveState(w)),
				new(IsAssaultMoveKey, FieldSaver.FormatValue(isAssaultMove)),
				new(RunningMoveActivityKey, FieldSaver.FormatValue(runningMoveActivity)),
				new(TargetKey, w.TargetRef(target))
			];
		}
	}
}
