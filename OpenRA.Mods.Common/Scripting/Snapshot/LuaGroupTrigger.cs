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
using Eluant;
using OpenRA.GameSaves;
using OpenRA.Scripting;

namespace OpenRA.Mods.Common.Scripting.Snapshot
{
	public enum LuaGroupTriggerKind
	{
		AllKilled,
		AnyKilled,
		AllRemovedFromWorld,
		KilledOrCaptured,
		AllKilledOrCaptured
	}

	public sealed class LuaGroupTrigger
	{
		const string KindKey = "Kind";
		const string HandleKey = "Handle";
		const string GroupKey = "Group";
		const string WatchedKey = "Watched";
		const string CalledKey = "Called";

		public readonly LuaGroupTriggerKind Kind;

		readonly List<Actor> group = [];

		readonly List<Actor> watched = [];

		LuaFunction function;
		bool called;

		int savedHandle = -1;

		LuaGroupTrigger(LuaGroupTriggerKind kind)
		{
			Kind = kind;
		}

		public static LuaGroupTrigger Create(LuaGroupTriggerKind kind, IEnumerable<Actor> actors, LuaFunction function)
		{
			var trigger = new LuaGroupTrigger(kind) { function = (LuaFunction)function.CopyReference() };
			trigger.group.AddRange(actors);
			trigger.watched.AddRange(trigger.group);

			return trigger;
		}

		public bool Spent => called || function == null;

		public void Subscribe(ScriptContext context)
		{
			foreach (var a in watched)
			{
				if (a == null || a.Disposed)
					continue;

				var triggers = TriggerGlobal.GetScriptTriggers(a);

				switch (Kind)
				{
					case LuaGroupTriggerKind.AllKilled:
					case LuaGroupTriggerKind.AnyKilled:
						triggers.OnKilledInternal += m => OnMember(context, m);
						break;

					case LuaGroupTriggerKind.AllRemovedFromWorld:
						triggers.OnRemovedInternal += m => OnMember(context, m);
						triggers.OnAddedInternal += m => OnMemberReturned(m);
						break;

					case LuaGroupTriggerKind.KilledOrCaptured:
					case LuaGroupTriggerKind.AllKilledOrCaptured:
						triggers.OnKilledInternal += m => OnMember(context, m);
						triggers.OnCapturedInternal += m => OnMember(context, m);
						break;
				}
			}
		}

		void OnMember(ScriptContext context, Actor member)
		{
			try
			{
				if (called || function == null)
					return;

				switch (Kind)
				{
					case LuaGroupTriggerKind.AnyKilled:
					case LuaGroupTriggerKind.KilledOrCaptured:
						Fire(context, member);
						break;

					case LuaGroupTriggerKind.AllKilled:
						group.Remove(member);
						if (group.Count == 0)
							Fire(context, member);
						break;

					case LuaGroupTriggerKind.AllRemovedFromWorld:
					case LuaGroupTriggerKind.AllKilledOrCaptured:
						if (group.Remove(member) && group.Count == 0)
							Fire(context, member);
						break;
				}
			}
			catch (Exception e)
			{
				context.FatalError(e);
			}
		}

		void OnMemberReturned(Actor member)
		{
			if (called || !watched.Contains(member) || group.Contains(member))
				return;

			group.Add(member);
		}

		void Fire(ScriptContext context, Actor member)
		{
			called = true;

			if (Kind == LuaGroupTriggerKind.AnyKilled)
			{
				using (var killed = member.ToLuaValue(context))
					function.Call(killed).Dispose();
			}
			else
				function.Call().Dispose();

			function.Dispose();
			function = null;
		}

		public List<MiniYamlNode> SaveState(ScriptContext context, SnapshotWriter w)
		{
			return
			[
				new(KindKey, FieldSaver.FormatValue(Kind)),
				new(HandleKey, FieldSaver.FormatValue(context.RegisterHandle(function))),
				new(CalledKey, FieldSaver.FormatValue(called)),

				new(GroupKey, group.Where(a => a != null && !a.Disposed).Select(w.ActorRef).JoinWith(",")),
				new(WatchedKey, watched.Where(a => a != null && !a.Disposed).Select(w.ActorRef).JoinWith(","))
			];
		}

		public static LuaGroupTrigger LoadState(MiniYaml yaml, SnapshotReader r)
		{
			var nodes = yaml.ToDictionary();
			var trigger = new LuaGroupTrigger(FieldLoader.GetValue<LuaGroupTriggerKind>(KindKey, nodes[KindKey].Value))
			{
				savedHandle = FieldLoader.GetValue<int>(HandleKey, nodes[HandleKey].Value),
				called = FieldLoader.GetValue<bool>(CalledKey, nodes[CalledKey].Value)
			};

			Defer(r, nodes, GroupKey, trigger.group);
			Defer(r, nodes, WatchedKey, trigger.watched);

			return trigger;
		}

		static void Defer(SnapshotReader r, Dictionary<string, MiniYaml> nodes, string key, List<Actor> into)
		{
			if (!nodes.TryGetValue(key, out var value) || string.IsNullOrEmpty(value.Value))
				return;

			foreach (var reference in value.Value.Split(',', StringSplitOptions.RemoveEmptyEntries))
				r.DeferActor(reference, a => { if (a != null) into.Add(a); });
		}

		public void Restore(ScriptContext context)
		{
			function = (LuaFunction)context.ResolveHandle(savedHandle).CopyReference();

			Subscribe(context);
		}
	}
}
