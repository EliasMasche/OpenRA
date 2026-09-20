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
using Eluant;
using OpenRA.Activities;
using OpenRA.Mods.Common.Activities;
using OpenRA.Mods.Common.Scripting.Snapshot;
using OpenRA.Mods.Common.Traits;
using OpenRA.Scripting;
using OpenRA.Traits;

namespace OpenRA.Mods.Common.Scripting
{
	[ScriptPropertyGroup("Combat")]
	public class CombatProperties : ScriptActorProperties, Requires<AttackBaseInfo>, Requires<IMoveInfo>
	{
		public CombatProperties(ScriptContext context, Actor self)
			: base(context, self)
		{
		}

		[ScriptActorPropertyActivity]
		[Desc("Ignoring visibility, find the closest hostile target and attack move to within 2 cells of it.")]
		public void Hunt()
		{
			Self.QueueActivity(new Hunt(Self));
		}

		[ScriptActorPropertyActivity]
		[Desc("Move to a cell, but stop and attack anything within range on the way. " +
			"closeEnough defines an optional range (in cells) that will be considered " +
			"close enough to complete the activity.")]
		public void AttackMove(CPos cell, int closeEnough = 0)
		{
			Self.QueueActivity(new AttackMoveActivity(Self, MoveSpec.ToCellAt(cell, closeEnough)));
		}

		[ScriptActorPropertyActivity]
		[Desc("Patrol along a set of given waypoints. The action is repeated by default, " +
			"and the actor will wait for `wait` ticks at each waypoint.")]
		public void Patrol(CPos[] waypoints, bool loop = true, int wait = 0)
		{
			foreach (var wpt in waypoints)
			{
				Self.QueueActivity(new AttackMoveActivity(Self, MoveSpec.ToCellAt(wpt, 2)));
				Self.QueueActivity(new Wait(wait));
			}

			if (loop)
				Self.QueueActivity(new LuaPatrol(waypoints, wait));
		}

		[ScriptActorPropertyActivity]
		[Desc("Patrol along a set of given waypoints until a condition becomes true. " +
			"The actor will wait for `wait` ticks at each waypoint. " +
			"The callback function will be called as func(self: actor):boolean.")]
		public void PatrolUntil(CPos[] waypoints, [ScriptEmmyTypeOverride("fun(self: actor):boolean")] LuaFunction func, int wait = 0)
		{
			Patrol(waypoints, false, wait);

			if (!func.Call(Self.ToLuaValue(Context)).First().ToBoolean())
				return;

			Self.QueueActivity(new LuaPatrolUntil(waypoints, wait, func, Context));
		}
	}

	[ScriptPropertyGroup("Combat")]
	public class GeneralCombatProperties : ScriptActorProperties, Requires<AttackBaseInfo>
	{
		readonly AttackBase[] attackBases;

		public GeneralCombatProperties(ScriptContext context, Actor self)
			: base(context, self)
		{
			attackBases = self.TraitsImplementing<AttackBase>().ToArray();
		}

		[Desc("Attack the target actor. The target actor needs to be visible.")]
		public void Attack(Actor targetActor, bool allowMove = true, bool forceAttack = false)
		{
			var target = Target.FromActor(targetActor);
			if (!target.IsValidFor(Self))
				Log.Write("lua", $"{targetActor} is an invalid target for {Self}!");

			if (!targetActor.Info.HasTraitInfo<FrozenUnderFogInfo>() && !targetActor.CanBeViewedByPlayer(Self.Owner))
				Log.Write("lua", $"{targetActor} is not revealed for player {Self.Owner}!");

			foreach (var attack in attackBases)
				attack.AttackTarget(target, AttackSource.Default, true, allowMove, forceAttack);
		}

		[Desc("Checks if the targeted actor is a valid target for this actor.")]
		public bool CanTarget(Actor targetActor)
		{
			return Target.FromActor(targetActor).IsValidFor(Self);
		}
	}
}
