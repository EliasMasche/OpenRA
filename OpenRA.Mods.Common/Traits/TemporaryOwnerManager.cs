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
using OpenRA.GameSaves;
using OpenRA.Primitives;
using OpenRA.Traits;

namespace OpenRA.Mods.Common.Traits
{
	[Desc("Interacts with the ChangeOwner warhead.",
		"Displays a bar how long this actor is affected and reverts back to the old owner on temporary changes.")]
	public class TemporaryOwnerManagerInfo : TraitInfo
	{
		public readonly Color BarColor = Color.Orange;

		public override object Create(ActorInitializer init) { return new TemporaryOwnerManager(init.Self, this); }
	}

	public class TemporaryOwnerManager : ISelectionBar, ITick, ISync, INotifyOwnerChanged, ISaveState
	{
		const string RemainingKey = "Remaining";
		const string DurationKey = "Duration";
		const string OriginalOwnerKey = "OriginalOwner";
		const string ChangingOwnerKey = "ChangingOwner";

		readonly TemporaryOwnerManagerInfo info;

		Player originalOwner;
		Player changingOwner;

		[VerifySync]
		int remaining = -1;
		int duration;

		public TemporaryOwnerManager(Actor self, TemporaryOwnerManagerInfo info)
		{
			this.info = info;
			originalOwner = self.Owner;
		}

		public void ChangeOwner(Actor self, Player newOwner, int duration)
		{
			remaining = this.duration = duration;
			changingOwner = newOwner;
			self.ChangeOwner(newOwner);
		}

		void ITick.Tick(Actor self)
		{
			if (!self.IsInWorld)
				return;

			if (--remaining == 0)
			{
				changingOwner = originalOwner;
				self.ChangeOwner(originalOwner);
				self.CancelActivity(); // Stop shooting, you have got new enemies
			}
		}

		void INotifyOwnerChanged.OnOwnerChanged(Actor self, Player oldOwner, Player newOwner)
		{
			if (changingOwner == null || changingOwner != newOwner)
				originalOwner = newOwner; // It wasn't a temporary change, so we need to update here
			else
				changingOwner = null; // It was triggered by this trait: reset
		}

		float ISelectionBar.GetValue()
		{
			if (remaining <= 0)
				return 0;

			return (float)remaining / duration;
		}

		Color ISelectionBar.GetColor()
		{
			return info.BarColor;
		}

		bool ISelectionBar.DisplayWhenEmpty => false;

		TraitInfo ISaveState.SaveStateInfo => info;

		List<MiniYamlNode> ISaveState.SaveState(Actor self, SnapshotWriter w)
		{
			return
			[
				new(RemainingKey, FieldSaver.FormatValue(remaining)),
				new(DurationKey, FieldSaver.FormatValue(duration)),
				new(OriginalOwnerKey, w.PlayerRef(originalOwner)),
				new(ChangingOwnerKey, w.PlayerRef(changingOwner))
			];
		}

		void ISaveState.LoadState(Actor self, MiniYaml data, SnapshotReader r)
		{
			var nodes = data.ToDictionary();
			if (nodes.TryGetValue(RemainingKey, out var rem))
				remaining = FieldLoader.GetValue<int>(RemainingKey, rem.Value);

			if (nodes.TryGetValue(DurationKey, out var dur))
				duration = FieldLoader.GetValue<int>(DurationKey, dur.Value);

			if (nodes.TryGetValue(OriginalOwnerKey, out var original))
				originalOwner = r.ResolvePlayer(original.Value);

			if (nodes.TryGetValue(ChangingOwnerKey, out var changing))
				changingOwner = r.ResolvePlayer(changing.Value);
		}
	}
}
