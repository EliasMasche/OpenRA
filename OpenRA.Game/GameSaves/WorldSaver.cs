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

namespace OpenRA.GameSaves
{
	/// <summary>Writes a world to a snapshot.</summary>
	/// <remarks>
	/// The walk covers the world actor, each player actor, and each actor in the world.
	/// <see cref="SaveableActors"/> also takes an actor that left the world but still holds
	/// <see cref="ISync"/> state, because that state takes part in the desync check.
	/// </remarks>
	public sealed class WorldSaver
	{
		readonly World world;
		readonly ActivitySerializer activities;
		readonly EffectSerializer effects;

		public int DroppedActivities => activities.DroppedTrees;

		public int DroppedEffects => effects.DroppedEffects;

		public SnapshotLobby Lobby { get; set; }

		public bool Diagnostics { get; set; }

		public WorldSaver(World world, ActivityRegistry registry, EffectRegistry effectRegistry)
		{
			ArgumentNullException.ThrowIfNull(world);
			ArgumentNullException.ThrowIfNull(registry);
			ArgumentNullException.ThrowIfNull(effectRegistry);

			this.world = world;
			activities = new ActivitySerializer(registry);
			effects = new EffectSerializer(effectRegistry);
		}

		public void Save(SnapshotWriter w)
		{
			ArgumentNullException.ThrowIfNull(w);

			Lobby?.Write(w);

			w.WriteYamlSection(WorldRestorer.WorldSection, SaveWorld(w), compress: true);
			w.WriteYamlSection(WorldRestorer.PlayersSection, SavePlayers(w), compress: true);
			w.WriteYamlSection(WorldRestorer.ActorsSection, SaveActors(w), compress: true);
			w.WriteYamlSection(WorldRestorer.EffectsSection, effects.Save(world, w), compress: true);

			SaveBulkState(w);

			using (var s = w.BeginSection(WorldRestorer.RandomSection))
				world.SharedRandom.SaveState().Write(s);

			if (Diagnostics)
			{
				w.WriteYamlSection(WorldRestorer.DiffSection, SnapshotDiff.Capture(world).Save(), compress: true);

				SnapshotAudit.Write(SnapshotAudit.OfWorld(world).Describe($"Saved world, tick {world.WorldTick}"));
			}
		}

		void SaveBulkState(SnapshotWriter w)
		{
			foreach (var actor in BulkStateActors())
			{
				foreach (var trait in actor.TraitsImplementing<IWorldSaveState>())
				{
					using (var s = w.BeginSection(trait.SectionName, compress: true))
						trait.SaveState(actor, s, w);
				}
			}
		}

		IEnumerable<Actor> BulkStateActors()
		{
			yield return world.WorldActor;

			foreach (var player in world.Players)
				yield return player.PlayerActor;
		}

		List<MiniYamlNode> SaveWorld(SnapshotWriter w)
		{
			var nodes = new List<MiniYamlNode>
			{
				new(WorldRestorer.WorldTickKey, FieldSaver.FormatValue(world.WorldTick)),
				new(WorldRestorer.PausedKey, FieldSaver.FormatValue(world.Paused)),
				new(WorldRestorer.GameOverKey, FieldSaver.FormatValue(world.IsGameOver)),
				new(WorldRestorer.NextActorIDKey, FieldSaver.FormatValue(world.NextActorID))
			};

			var traits = SaveTraits(world.WorldActor, w);
			if (traits.Count > 0)
				nodes.Add(new MiniYamlNode(WorldRestorer.TraitsKey, new MiniYaml("", traits)));

			return nodes;
		}

		List<MiniYamlNode> SavePlayers(SnapshotWriter w)
		{
			var nodes = new List<MiniYamlNode>();
			foreach (var player in world.Players)
			{
				var playerNodes = new List<MiniYamlNode>
				{
					new(WorldRestorer.WinStateKey, FieldSaver.FormatValue(player.WinState))
				};

				var traits = SaveTraits(player.PlayerActor, w);
				if (traits.Count > 0)
					playerNodes.Add(new MiniYamlNode(WorldRestorer.TraitsKey, new MiniYaml("", traits)));

				nodes.Add(new MiniYamlNode(player.InternalName, new MiniYaml("", playerNodes)));
			}

			return nodes;
		}

		List<MiniYamlNode> SaveActors(SnapshotWriter w)
		{
			var nodes = new List<MiniYamlNode>();

			var players = world.Players.Select(p => p.PlayerActor).ToHashSet();

			foreach (var actor in SaveableActors().OrderBy(a => a.ActorID))
			{
				if (actor == world.WorldActor || players.Contains(actor))
					continue;

				nodes.Add(new MiniYamlNode(actor.ActorID.ToStringInvariant(), SaveActor(actor, w)));
			}

			return nodes;
		}

		IEnumerable<Actor> SaveableActors()
		{
			return world.Actors.Union(world.ActorsHavingTrait<ISync>());
		}

		MiniYaml SaveActor(Actor actor, SnapshotWriter w)
		{
			var nodes = new List<MiniYamlNode>
			{
				new(WorldRestorer.TypeKey, actor.Info.Name),
				new(WorldRestorer.OwnerKey, w.PlayerRef(actor.Owner)),
				new(WorldRestorer.InWorldKey, FieldSaver.FormatValue(actor.IsInWorld)),
				new(WorldRestorer.GenerationKey, FieldSaver.FormatValue(actor.Generation))
			};

			var inits = SnapshotInits.Save(actor);
			if (inits != null)
				nodes.Add(new MiniYamlNode(SnapshotInits.InitsKey, new MiniYaml("", inits)));

			var traits = SaveTraits(actor, w);
			if (traits.Count > 0)
				nodes.Add(new MiniYamlNode(WorldRestorer.TraitsKey, new MiniYaml("", traits)));

			var tree = activities.Save(actor, actor.CurrentActivity, w);
			if (tree != null)
				nodes.Add(new MiniYamlNode(ActivitySerializer.ActivitiesKey, new MiniYaml("", tree)));

			return new MiniYaml("", nodes);
		}

		static List<MiniYamlNode> SaveTraits(Actor actor, SnapshotWriter w)
		{
			var nodes = new List<MiniYamlNode>();
			foreach (var trait in actor.TraitsImplementing<ISaveState>())
			{
				var state = trait.SaveState(actor, w);
				if (state == null || state.Count == 0)
					continue;

				nodes.Add(new MiniYamlNode(World.TraitKey(actor, trait.SaveStateInfo).ToString(), new MiniYaml("", state)));
			}

			return nodes;
		}
	}
}
