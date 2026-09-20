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
using System.IO;
using System.Linq;
using OpenRA.Activities;

namespace OpenRA.GameSaves
{
	public interface IActivityReferences
	{
		IEnumerable<(string Key, Activity Activity)> SaveReferences();

		void LoadReference(string key, Activity activity);
	}

	public interface IProvidesMovePath
	{
		void ProvideMovePath(Actor self, Activity child);
	}

	/// <summary>Writes an activity tree to YAML, and rebuilds it during a restore.</summary>
	/// <remarks>
	/// Every activity receives a number, and the child link and the next link become numbers. The writer
	/// drops the whole tree when one activity cannot be saved, because the activities that remain would
	/// keep a link to an activity that no longer exists.
	/// </remarks>
	public sealed class ActivitySerializer
	{
		public const string ActivitiesKey = "Activities";

		const string RootKey = "Root";
		const string ReferencesKey = "References";
		const string StateKey = "State";
		const string FirstRunCompletedKey = "FirstRunCompleted";
		const string FinishingKey = "Finishing";
		const string IsInterruptibleKey = "IsInterruptible";
		const string ChildHasPriorityKey = "ChildHasPriority";
		const string ChildKey = "Child";
		const string NextKey = "Next";

		readonly ActivityRegistry registry;

		public int DroppedTrees { get; private set; }

		public ActivitySerializer(ActivityRegistry registry)
		{
			ArgumentNullException.ThrowIfNull(registry);

			this.registry = registry;
		}

		#region Save

		public List<MiniYamlNode> Save(Actor self, Activity root, SnapshotWriter w)
		{
			ArgumentNullException.ThrowIfNull(w);

			if (root == null)
				return null;

			var ids = new Dictionary<Activity, int>();
			AssignIds(root, ids);

			var nodes = new List<MiniYamlNode>();
			foreach (var (activity, id) in ids)
			{
				var state = SaveOne(self, w, activity, ids);
				if (state == null)
				{
					DroppedTrees++;
					return null;
				}

				nodes.Add(new MiniYamlNode(id.ToStringInvariant(), ActivityRegistry.NameOf(activity), state));
			}

			nodes.Add(new MiniYamlNode(RootKey, ids[root].ToStringInvariant()));
			return nodes;
		}

		static void AssignIds(Activity activity, Dictionary<Activity, int> ids)
		{
			var pending = new Stack<Activity>();
			pending.Push(activity);

			while (pending.Count > 0)
			{
				var a = pending.Pop();

				if (a == null || !ids.TryAdd(a, ids.Count + 1))
					continue;

				var (_, _, _, child, next) = a.SaveBaseState();
				if (next != null)
					pending.Push(next);

				if (child != null)
					pending.Push(child);
			}
		}

		List<MiniYamlNode> SaveOne(Actor self, SnapshotWriter w, Activity activity, Dictionary<Activity, int> ids)
		{
			if (!registry.IsSaveable(activity))
				return null;

			var state = activity.SaveState(self, w);
			if (state == null)
				return null;

			var (activityState, firstRunCompleted, finishing, child, next) = activity.SaveBaseState();

			var nodes = new List<MiniYamlNode>
			{
				new(StateKey, activityState.ToString()),
				new(FirstRunCompletedKey, FieldSaver.FormatValue(firstRunCompleted)),
				new(FinishingKey, FieldSaver.FormatValue(finishing)),
				new(IsInterruptibleKey, FieldSaver.FormatValue(activity.IsInterruptible)),
				new(ChildHasPriorityKey, FieldSaver.FormatValue(activity.ChildHasPriority)),
				new(ChildKey, Ref(child, ids)),
				new(NextKey, Ref(next, ids))
			};

			nodes.AddRange(state);

			if (activity is IActivityReferences refs)
			{
				var refNodes = refs.SaveReferences()
					.Select(x => new MiniYamlNode(x.Key, Ref(x.Activity, ids)))
					.ToList();

				if (refNodes.Count > 0)
					nodes.Add(new MiniYamlNode(ReferencesKey, new MiniYaml("", refNodes)));
			}

			var keys = new HashSet<string>();
			foreach (var node in nodes)
			{
				if (keys.Add(node.Key))
					continue;

				// The reader builds a dictionary from these nodes, so a duplicate key
				// would lose one of the two values.
				DroppedTrees++;
				Log.Write("debug",
					$"Activity {ActivityRegistry.NameOf(activity)} on actor {self.ActorID} writes state key " +
					$"'{node.Key}' more than once, so its activity tree cannot be restored and was dropped.");
				return null;
			}

			return nodes;
		}

