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
using System.Linq;
using OpenRA.Activities;
using OpenRA.GameSaves;
using OpenRA.Mods.Cnc.Traits;
using OpenRA.Mods.Common.Traits;
using OpenRA.Mods.Common.Traits.Render;
using OpenRA.Primitives;
using OpenRA.Traits;

namespace OpenRA.Mods.Cnc.Activities
{
	[SaveableActivity]
	public class Teleport : Activity
	{
		Actor teleporter;
		readonly int? maximumDistance;
		readonly bool killOnFailure;
		readonly BitSet<DamageType> killDamageTypes;
		CPos destination;
		readonly bool killCargo;
		readonly bool screenFlash;
		readonly string sound;

		public Teleport(Actor teleporter, CPos destination, int? maximumDistance,
			bool killCargo, bool screenFlash, string sound, bool interruptable = true,
			bool killOnFailure = false, BitSet<DamageType> killDamageTypes = default)
		{
			var max = teleporter.World.Map.Grid.MaximumTileSearchRange;
			if (maximumDistance > max)
				throw new InvalidOperationException($"Teleport distance cannot exceed the value of MaximumTileSearchRange ({max}).");

			this.teleporter = teleporter;
			this.destination = destination;
			this.maximumDistance = maximumDistance;
			this.killCargo = killCargo;
			this.screenFlash = screenFlash;
			this.sound = sound;
			this.killOnFailure = killOnFailure;
			this.killDamageTypes = killDamageTypes;

			if (!interruptable)
				IsInterruptible = false;
		}

		internal Teleport(Actor _, SnapshotReader r, MiniYaml yaml)
		{
			var n = yaml.ToDictionary();
			destination = FieldLoader.GetValue<CPos>("Destination", n["Destination"].Value);
			killCargo = FieldLoader.GetValue<bool>("KillCargo", n["KillCargo"].Value);
			screenFlash = FieldLoader.GetValue<bool>("ScreenFlash", n["ScreenFlash"].Value);
			sound = n["Sound"].Value;
			killOnFailure = FieldLoader.GetValue<bool>("KillOnFailure", n["KillOnFailure"].Value);
			killDamageTypes = FieldLoader.GetValue<BitSet<DamageType>>("KillDamageTypes", n["KillDamageTypes"].Value);

			var max = n["MaximumDistance"].Value;
			if (!string.IsNullOrEmpty(max))
				maximumDistance = FieldLoader.GetValue<int>("MaximumDistance", max);

			if (!FieldLoader.GetValue<bool>("Interruptible", n["Interruptible"].Value))
				IsInterruptible = false;

			r.DeferActor(n["Teleporter"].Value, a => teleporter = a);
		}

		public override List<MiniYamlNode> SaveState(Actor self, SnapshotWriter w)
		{
			return
			[
				new("Teleporter", w.ActorRef(teleporter)),
				new("Destination", FieldSaver.FormatValue(destination)),
				new("MaximumDistance", maximumDistance.HasValue ? FieldSaver.FormatValue(maximumDistance.Value) : ""),
				new("KillCargo", FieldSaver.FormatValue(killCargo)),
				new("ScreenFlash", FieldSaver.FormatValue(screenFlash)),
				new("Sound", sound ?? ""),
				new("KillOnFailure", FieldSaver.FormatValue(killOnFailure)),
				new("KillDamageTypes", FieldSaver.FormatValue(killDamageTypes)),
				new("Interruptible", FieldSaver.FormatValue(IsInterruptible))
			];
		}

		public override bool Tick(Actor self)
		{
			var pc = self.TraitOrDefault<PortableChrono>();
			if (teleporter == self && pc != null && (!pc.CanTeleport || IsCanceling))
			{
				if (killOnFailure)
					self.Kill(teleporter, killDamageTypes);

				return true;
			}

			var bestCell = ChooseBestDestinationCell(self, destination);
			if (bestCell == null)
			{
				if (killOnFailure)
					self.Kill(teleporter, killDamageTypes);

				return true;
			}

			destination = bestCell.Value;

			Game.Sound.Play(SoundType.World, sound, self.CenterPosition);
			Game.Sound.Play(SoundType.World, sound, self.World.Map.CenterOfCell(destination));

			self.Trait<IPositionable>().SetPosition(self, destination);
			self.Generation++;

			if (killCargo)
			{
				var cargo = self.TraitOrDefault<Cargo>();
				if (cargo != null && teleporter != null)
				{
					while (!cargo.IsEmpty())
					{
						var a = cargo.Unload(self);

						// Kill all the units that are unloaded into the void
						// Kill() handles kill and death statistics
						a.Kill(teleporter);
					}
				}
			}

			// Consume teleport charges if this wasn't triggered via chronosphere
			if (teleporter == self)
				pc?.ResetChargeTime();

			// Trigger screen desaturate effect
			if (screenFlash)
				foreach (var a in self.World.ActorsWithTrait<ChronoshiftPostProcessEffect>())
					a.Trait.Enable();

			if (teleporter != null && self != teleporter && !teleporter.Disposed)
			{
				var building = teleporter.TraitOrDefault<WithSpriteBody>();
				if (building != null && building.DefaultAnimation.HasSequence("active"))
					building.PlayCustomAnimation(teleporter, "active");
			}

			return true;
		}

		CPos? ChooseBestDestinationCell(Actor self, CPos destination)
		{
			if (teleporter == null)
				return null;

			var restrictTo = maximumDistance == null ? null : self.World.Map.FindTilesInCircle(self.Location, maximumDistance.Value).ToHashSet();

			if (maximumDistance != null)
				destination = restrictTo.MinBy(x => (x - destination).LengthSquared);

			var pos = self.Trait<IPositionable>();
			if (pos.CanEnterCell(destination) && teleporter.Owner.Shroud.IsExplored(destination))
				return destination;

			var max = maximumDistance ?? teleporter.World.Map.Grid.MaximumTileSearchRange;
			foreach (var tile in self.World.Map.FindTilesInCircle(destination, max))
			{
				if (teleporter.Owner.Shroud.IsExplored(tile)
					&& (restrictTo == null || restrictTo.Contains(tile))
					&& pos.CanEnterCell(tile))
					return tile;
			}

			return null;
		}
	}
}
