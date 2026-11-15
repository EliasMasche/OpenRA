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
using OpenRA.Mods.Common.Activities;
using OpenRA.Mods.Common.Effects;

namespace OpenRA.Mods.Common.Traits
{
	public interface IAircraftRunObserver
	{
		void EnteredTargetArea(Actor aircraft);
		void ExitedTargetArea(Actor aircraft);
		void RemovedFromWorld(Actor aircraft);
	}

	/// <summary>
	/// The clock of the beacon of an aircraft run. It runs out as the aircraft that the power measures with comes
	/// near the target.
	/// </summary>
	public sealed class AircraftRunBeaconClock : IBeaconClock
	{
		readonly Actor aircraft;
		readonly WPos target;
		readonly int offset;
		readonly int distance;

		public AircraftRunBeaconClock(Actor aircraft, WPos target, int offset, int distance)
		{
			this.aircraft = aircraft;
			this.target = target;
			this.offset = offset;
			this.distance = distance;
		}

		float IBeaconClock.Fraction => 1 - ((aircraft.CenterPosition - target).HorizontalLength - offset) * 1f / distance;
	}

	/// <summary>The state one support power run shares between its aircraft: which are over the target, the camera and the beacon.</summary>
	public sealed class SupportPowerAircraftRun : IAircraftRunObserver
	{
		public readonly Dictionary<Actor, bool> AircraftInRange = [];
		public Beacon Beacon;

		readonly Actor launcher;
		readonly WPos target;
		readonly string cameraActor;
		readonly int cameraRemoveDelay;
		readonly string arrivedSpeechNotification;
		readonly string arrivedTextNotification;
		Actor camera;

		public SupportPowerAircraftRun(Actor launcher, WPos target, string cameraActor, int cameraRemoveDelay,
			string arrivedSpeechNotification = null, string arrivedTextNotification = null)
		{
			this.launcher = launcher;
			this.target = target;
			this.cameraActor = cameraActor;
			this.cameraRemoveDelay = cameraRemoveDelay;
			this.arrivedSpeechNotification = arrivedSpeechNotification;
			this.arrivedTextNotification = arrivedTextNotification;
		}

		void IAircraftRunObserver.EnteredTargetArea(Actor aircraft)
		{
			var firstToArrive = !AircraftInRange.Any(kv => kv.Value);

			// Spawn a camera and remove the beacon when the first plane enters the target area
			if (cameraActor != null && camera == null && firstToArrive)
			{
				launcher.World.AddFrameEndTask(w =>
				{
					camera = w.CreateActor(cameraActor,
					[
						new LocationInit(launcher.World.Map.CellContaining(target)),
						new OwnerInit(launcher.Owner),
					]);
				});
			}

			RemoveBeacon();

			if (firstToArrive)
			{
				Game.Sound.PlayNotification(launcher.World.Map.Rules, launcher.Owner, "Speech",
					arrivedSpeechNotification, launcher.Owner.Faction.InternalName);

				TextNotificationsManager.AddTransientLine(launcher.Owner, arrivedTextNotification);
			}

			AircraftInRange[aircraft] = true;
		}

		void IAircraftRunObserver.ExitedTargetArea(Actor aircraft)
		{
			AircraftInRange[aircraft] = false;

			// Remove the camera when the final plane leaves the target area
			if (!AircraftInRange.Any(kv => kv.Value))
				RemoveCamera();
		}

		void IAircraftRunObserver.RemovedFromWorld(Actor aircraft)
		{
			AircraftInRange[aircraft] = false;

			// Checking for attack range is not relevant here because
			// aircraft may be shot down before entering the range.
			// If at the map's edge, they may be removed from world before leaving.
			if (AircraftInRange.All(kv => !kv.Key.IsInWorld))
			{
				RemoveCamera();
				RemoveBeacon();
			}
		}

		void RemoveCamera()
		{
			if (camera == null)
				return;

			camera.QueueActivity(new Wait(cameraRemoveDelay));
			camera.QueueActivity(new RemoveSelf());
		}

		void RemoveBeacon()
		{
			var beacon = Beacon;
			if (beacon == null)
				return;

			// The run has no beacon from now on, so a save does not keep the one that went away.
			Beacon = null;
			launcher.World.AddFrameEndTask(w => w.Remove(beacon));
		}
	}
}
