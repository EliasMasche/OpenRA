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

using OpenRA.Mods.Common.Effects;
using OpenRA.Primitives;
using OpenRA.Traits;

namespace OpenRA.Mods.Common.Traits
{
	interface IDelayedDemolition
	{
		void Demolish(Actor self, Actor saboteur, BitSet<DamageType> damageTypes);
	}

	sealed class DelayedDemolition : DelayedSimEffect
	{
		readonly IDelayedDemolition hut;
		readonly Actor self;
		readonly Actor saboteur;
		readonly BitSet<DamageType> damageTypes;

		public DelayedDemolition(IDelayedDemolition hut, Actor self, Actor saboteur, BitSet<DamageType> damageTypes, int delay)
			: base(delay)
		{
			this.hut = hut;
			this.self = self;
			this.saboteur = saboteur;
			this.damageTypes = damageTypes;
		}

		protected override void Run(World world)
		{
			hut.Demolish(self, saboteur, damageTypes);
		}
	}
}
