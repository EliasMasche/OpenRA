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

namespace OpenRA.Mods.Common.Activities
{
	[SaveableActivity]
	public class MoveToDock : Activity
	{
		readonly DockClientManager dockClient;
		Actor dockHostActor;
		IDockHost dockHost;
		readonly INotifyDockClientMoving[] notifyDockClientMoving;
		readonly Color? dockLineColor;
		readonly MoveCooldownHelper moveCooldownHelper;
		readonly bool forceEnter;
		readonly bool ignoreOccupancy;

		bool dockingCancelled;

		public MoveToDock(Actor self, Actor dockHostActor = null, IDockHost dockHost = null,
			bool forceEnter = false, bool ignoreOccupancy = false, Color? dockLineColor = null)
		{
			dockClient = self.Trait<DockClientManager>();
			this.dockHostActor = dockHostActor;
			this.dockHost = dockHost;
			this.forceEnter = forceEnter;
			this.ignoreOccupancy = ignoreOccupancy;
			this.dockLineColor = dockLineColor;
			notifyDockClientMoving = self.TraitsImplementing<INotifyDockClientMoving>().ToArray();
			moveCooldownHelper = new MoveCooldownHelper(self.World, self.Trait<IMove>() as Mobile) { RetryIfDestinationBlocked = true };
		}

		internal MoveToDock(Actor self, SnapshotReader r, MiniYaml yaml)
		{
			dockClient = self.Trait<DockClientManager>();
			notifyDockClientMoving = self.TraitsImplementing<INotifyDockClientMoving>().ToArray();
			moveCooldownHelper = new MoveCooldownHelper(self.World, self.Trait<IMove>() as Mobile) { RetryIfDestinationBlocked = true };

			var n = yaml.ToDictionary();
			forceEnter = FieldLoader.GetValue<bool>("ForceEnter", n["ForceEnter"].Value);
			ignoreOccupancy = FieldLoader.GetValue<bool>("IgnoreOccupancy", n["IgnoreOccupancy"].Value);
			dockingCancelled = FieldLoader.GetValue<bool>("DockingCancelled", n["DockingCancelled"].Value);
			moveCooldownHelper.LoadState(n["Cooldown"]);

			var color = n["DockLineColor"].Value;
			if (!string.IsNullOrEmpty(color))
				dockLineColor = FieldLoader.GetValue<Color>("DockLineColor", color);

			r.DeferActor(n["DockHostActor"].Value, a =>
			{
				dockHostActor = a;
				dockHost = a?.TraitsImplementing<IDockHost>().FirstOrDefault();
			});
		}

		public override List<MiniYamlNode> SaveState(Actor self, SnapshotWriter w)
		{
			return
			[
				new("DockHostActor", w.ActorRef(dockHostActor)),
				new("ForceEnter", FieldSaver.FormatValue(forceEnter)),
				new("IgnoreOccupancy", FieldSaver.FormatValue(ignoreOccupancy)),
				new("DockingCancelled", FieldSaver.FormatValue(dockingCancelled)),
				new("DockLineColor", dockLineColor.HasValue ? FieldSaver.FormatValue(dockLineColor.Value) : ""),
				new("Cooldown", new MiniYaml("", moveCooldownHelper.SaveState()))
			];
		}

		protected override void OnFirstRun(Actor self)
		{
			if (dockClient.IsTraitDisabled)
				return;

			// We were ordered to dock to an actor but host was unspecified.
			if (dockHostActor != null && dockHost == null)
			{
				if (dockHostActor.IsDead || !dockHostActor.IsInWorld)
				{
					dockingCancelled = true;
					return;
				}

				var link = dockClient.AvailableDockHosts(dockHostActor, default, forceEnter, ignoreOccupancy)
					.ClosestDock(self, dockClient);

				if (link.HasValue)
					dockHost = link.Value.Trait;
				else
					dockingCancelled = true;
			}
		}

		public override bool Tick(Actor self)
		{
			if (IsCanceling)
				return true;

			if (dockingCancelled || dockClient.IsTraitDisabled)
			{
				Cancel(self, true);
				return true;
			}

			// Find the nearest DockHost if not explicitly ordered to a specific dock.
			if (dockHost == null || !dockHost.IsEnabledAndInWorld)
			{
				var host = dockClient.ClosestDock(null);
				if (host.HasValue)
				{
					dockHost = host.Value.Trait;
					dockHostActor = host.Value.Actor;
				}
				else
				{
					// No docks exist; check again after delay defined in dockClient.
					QueueChild(new Wait(dockClient.Info.SearchForDockDelay));
					return false;
				}
			}

			var result = moveCooldownHelper.Tick(false);
			if (result != null)
				return result.Value;

			if (dockClient.ReserveHost(dockHostActor, dockHost))
			{
				if (dockHost.QueueMoveActivity(this, dockHostActor, self, dockClient, moveCooldownHelper))
				{
					foreach (var ndcm in notifyDockClientMoving)
						ndcm.MovingToDock(self, dockHostActor, dockHost);

					return false;
				}

				dockHost.QueueDockActivity(this, dockHostActor, self, dockClient);
				return true;
			}
			else
			{
				foreach (var ndcm in notifyDockClientMoving)
					ndcm.MovementCancelled(self);

				// The dock explicitly chosen by the user is currently occupied. Wait and check again.
				QueueChild(new Wait(dockClient.Info.SearchForDockDelay));
				return false;
			}
		}

		public override void Cancel(Actor self, bool keepQueue = false)
		{
			dockClient.UnreserveHost();
			foreach (var ndcm in notifyDockClientMoving)
				ndcm.MovementCancelled(self);

			base.Cancel(self, keepQueue);
		}

		public override IEnumerable<TargetLineNode> TargetLineNodes(Actor self)
		{
			if (!dockLineColor.HasValue)
				yield break;

			if (dockHostActor != null)
				yield return new TargetLineNode(Target.FromActor(dockHostActor), dockLineColor.Value);
			else
			{
				if (dockClient.ReservedHostActor != null)
					yield return new TargetLineNode(Target.FromActor(dockClient.ReservedHostActor), dockLineColor.Value);
			}
		}
	}
}