		static string Ref(Activity activity, Dictionary<Activity, int> ids)
		{
			if (activity == null || !ids.TryGetValue(activity, out var id))
				return SnapshotRefs.Null;

			return id.ToStringInvariant();
		}

		#endregion

		#region Restore

		public void Restore(Actor self, MiniYaml data, SnapshotReader r, Action<Activity> setRoot)
		{
			ArgumentNullException.ThrowIfNull(data);
			ArgumentNullException.ThrowIfNull(r);
			ArgumentNullException.ThrowIfNull(setRoot);

			var activities = new Dictionary<int, Activity>();
			var states = new Dictionary<int, MiniYaml>();
			var rootID = 0;

			foreach (var node in data.Nodes)
			{
				if (node.Key == RootKey)
				{
					rootID = ParseID(node.Value.Value);
					continue;
				}

				var id = ParseID(node.Key);
				var activity = registry.Create(node.Value.Value, self, r, node.Value);
				if (!activities.TryAdd(id, activity))
					throw new InvalidDataException($"Saved activity id {id} appears twice on {Describe(self)}.");

				states[id] = node.Value;
			}

			if (activities.Count == 0)
				return;

			if (!activities.TryGetValue(rootID, out var root))
				throw new InvalidDataException($"Saved activity tree on {Describe(self)} names a missing root {rootID}.");

			foreach (var (id, activity) in activities)
				RestoreOne(self, activity, states[id], activities);

			setRoot(root);
		}

		static string Describe(Actor self)
		{
			return self != null ? $"actor {self.ActorID}" : "actor";
		}

		static void RestoreOne(Actor self, Activity activity, MiniYaml yaml, Dictionary<int, Activity> activities)
		{
			var nodes = yaml.ToDictionary();

			var state = Value(nodes, StateKey, ActivityState.Active);
			var firstRunCompleted = Value(nodes, FirstRunCompletedKey, true);

			var hasStarted = state != ActivityState.Queued;
			if (hasStarted != firstRunCompleted)
				throw new InvalidDataException(
					$"Saved activity {ActivityRegistry.NameOf(activity)} on {Describe(self)} is {state} " +
					$"with FirstRunCompleted {firstRunCompleted}.");

			activity.RestoreLinks(
				Resolve(self, nodes, ChildKey, activities),
				Resolve(self, nodes, NextKey, activities));

			activity.RestoreBaseState(
				state,
				firstRunCompleted,
				Value(nodes, FinishingKey, false),
				Value(nodes, IsInterruptibleKey, activity.IsInterruptible),
				Value(nodes, ChildHasPriorityKey, activity.ChildHasPriority));

			if (activity is IActivityReferences refs && nodes.TryGetValue(ReferencesKey, out var refYaml))
			{
				var refNodes = refYaml.ToDictionary();
				foreach (var key in refNodes.Keys)
					refs.LoadReference(key, Resolve(self, refNodes, key, activities));
			}

			if (activity is IProvidesMovePath provider)
			{
				var child = Resolve(self, nodes, ChildKey, activities);
				if (child != null)
					provider.ProvideMovePath(self, child);
			}
		}

		static Activity Resolve(Actor self, Dictionary<string, MiniYaml> nodes, string key, Dictionary<int, Activity> activities)
		{
			if (!nodes.TryGetValue(key, out var node) || node.Value == SnapshotRefs.Null || string.IsNullOrEmpty(node.Value))
				return null;

			var id = ParseID(node.Value);
			if (!activities.TryGetValue(id, out var activity))
				throw new InvalidDataException($"Saved activity on {Describe(self)} links to a missing activity {id}.");

			return activity;
		}

		static T Value<T>(Dictionary<string, MiniYaml> nodes, string key, T fallback)
		{
			if (!nodes.TryGetValue(key, out var node) || string.IsNullOrEmpty(node.Value))
				return fallback;

			return FieldLoader.GetValue<T>(key, node.Value);
		}

		static int ParseID(string value)
		{
			if (!int.TryParse(value, System.Globalization.NumberStyles.None, System.Globalization.NumberFormatInfo.InvariantInfo, out var id) || id < 1)
				throw new InvalidDataException($"Malformed saved activity id '{value}'.");

			return id;
		}

		#endregion
	}
}
