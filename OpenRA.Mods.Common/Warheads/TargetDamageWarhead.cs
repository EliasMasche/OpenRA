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

using OpenRA.GameRules;
using OpenRA.Mods.Common.Traits;
using OpenRA.Traits;

namespace OpenRA.Mods.Common.Warheads
{
	[Desc("Apply damage to the targeted actor.")]
	public class TargetDamageWarhead : DamageWarhead
	{
		[Desc("Damage will be applied to actors in this area. A value of zero means only targeted actor will be damaged.")]
		public readonly WDist Spread = WDist.Zero;

		protected override void DoImpact(WPos pos, Player firedBy, Actor firedByActor, WarheadArgs args)
		{
			if (Spread == WDist.Zero)
				return;

			var world = args.World;
			var debugVis = world.WorldActor.TraitOrDefault<DebugVisualizations>();
			if (debugVis != null && debugVis.CombatGeometry)
				world.WorldActor.Trait<WarheadDebugOverlay>().AddImpact(pos, [WDist.Zero, Spread], DebugOverlayColor);

			foreach (var victim in world.FindActorsOnCircle(pos, Spread))
			{
				if (!IsValidAgainst(victim, firedBy, firedByActor))
					continue;

				HitShape closestActiveShape = null;
				var closestDistance = int.MaxValue;

				// PERF: Avoid using TraitsImplementing<HitShape> that needs to find the actor in the trait dictionary.
				foreach (var targetPos in victim.EnabledTargetablePositions)
				{
					if (targetPos is HitShape hitshape)
					{
						var distance = hitshape.DistanceFromEdge(victim, pos).Length;
						if (distance < closestDistance)
						{
							closestDistance = distance;
							closestActiveShape = hitshape;
						}
					}
				}

				// Cannot be damaged without an active HitShape.
				if (closestActiveShape == null)
					continue;

				// Cannot be damaged if HitShape is outside Spread.
				if (closestDistance > Spread.Length)
					continue;

				InflictDamage(victim, firedBy, firedByActor, closestActiveShape, args);
			}
		}
	}
}
