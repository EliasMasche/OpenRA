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
using Eluant;
using OpenRA.Activities;
using OpenRA.Effects;
using OpenRA.GameSaves;
using OpenRA.Graphics;
using OpenRA.Mods.Common.Scripting;
using OpenRA.Mods.Common.Scripting.Snapshot;
using OpenRA.Mods.Common.Traits;
using OpenRA.Scripting;

namespace OpenRA.Mods.Common.Effects
{
	[SaveableEffect]
	public class SpawnActorEffect : IEffect, ISaveableEffect, ILuaHandleHolder
	{
		const string ActorKey = "Actor";
		const string DelayKey = "Delay";
		const string PathKey = "Path";
		const string HandleKey = "Handle";

		Actor actor;
		readonly CPos[] pathAfterSpawn;
		Activity activityAtDestination;
		IMove move;
		int remainingDelay;

		readonly int savedHandle = -1;
		readonly ScriptContext context;

		public SpawnActorEffect(Actor actor)
			: this(actor, 0, [], null) { }

		public SpawnActorEffect(Actor actor, int delay)
			: this(actor, delay, [], null) { }

		public SpawnActorEffect(Actor actor, int delay, CPos[] pathAfterSpawn, Activity activityAtDestination)
		{
			this.actor = actor;
			remainingDelay = delay;
			this.pathAfterSpawn = pathAfterSpawn;
			this.activityAtDestination = activityAtDestination;
			move = actor.TraitOrDefault<IMove>();
		}

		internal SpawnActorEffect(World world, SnapshotReader r, MiniYaml yaml)
		{
			var nodes = yaml.ToDictionary();

			remainingDelay = FieldLoader.GetValue<int>(DelayKey, nodes[DelayKey].Value);
			pathAfterSpawn = nodes.TryGetValue(PathKey, out var p) && !string.IsNullOrEmpty(p.Value)
				? FieldLoader.GetValue<CPos[]>(PathKey, p.Value)
				: [];

			r.DeferActor(nodes[ActorKey].Value, a =>
			{
				actor = a;
				move = a?.TraitOrDefault<IMove>();
			});

			if (!nodes.TryGetValue(HandleKey, out var h))
				return;

			savedHandle = FieldLoader.GetValue<int>(HandleKey, h.Value);

			context = this.RegisterForHandles(world);
		}

		void ILuaHandleHolder.ResolveHandles(ScriptContext context)
		{
			using (var function = context.ResolveHandle(savedHandle).CopyReference() as LuaFunction)
				activityAtDestination = new LuaCallWithSelf(function, context);
		}

		List<MiniYamlNode> ISaveableEffect.SaveState(World world, SnapshotWriter w)
		{
			if (actor == null || actor.Disposed)
				return null;

			var nodes = new List<MiniYamlNode>
			{
				new(ActorKey, w.ActorRef(actor)),
				new(DelayKey, FieldSaver.FormatValue(remainingDelay)),
				new(PathKey, FieldSaver.FormatValue(pathAfterSpawn))
			};

			if (activityAtDestination is LuaCallWithSelf call && call.PendingFunction != null)
				nodes.Add(new MiniYamlNode(HandleKey,
					FieldSaver.FormatValue(world.WorldActor.Trait<LuaScript>().Context.RegisterHandle(call.PendingFunction))));

			return nodes;
		}

		public void Tick(World world)
		{
			if (remainingDelay-- > 0)
				return;

			if (actor == null)
			{
				world.AddFrameEndTask(w => w.Remove(this));
				return;
			}

			world.Add(actor);
			if (move != null)
				for (var j = 0; j < pathAfterSpawn.Length; j++)
					actor.QueueActivity(move.MoveTo(pathAfterSpawn[j], 2));

			if (activityAtDestination != null)
				actor.QueueActivity(activityAtDestination);

			world.AddFrameEndTask(w => w.Remove(this));
		}

		public IEnumerable<IRenderable> Render(WorldRenderer wr) { return SpriteRenderable.None; }
	}
}
