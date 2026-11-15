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

using OpenRA.Activities;
using OpenRA.Mods.Common.Traits;
using OpenRA.Primitives;

namespace OpenRA.Mods.Common.Activities
{
	public interface IDeliversProduction
	{
		void DoProduction(Actor self, ActorInfo producee, ExitInfo exitinfo, string productionType, TypeDictionary inits);
		void PlayDeliveredNotifications(Actor self);
	}

	/// <summary>Queued on the delivery aircraft: hands the delivered unit over to its producer, or refunds it if the producer is gone.</summary>
	public sealed class CompleteProductionDelivery : Activity
	{
		readonly IDeliversProduction production;
		readonly Actor producer;
		readonly Player owner;
		readonly ActorInfo producee;
		readonly ExitInfo exit;
		readonly string productionType;
		readonly TypeDictionary inits;
		readonly int refundableValue;

		public CompleteProductionDelivery(IDeliversProduction production, Actor producer, Player owner, ActorInfo producee, ExitInfo exit,
			string productionType, TypeDictionary inits, int refundableValue)
		{
			this.production = production;
			this.producer = producer;
			this.owner = owner;
			this.producee = producee;
			this.exit = exit;
			this.productionType = productionType;
			this.inits = inits;
			this.refundableValue = refundableValue;
		}

		public override bool Tick(Actor self)
		{
			if (!producer.IsInWorld || producer.IsDead)
			{
				owner.PlayerActor.Trait<PlayerResources>().RefundCash(refundableValue);
				return true;
			}

			foreach (var cargo in producer.TraitsImplementing<INotifyDelivery>())
				cargo.Delivered(producer);

			producer.World.AddFrameEndTask(_ => production.DoProduction(producer, producee, exit, productionType, inits));
			production.PlayDeliveredNotifications(producer);
			return true;
		}
	}
}
