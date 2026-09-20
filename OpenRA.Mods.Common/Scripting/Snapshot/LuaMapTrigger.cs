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
using OpenRA.Mods.Common.Traits;
using OpenRA.Scripting;

namespace OpenRA.Mods.Common.Scripting.Snapshot
{
	public enum LuaMapTriggerKind
	{
		EnteredFootprint,
		ExitedFootprint,
		EnteredProximity,
		ExitedProximity
	}

	public sealed class LuaMapTrigger
	{
		const string IdKey = "Id";
		const string KindKey = "Kind";
		const string HandleKey = "Handle";
		const string CellsKey = "Cells";
		const string PosKey = "Pos";
		const string RangeKey = "Range";
		const string OccupantsKey = "Occupants";

		public readonly LuaMapTriggerKind Kind;
		public readonly CPos[] Cells;
		public readonly WPos Pos;
		public readonly WDist Range;

		public int Id { get; private set; }

		public LuaFunction Function { get; private set; }

		int savedHandle = -1;
		readonly List<Actor> savedOccupants = [];

		LuaMapTrigger(LuaMapTriggerKind kind, CPos[] cells, WPos pos, WDist range)
		{
			Kind = kind;
			Cells = cells;
			Pos = pos;
			Range = range;
		}

		public static LuaMapTrigger Footprint(LuaMapTriggerKind kind, CPos[] cells, LuaFunction function)
		{
			return new LuaMapTrigger(kind, cells, WPos.Zero, WDist.Zero) { Function = function };
		}

		public static LuaMapTrigger Proximity(LuaMapTriggerKind kind, WPos pos, WDist range, LuaFunction function)
		{
			return new LuaMapTrigger(kind, null, pos, range) { Function = function };
		}

		public void SetId(int id)
		{
			Id = id;
		}

		bool IsFootprint => Kind == LuaMapTriggerKind.EnteredFootprint || Kind == LuaMapTriggerKind.ExitedFootprint;

		bool IsEntry => Kind == LuaMapTriggerKind.EnteredFootprint || Kind == LuaMapTriggerKind.EnteredProximity;

		public List<MiniYamlNode> SaveState(World world, ScriptContext context, SnapshotWriter w)
		{
			var actorMap = world.WorldActor.Trait<ActorMap>();
			var occupants = IsFootprint ? actorMap.CellTriggerOccupants(Id) : actorMap.ProximityTriggerOccupants(Id);

			var nodes = new List<MiniYamlNode>
			{
				new(IdKey, FieldSaver.FormatValue(Id)),
				new(KindKey, FieldSaver.FormatValue(Kind)),
				new(HandleKey, FieldSaver.FormatValue(context.RegisterHandle(Function))),

				new(OccupantsKey, occupants.Where(a => !a.Disposed).Select(w.ActorRef).JoinWith(","))
			};

			if (IsFootprint)
				nodes.Add(new MiniYamlNode(CellsKey, FieldSaver.FormatValue(Cells)));
			else
			{
				nodes.Add(new MiniYamlNode(PosKey, FieldSaver.FormatValue(Pos)));
				nodes.Add(new MiniYamlNode(RangeKey, FieldSaver.FormatValue(Range)));
			}

			return nodes;
		}

		public static LuaMapTrigger LoadState(MiniYaml yaml, SnapshotReader r)
		{
			var nodes = yaml.ToDictionary();
			var kind = FieldLoader.GetValue<LuaMapTriggerKind>(KindKey, nodes[KindKey].Value);

			var cells = nodes.TryGetValue(CellsKey, out var c) ? FieldLoader.GetValue<CPos[]>(CellsKey, c.Value) : null;
			var pos = nodes.TryGetValue(PosKey, out var p) ? FieldLoader.GetValue<WPos>(PosKey, p.Value) : WPos.Zero;
			var range = nodes.TryGetValue(RangeKey, out var g) ? FieldLoader.GetValue<WDist>(RangeKey, g.Value) : WDist.Zero;

			var trigger = new LuaMapTrigger(kind, cells, pos, range)
			{
				Id = FieldLoader.GetValue<int>(IdKey, nodes[IdKey].Value)
			};

			trigger.savedHandle = FieldLoader.GetValue<int>(HandleKey, nodes[HandleKey].Value);

			if (nodes.TryGetValue(OccupantsKey, out var o) && !string.IsNullOrEmpty(o.Value))
				foreach (var reference in o.Value.Split(',', StringSplitOptions.RemoveEmptyEntries))
					r.DeferActor(reference, a => { if (a != null) trigger.savedOccupants.Add(a); });

			return trigger;
		}

		public void Restore(World world, ScriptContext context)
		{
			Function = (LuaFunction)context.ResolveHandle(savedHandle).CopyReference();

			var actorMap = world.WorldActor.Trait<ActorMap>();
			var invoke = Invoke(context);

			if (IsFootprint)
				actorMap.RestoreCellTrigger(Id, Cells, IsEntry ? invoke : null, IsEntry ? null : invoke, savedOccupants);
			else
				actorMap.RestoreProximityTrigger(Id, Pos, Range, WDist.Zero, IsEntry ? invoke : null, IsEntry ? null : invoke, savedOccupants);
		}

		public Action<Actor> Invoke(ScriptContext context)
		{
			return a =>
			{
				try
				{
					using (var luaActor = a.ToLuaValue(context))
					using (var luaId = Id.ToLuaValue(context))
						Function.Call(luaActor, luaId).Dispose();
				}
				catch (Exception e)
				{
					context.FatalError(e);
				}
			};
		}
	}
}
