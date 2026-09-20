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
using OpenRA.GameSaves;
using OpenRA.Mods.Common.Traits;
using OpenRA.Traits;

namespace OpenRA.Mods.Cnc.Traits
{
	[TraitLocation(SystemActors.Player)]
	[Desc("Required for `GpsPower`. Attach this to the player actor.")]
	sealed class GpsWatcherInfo : TraitInfo
	{
		public override object Create(ActorInitializer init) { return new GpsWatcher(init.Self.Owner, this); }
	}

	interface IOnGpsRefreshed { void OnGpsRefresh(Actor self, Player player); }

	sealed class GpsWatcher : ISync, IPreventsShroudReset, ISaveState
	{
		const string LaunchedKey = "Launched";
		const string GrantedAlliesKey = "GrantedAllies";
		const string GrantedKey = "Granted";
		const string ExploredKey = "Explored";
		const string ActorsKey = "Actors";

		[VerifySync]
		public bool Launched { get; private set; }

		[VerifySync]
		public bool GrantedAllies { get; private set; }

		[VerifySync]
		public bool Granted { get; private set; }

		// Whether this watcher has explored the terrain (by becoming Launched, or an ally becoming Launched)
		[VerifySync]
		bool explored;

		readonly GpsWatcherInfo info;
		readonly Player owner;

		readonly List<Actor> actors = [];
		readonly HashSet<TraitPair<IOnGpsRefreshed>> notifyOnRefresh = [];

		public GpsWatcher(Player owner, GpsWatcherInfo info)
		{
			this.owner = owner;
			this.info = info;
		}

		public void GpsRemove(Actor atek)
		{
			actors.Remove(atek);
			RefreshGps(atek.Owner);
		}

		public void GpsAdd(Actor atek)
		{
			actors.Add(atek);
			RefreshGps(atek.Owner);
		}

		public void ReachedOrbit(Player launcher)
		{
			Launched = true;
			RefreshGps(launcher);
		}

		public void RefreshGps(Player launcher)
		{
			RefreshGranted();

			foreach (var i in launcher.World.ActorsWithTrait<GpsWatcher>())
				i.Trait.RefreshGranted();
		}

		void RefreshGranted()
		{
			var wasGranted = Granted;
			var wasGrantedAllies = GrantedAllies;
			var allyWatchers = owner.World.ActorsWithTrait<GpsWatcher>().Where(kv => kv.Actor.Owner.IsAlliedWith(owner)).ToList();

			Granted = actors.Count > 0 && Launched;
			GrantedAllies = allyWatchers.Any(w => w.Trait.Granted);

			if (!explored && (Launched || allyWatchers.Any(w => w.Trait.Launched)))
			{
				explored = true;
				owner.Shroud.ExploreAll();
			}

			if (wasGranted != Granted || wasGrantedAllies != GrantedAllies)
				foreach (var tp in notifyOnRefresh.ToList())
					tp.Trait.OnGpsRefresh(tp.Actor, owner);
		}

		bool IPreventsShroudReset.PreventShroudReset(Actor self)
		{
			return Granted || GrantedAllies;
		}

		public void RegisterForOnGpsRefreshed(Actor actor, IOnGpsRefreshed toBeNotified)
		{
			notifyOnRefresh.Add(new TraitPair<IOnGpsRefreshed>(actor, toBeNotified));
		}

		public void UnregisterForOnGpsRefreshed(Actor actor, IOnGpsRefreshed toBeNotified)
		{
			notifyOnRefresh.Remove(new TraitPair<IOnGpsRefreshed>(actor, toBeNotified));
		}

		TraitInfo ISaveState.SaveStateInfo => info;

		List<MiniYamlNode> ISaveState.SaveState(Actor self, SnapshotWriter w)
		{
			return
			[
				new(LaunchedKey, FieldSaver.FormatValue(Launched)),
				new(GrantedAlliesKey, FieldSaver.FormatValue(GrantedAllies)),
				new(GrantedKey, FieldSaver.FormatValue(Granted)),
				new(ExploredKey, FieldSaver.FormatValue(explored)),
				new(ActorsKey, actors.Select(w.ActorRef).JoinWith(", "))
			];
		}

		void ISaveState.LoadState(Actor self, MiniYaml data, SnapshotReader r)
		{
			var nodes = data.ToDictionary();
			if (nodes.TryGetValue(LaunchedKey, out var launched))
				Launched = FieldLoader.GetValue<bool>(LaunchedKey, launched.Value);

			if (nodes.TryGetValue(GrantedAlliesKey, out var allies))
				GrantedAllies = FieldLoader.GetValue<bool>(GrantedAlliesKey, allies.Value);

			if (nodes.TryGetValue(GrantedKey, out var granted))
				Granted = FieldLoader.GetValue<bool>(GrantedKey, granted.Value);

			if (nodes.TryGetValue(ExploredKey, out var exploredNode))
				explored = FieldLoader.GetValue<bool>(ExploredKey, exploredNode.Value);

			actors.Clear();

			if (!nodes.TryGetValue(ActorsKey, out var actorsNode) || string.IsNullOrEmpty(actorsNode.Value))
				return;

			foreach (var reference in actorsNode.Value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
				r.DeferActor(reference, a =>
				{
					if (a != null)
						actors.Add(a);
				});
		}
	}
}
