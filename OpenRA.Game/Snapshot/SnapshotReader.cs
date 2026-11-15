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
using System.Collections;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using OpenRA.Activities;
using OpenRA.Effects;
using OpenRA.Graphics;
using OpenRA.Primitives;
using OpenRA.Scripting;
using OpenRA.Traits;

namespace OpenRA.Snapshot
{
	/// <summary>A snapshot cannot be loaded into this world. The message names the entry or the field.</summary>
	public sealed class SnapshotLoadException : Exception
	{
		public SnapshotLoadException(string message)
			: base(message) { }

		public SnapshotLoadException(string message, Exception inner)
			: base(message, inner) { }
	}

	/// <summary>
	/// Writes a snapshot over a world that just started on its map with the same players. Creation built what a
	/// snapshot leaves out. It refuses, and never skips, what it cannot restore.
	/// </summary>
	public sealed class SnapshotReader
	{
		static readonly MethodInfo CreateImmutableArray = typeof(ImmutableArray).GetMethods()
			.First(m => m.Name == nameof(ImmutableArray.Create) && m.GetParameters() is [{ ParameterType.IsArray: true }]);

		static readonly MethodInfo CreateFrozenSet = typeof(FrozenSet).GetMethods()
			.First(m => m.Name == nameof(FrozenSet.ToFrozenSet) && m.GetParameters().Length == 2);

		static readonly MethodInfo CreateFrozenDictionary = typeof(FrozenDictionary).GetMethods()
			.First(m => m.Name == nameof(FrozenDictionary.ToFrozenDictionary) && m.GetGenericArguments().Length == 2 && m.GetParameters().Length == 2);

		// Other assemblies whose types a snapshot can name. Only a test host sets them.
		internal static ImmutableArray<Assembly> HostAssemblies = [];

		readonly World world;
		readonly List<Actor> createdActors = [];
		readonly Dictionary<string, MiniYaml> sections;
		readonly Dictionary<string, object> playerRules = [];
		SnapshotFormat.RuleNames ruleNames;
		readonly Dictionary<string, Player> players = [];

		readonly Dictionary<uint, Actor> actors = [];
		readonly SortedDictionary<uint, Actor> disposedActors = [];
		readonly Dictionary<uint, Dictionary<string, object>> traits = [];
		readonly Dictionary<string, FrozenActor> frozenActors = [];
		readonly List<IEffect> effects = [];
		readonly List<Activity> activities = [];
		readonly Dictionary<int, object> objects = [];
		readonly Dictionary<Type, Dictionary<string, FieldInfo>> fieldKeys = [];
		readonly Dictionary<string, Type> types = [];

		SnapshotReader(World world, IEnumerable<MiniYamlNode> snapshot)
		{
			this.world = world;
			sections = snapshot.ToDictionary(n => n.Key, n => n.Value);
		}

		/// <summary>Call it between ticks, on a world that has not ticked. A failed load leaves the world half written.</summary>
		public static void Load(World world, string snapshot)
		{
			List<MiniYamlNode> nodes;
			try
			{
				nodes = MiniYaml.FromString(snapshot, "snapshot").ToList();
			}
			catch (Exception e)
			{
				throw new SnapshotLoadException($"The snapshot could not be read: {e.Message}", e);
			}

			Load(world, nodes);
		}

		public static void Load(World world, IEnumerable<MiniYamlNode> snapshot)
		{
			// Running a constructor again can play a sound, as IonCannon does.
			var soundsWereOff = Game.Sound.DisableAllSounds;
			try
			{
				world.IsLoadingSnapshot = true;
				Game.Sound.DisableAllSounds = true;
				new SnapshotReader(world, snapshot).Load();
			}
			catch (Exception e) when (e is not SnapshotLoadException)
			{
				throw new SnapshotLoadException($"The snapshot could not be read: {e.Message}", e);
			}
			finally
			{
				world.IsLoadingSnapshot = false;
				Game.Sound.DisableAllSounds = soundsWereOff;
			}

			// A save can come from another machine, and a value that no saved world holds can make a trait throw here.
			try
			{
				RebuildAfterLoad(world);
			}
			catch (Exception e) when (e is not SnapshotLoadException)
			{
				throw new SnapshotLoadException($"The loaded world could not be built again: {e.Message}", e);
			}
		}

		static void RebuildAfterLoad(World world)
		{
			// This comes before the animation callbacks, which can end what this builds.
			foreach (var pair in world.ActorsWithTrait<IRebuildAfterLoad>())
				pair.Trait.RebuildAfterLoad(pair.Actor);

			foreach (var effect in world.Effects.OfType<IRebuildAfterLoad>().ToArray())
				effect.RebuildAfterLoad(null);

			// A shroud source is not saved. A trait or an effect that keeps one and does not give it back leaves its cells counted.
			foreach (var player in world.Players)
			{
				var cell = player.Shroud.FirstMismatchedCell();
				if (cell != null)
					throw new SnapshotLoadException($"Player {player.InternalName}: the shroud sources do not add up to the saved counts "
						+ $"at {cell}. A trait or an effect that keeps a source did not give it back after the load.");
			}

			// The trait dictionary finds every actor, also one outside the world such as a passenger.
			foreach (var pair in world.ActorsWithTrait<IResumeAnimations>().ToArray())
				pair.Trait.ResumeAnimations(pair.Actor);

			foreach (var actor in world.ActorsWithTrait<object>().Select(p => p.Actor).Distinct().ToArray())
				if (actor.CurrentActivity != null)
					foreach (var activity in actor.CurrentActivity.ActivitiesImplementing<IResumeAnimations>())
						activity.ResumeAnimations(actor);

			foreach (var effect in world.Effects.OfType<IResumeAnimations>().ToArray())
				effect.ResumeAnimations(null);
		}

		void Load()
		{
			CheckHeader();

			if (world.StartupActors != StartupActors.FromSnapshot)
				throw new SnapshotLoadException("The world started with the actors of its map. "
					+ $"A world that loads a snapshot starts with {nameof(StartupActors)}.{nameof(StartupActors.FromSnapshot)}.");

			// A snapshot does not hold the state of a Lua script, so a save of a map with a script is never a snapshot.
			if (world.WorldActor.Info.HasTraitInfo<IWorldScriptInfo>())
				throw new SnapshotLoadException("The map runs a Lua script, and a snapshot does not hold its state.");

			MapSection.Read(world.Map, Section(SnapshotFormat.Sections.Map));
			ruleNames = SnapshotFormat.NamesOf(world.Map.Rules);
			foreach (var (rule, name) in SnapshotFormat.PlayerRules(world))
				playerRules.TryAdd(name, rule);

			foreach (var player in world.Players)
				players.Add(player.InternalName, player);

			MatchPlayers();
			MatchActors();
			CreateObjects(SnapshotFormat.Sections.Effects, "an effect", effects);
			CreateObjects(SnapshotFormat.Sections.Activities, "an activity", activities);

			// Frame-end tasks of creation expect the state that creation left, so they run before the reader writes a
			// saved value.
			var frameEndTasks = world.FrameEndActions;
			Run(frameEndTasks);

			IndexFrozenActors();

			// Adding actors to the world after the first pass changes saved state and queues tasks. The second pass undoes it.
			WriteState(true);
			BuryDisposedActors();
			foreach (var actor in createdActors)
				if (actor.IsInWorld)
					world.Add(actor);

			Run(frameEndTasks);

			WriteState(false);
			RemoveStaleFrozenActors();
			AddEffects();
			WriteWorld();

			if (frameEndTasks.Count != 0)
				throw new SnapshotLoadException($"Writing the saved state queued {frameEndTasks.Count} frame-end tasks.");

			var loaded = actors.Values.ToHashSet();
			var extra = world.TraitDict.ActorsWithTrait<object>().Select(p => p.Actor).Distinct().Where(a => !loaded.Contains(a)).ToArray();
			if (extra.Length > 0)
				throw new SnapshotLoadException("The load created actors the snapshot lacks: "
					+ $"{extra.Select(a => $"{a.Info.Name} {a.ActorID}").JoinWith(", ")}.");

			OrdersSection.Read(world.OrderManager, Section(SnapshotFormat.Sections.Orders));
		}

		void Run(Queue<Action<World>> frameEndTasks)
		{
			while (frameEndTasks.Count != 0)
				frameEndTasks.Dequeue()(world);
		}

		MiniYaml Section(string name)
		{
			return sections.TryGetValue(name, out var section) ? section : throw new SnapshotLoadException($"The snapshot has no {name} section.");
		}

		void CheckHeader()
		{
			var header = Section(SnapshotFormat.Sections.Header);
			var format = Parse<int>(header.NodeWithKey(SnapshotFormat.Keys.Format).Value.Value, "Snapshot.Format");
			if (format != SnapshotWriter.Format)
				throw new SnapshotLoadException($"The snapshot has format {format}, and this engine reads format {SnapshotWriter.Format}.");

			var mod = header.NodeWithKey(SnapshotFormat.Keys.Mod).Value.Value;
			if (mod != Game.ModData.Manifest.Id)
				throw new SnapshotLoadException($"The snapshot is of mod {mod}, and the world runs {Game.ModData.Manifest.Id}.");

			var map = header.NodeWithKey(SnapshotFormat.Keys.Map).Value.Value;
			if (map != world.Map.Uid)
				throw new SnapshotLoadException($"The snapshot is of map {map}, and the world runs {world.Map.Uid}.");
		}

		void MatchPlayers()
		{
			var saved = Section(SnapshotFormat.Sections.Players).Nodes;
			foreach (var node in saved)
			{
				if (!players.TryGetValue(node.Key, out var player))
					throw new SnapshotLoadException($"Player {node.Key} is in the snapshot but not in the world.");

				var actorId = Parse<uint>(node.Value.Value, $"Player {node.Key}");
				if (player.PlayerActor.ActorID != actorId)
					throw new SnapshotLoadException($"Player {node.Key} has actor {player.PlayerActor.ActorID} in the world "
						+ $"and actor {actorId} in the snapshot.");

				// A snapshot holds what a bot knows, not which modules it runs.
				var savedBot = node.Value.NodeWithKey(nameof(Player.BotType)).Value.Value;
				var bot = savedBot == SnapshotWriter.Null ? null : Parse<string>(savedBot, $"Player {node.Key}");
				if (bot != player.BotType)
					throw new SnapshotLoadException($"Player {node.Key} has the bot {player.BotType ?? "(none)"} in the world "
						+ $"and the bot {bot ?? "(none)"} in the snapshot.");
			}

			if (saved.Length != players.Count)
				throw new SnapshotLoadException($"The world has {players.Count} players and the snapshot has {saved.Length}.");
		}

		// Matches the actors that the world created at its start by ID. Creates every other actor under its saved ID.
		void MatchActors()
		{
			foreach (var actor in world.TraitDict.ActorsWithTrait<object>().Select(p => p.Actor).Distinct())
				actors.Add(actor.ActorID, actor);

			var saved = Section(SnapshotFormat.Sections.Actors).Nodes.ToDictionary(n => ActorId(n.Key));
			var extra = actors.Values.Where(a => !saved.ContainsKey(a.ActorID)).Select(a => $"{a.Info.Name} {a.ActorID}").ToArray();
			if (extra.Length > 0)
				throw new SnapshotLoadException($"The world has actors the snapshot lacks: {extra.JoinWith(", ")}.");

			var disposed = Section(SnapshotFormat.Sections.DisposedActors).Nodes.ToDictionary(n => ActorId(n.Key));
			foreach (var (id, node) in saved.Concat(disposed).OrderBy(n => n.Key))
			{
				var type = node.Value.Value;
				if (disposed.ContainsKey(id))
				{
					if (saved.ContainsKey(id) || actors.ContainsKey(id))
						throw new SnapshotLoadException($"{node.Key} has died in the snapshot, and is alive in "
							+ $"{(saved.ContainsKey(id) ? "the snapshot too" : "the world")}. Loading this is not supported yet.");

					disposedActors.Add(id, CreateActor(id, type, node.Value));
				}
				else if (!actors.TryGetValue(id, out var actor))
				{
					actor = CreateActor(id, type, node.Value);
					actors.Add(id, actor);
					createdActors.Add(actor);
				}
				else if (actor.Info.Name != type)
					throw new SnapshotLoadException($"{node.Key} is a {type} in the snapshot and a {actor.Info.Name} in the world.");
			}
		}

		// Creating an actor makes its frozen actors, so the first pass finds each one by player and actor.
		void IndexFrozenActors()
		{
			foreach (var actor in actors.Values.Concat(disposedActors.Values))
			{
				foreach (var player in world.Players)
				{
					var frozen = player.FrozenActorLayer?.FromID(actor.ActorID);
					if (frozen != null)
						frozenActors.Add($"{player.InternalName}/{actor.ActorID}", frozen);
				}
			}
		}

		// A player who saw an actor die has no frozen actor of it. Removes it through the layer, which also indexes it.
		void RemoveStaleFrozenActors()
		{
			foreach (var frozen in frozenActors.Values)
			{
				var layer = frozen.Viewer.FrozenActorLayer;
				if (layer.FromID(frozen.ID) != frozen)
					layer.Remove(frozen);
			}
		}

		// A dead actor is in the snapshot only because something names it or a trait of it. It leaves the trait dictionary,
		// and its traits stay reachable by key.
		void BuryDisposedActors()
		{
			foreach (var actor in disposedActors.Values)
			{
				TraitsByKey(actor, $"Actor{actor.ActorID} ({actor.Info.Name})");
				world.TraitDict.RemoveActor(actor);
				actor.Disposed = true;
			}
		}

		Actor CreateActor(uint id, string type, MiniYaml saved)
		{
			var path = $"Actor{id} ({type})";
			if (Reference(saved.NodeWithKey(SnapshotFormat.Keys.Owner).Value.Value, path) is not Player owner)
				throw new SnapshotLoadException($"{path} has no player as its owner.");

			var inits = new TypeDictionary { new OwnerInit(owner) };
			var location = saved.NodeWithKeyOrDefault(SnapshotFormat.Keys.Location);
			if (location != null)
				inits.Add(new LocationInit(Parse<CPos>(location.Value.Value, path)));

			if (!world.Map.Rules.Actors.TryGetValue(type, out var info))
				throw new SnapshotLoadException($"{path}: the rules define no such actor.");

			// The saved state already holds what the actor made during its creation, and what it then became.
			var traitKeys = SnapshotFormat.TraitKeys(info);
			var traitInfos = info.TraitsInConstructOrder().ToArray();
			for (var i = 0; i < traitInfos.Length; i++)
			{
				if (traitInfos[i] is not ISnapshotCreationInfo creation)
					continue;

				var key = traitKeys[i];
				creation.AddSnapshotInits(inits, owner,
					field => SavedValue(saved, key, field) is string text ? Parse<string>(text, path) : null,
					field => SavedActor(saved, key, field, path));
			}

			world.NextActorId = id;
			Actor actor;
			try
			{
				actor = world.CreateActor(false, type, inits);
			}
			catch (Exception e)
			{
				throw new SnapshotLoadException($"{path} could not be created: {e.Message}", e);
			}

			var used = world.NextActorId - id;
			if (used != 1)
				throw new SnapshotLoadException($"{path} created {used - 1} more actors while it was created. "
					+ "Its saved state already holds them, so it has to be created without them.");

			return actor;
		}

		// The actor that a saved field of a trait names, or null. It has a lower ID. A field that holds a trait names its actor.
		Actor SavedActor(MiniYaml saved, string traitKey, string fieldKey, SnapshotPath path)
		{
			var reference = SavedValue(saved, traitKey, fieldKey);
			if (reference == null)
				return null;

			var actorReference = reference.Split(SnapshotFormat.References.TraitSeparator)[0];
			return Reference(actorReference, $"{path} {traitKey}.{fieldKey}") as Actor;
		}

		static string SavedValue(MiniYaml saved, string traitKey, string fieldKey)
		{
			var value = saved.NodeWithKeyOrDefault(SnapshotFormat.Keys.Traits)?.Value
				.NodeWithKeyOrDefault(traitKey)?.Value.NodeWithKeyOrDefault(fieldKey)?.Value.Value;

			return value == SnapshotWriter.Null ? null : value;
		}

		static uint ActorId(string key)
		{
			if (!key.StartsWith(SnapshotFormat.ActorKeyPrefix, StringComparison.Ordinal)
				|| !uint.TryParse(key.AsSpan(SnapshotFormat.ActorKeyPrefix.Length), out var id))
				throw new SnapshotLoadException($"{key} does not name an actor.");

			return id;
		}

		// Builds activities and effects without a constructor, before it writes any field, because they refer to each
		// other.
		void CreateObjects<T>(string section, string description, List<T> created)
		{
			foreach (var node in Section(section).Nodes)
			{
				var path = $"{section}.{node.Key}";
				var type = FindType(node.Value.Value, path);
				if (!typeof(T).IsAssignableFrom(type))
					throw new SnapshotLoadException($"{path}: {type.Name} is not {description}.");

				RequireSnapshotType(type, path);

				created.Add((T)RuntimeHelpers.GetUninitializedObject(type));
			}
		}

		// The snapshot holds every effect that the simulation reads, in tick order.
		void AddEffects()
		{
			world.RemoveAll(e => !SavePolicy.IsLeftOut(e.GetType()));
			foreach (var effect in effects)
				world.Add(effect);
		}

		// Runs the constructor of an effect whose parameters match saved fields, to build what a snapshot leaves out,
		// such as a sprite. Refuses an effect without such a constructor when the effect has something to draw.
		void Construct(IEffect effect, ImmutableArray<MiniYamlNode> nodes, SnapshotPath path)
		{
			var keys = FieldKeys(effect.GetType());
			foreach (var constructor in SavePolicy.LoadConstructors(effect.GetType()))
			{
				var parameters = constructor.GetParameters();
				var arguments = new object[parameters.Length];
				var found = 0;
				foreach (var parameter in parameters)
				{
					var node = nodes.FirstOrDefault(n => string.Equals(n.Key, parameter.Name, StringComparison.OrdinalIgnoreCase));
					if (node == null || !keys.TryGetValue(node.Key, out var field))
						break;

					// An "in" parameter has a by-reference type, which no value is an instance of.
					var type = parameter.ParameterType.IsByRef ? parameter.ParameterType.GetElementType() : parameter.ParameterType;
					var argument = Decode(SavePolicy.Classify(field, false), field.FieldType, node.Value, null, path.Field(node.Key));
					if (argument != null ? !type.IsInstanceOfType(argument) : type.IsValueType)
						break;

					arguments[found++] = argument;
				}

				if (found != parameters.Length)
					continue;

				try
				{
					constructor.Invoke(effect, arguments);
				}
				catch (TargetInvocationException e)
				{
					throw new SnapshotLoadException($"{path} could not be constructed: {e.InnerException?.Message}", e.InnerException);
				}

				return;
			}

			var unbuilt = SavePolicy.BuiltByConstructor(effect.GetType());
			if (unbuilt.Length > 0)
				throw new SnapshotLoadException($"{path}: no constructor takes only what the snapshot holds, so nothing builds "
					+ $"{unbuilt.JoinWith(", ")}. Loading this effect is not supported yet.");
		}

		void WriteState(bool isFirstPass)
		{
			foreach (var node in Section(SnapshotFormat.Sections.Players).Nodes)
				WriteFields(players[node.Key], node.Value.Nodes, true, $"Player {node.Key}");

			foreach (var node in Section(SnapshotFormat.Sections.Actors).Nodes)
			{
				var actor = actors[ActorId(node.Key)];
				var path = $"{node.Key} ({actor.Info.Name})";
				WriteFields(actor, node.Value.NodeWithKey(SnapshotFormat.Keys.Actor).Value.Nodes, true, path);
				foreach (var trait in node.Value.NodeWithKey(SnapshotFormat.Keys.Traits).Value.Nodes)
					WriteFields(Trait(actor, trait.Key, path), trait.Value.Nodes, true, $"{path} {trait.Key}");
			}

			var effectNodes = Section(SnapshotFormat.Sections.Effects).Nodes;
			for (var i = 0; i < effectNodes.Length; i++)
			{
				var path = $"Effect {effectNodes[i].Key} {effects[i].GetType().Name}";
				if (isFirstPass)
					Construct(effects[i], effectNodes[i].Value.Nodes, path);

				WriteFields(effects[i], effectNodes[i].Value.Nodes, false, path);
			}

			var activityNodes = Section(SnapshotFormat.Sections.Activities).Nodes;
			for (var i = 0; i < activityNodes.Length; i++)
				WriteFields(activities[i], activityNodes[i].Value.Nodes, false, $"Activity {activityNodes[i].Key} {activities[i].GetType().Name}");

			foreach (var node in Section(SnapshotFormat.Sections.FrozenActors).Nodes)
				WriteFields(FrozenActor(node.Key, "FrozenActors"), node.Value.Nodes, false, $"FrozenActor {node.Key}");
		}

		FrozenActor FrozenActor(string key, SnapshotPath path)
		{
			return frozenActors.TryGetValue(key, out var frozen) ? frozen
				: throw new SnapshotLoadException($"{path}: there is no frozen actor {key}. Creating that actor did not make one.");
		}

		// Last, as everything before can create an actor or draw from the random generators.
		void WriteWorld()
		{
			var saved = Section(SnapshotFormat.Sections.World);
			world.NextActorId = Parse<uint>(saved.NodeWithKey(SnapshotFormat.Keys.NextActorId).Value.Value, "World.NextActorID");
			world.RestoreRunState(
				Parse<int>(saved.NodeWithKey(SnapshotFormat.Keys.Tick).Value.Value, "World.Tick"),
				Parse<bool>(saved.NodeWithKey(SnapshotFormat.Keys.Paused).Value.Value, "World.Paused"),
				Parse<bool>(saved.NodeWithKey(SnapshotFormat.Keys.PredictedPaused).Value.Value, "World.PredictedPaused"),
				Parse<bool>(saved.NodeWithKey(SnapshotFormat.Keys.GameOver).Value.Value, "World.GameOver"));
			WriteFields(world.SharedRandom, saved.NodeWithKey(SnapshotFormat.Keys.SharedRandom).Value.Nodes, false, "World.SharedRandom");
			WriteFields(world.LocalRandom, saved.NodeWithKey(SnapshotFormat.Keys.LocalRandom).Value.Nodes, false, "World.LocalRandom");
		}

		object Trait(Actor actor, string key, SnapshotPath path)
		{
			return TraitsByKey(actor, path).TryGetValue(key, out var trait) ? trait
				: throw new SnapshotLoadException($"{path}: {actor.Info.Name} {actor.ActorID} has no trait {key}.");
		}

		Dictionary<string, object> TraitsByKey(Actor actor, SnapshotPath path)
		{
			if (!traits.TryGetValue(actor.ActorID, out var byKey))
			{
				var keys = SnapshotFormat.TraitKeys(actor.Info);
				var instances = world.TraitDict.WithInterface<object>(actor).ToArray();
				if (keys.Length != instances.Length)
					throw new SnapshotLoadException($"{path} has {instances.Length} traits but its rules define {keys.Length}.");

				byKey = [];
				for (var i = 0; i < keys.Length; i++)
					byKey.Add(keys[i], instances[i]);

				traits.Add(actor.ActorID, byKey);
			}

			return byKey;
		}

		Dictionary<string, FieldInfo> FieldKeys(Type type)
		{
			if (!fieldKeys.TryGetValue(type, out var keys))
				fieldKeys.Add(type, keys = SavePolicy.FieldKeys(type).ToDictionary(k => k.Key, k => k.Field));

			return keys;
		}

		void WriteFields(object holder, ImmutableArray<MiniYamlNode> nodes, bool isCreated, SnapshotPath path)
		{
			var type = holder.GetType();
			var keys = FieldKeys(type);
			foreach (var node in nodes)
			{
				if (node.Key is SnapshotWriter.TypeKey or SnapshotWriter.IdKey)
					continue;

				var fieldPath = path.Field(node.Key);
				if (!keys.TryGetValue(node.Key, out var field))
					throw new SnapshotLoadException($"{fieldPath} is not a field of {type.Name}.");

				object value;
				try
				{
					var kind = SavePolicy.Classify(field, isCreated);
					value = Decode(kind, field.FieldType, node.Value, kind == SaveKind.Value ? null : field.GetValue(holder), fieldPath);
				}
				catch (Exception e) when (e is not SnapshotLoadException)
				{
					throw new SnapshotLoadException($"{fieldPath}: {e.Message}", e);
				}

				field.SetValue(holder, value);
			}
		}

		// Writes a collection or an owned object in place over existing, which keeps its identity and its unsaved
		// state.
		object Decode(SaveKind kind, Type declared, MiniYaml yaml, object existing, SnapshotPath path)
		{
			var value = yaml.Value ?? "";
			if (value == SnapshotWriter.Null && yaml.Nodes.Length == 0)
				return declared.IsValueType && Nullable.GetUnderlyingType(declared) == null ? Activator.CreateInstance(declared) : null;

			switch (kind)
			{
				case SaveKind.Value:
					return Parse(declared, value, path);

				case SaveKind.Collection:
					return Collection(declared, yaml, existing, path);

				// A node with a value is a reference. An owned object has only fields.
				case SaveKind.Owned when value.Length == 0:
				case SaveKind.Target when value.Length == 0:
					return OwnedObject(declared, yaml, existing, path);

				case SaveKind.Animation:
					return Animation(yaml, existing, path);

				case SaveKind.Structure when !value.StartsWith('@'):
					return OwnedObject(declared, yaml, existing, path);

				case SaveKind.NotSaved:
				case SaveKind.Rebuilt:
				case SaveKind.Query:
				case SaveKind.Presentation:
				case SaveKind.Unsupported:
					throw new SnapshotLoadException($"{path} is in the snapshot, but this engine does not save it ({kind}).");

				default:
					if (value == SnapshotFormat.DefaultComparer)
						return SnapshotFormat.DefaultComparerFor(declared);

					// An object that is no trait can stand behind a field typed as a trait. The writer saves it as an owned object.
					if (value.Length == 0 && yaml.NodeWithKeyOrDefault(SnapshotWriter.TypeKey) != null)
						return OwnedObject(declared, yaml, existing, path);

					var target = Reference(value, path);
					if (yaml.Nodes.Length == 0)
						return target;

					if (!value.Contains(SnapshotFormat.References.Disposed + SnapshotFormat.References.TraitSeparator, StringComparison.Ordinal))
						throw new SnapshotLoadException($"{path}: {value} is a reference, and a reference holds no fields.");

					WriteFields(target, yaml.Nodes, true, path);
					return target;
			}
		}

		// Puts an animation back at its saved sequence and frame. A callback comes back when the animations resume. An
		// animation without a saved frame keeps what creation built, but this stops a wait that creation started.
		static object Animation(MiniYaml yaml, object existing, SnapshotPath path)
		{
			if (existing is not Animation animation)
			{
				if (yaml.Nodes.Length == 0)
					return existing;

				throw new SnapshotLoadException($"{path}: the field holds no animation to put back at its saved frame.");
			}

			// The holder changes the image only when its own state changes, and the load already wrote that state.
			var image = yaml.NodeWithKeyOrDefault(SnapshotFormat.Keys.Image)?.Value.Value ?? animation.CreatedName;
			var sequence = yaml.NodeWithKeyOrDefault(SnapshotFormat.Keys.Sequence);
			if (sequence == null)
			{
				if (animation.WaitsToFinish)
					animation.Restore(animation.Capture() with { WaitsToFinish = false });

				if (animation.Name != image)
					animation.ChangeImage(image, animation.CurrentSequence.Name);

				return animation;
			}

			var repeats = yaml.NodeWithKeyOrDefault(SnapshotFormat.Keys.Repeats) is { } r && Parse<bool>(r.Value.Value, path);
			var finished = yaml.NodeWithKeyOrDefault(SnapshotFormat.Keys.Finished) is { } f && Parse<bool>(f.Value.Value, path);
			animation.Restore(new AnimationState(
				image,
				sequence.Value.Value,
				Parse<int>(yaml.NodeWithKey(SnapshotFormat.Keys.Frame).Value.Value, path),
				Parse<bool>(yaml.NodeWithKey(SnapshotFormat.Keys.Backwards).Value.Value, path),
				Parse<int>(yaml.NodeWithKey(SnapshotFormat.Keys.TimeUntilNextFrame).Value.Value, path),
				!repeats && !finished,
				repeats));

			return animation;
		}

		object OwnedObject(Type declared, MiniYaml yaml, object existing, SnapshotPath path)
		{
			var typeNode = yaml.NodeWithKeyOrDefault(SnapshotWriter.TypeKey);
			var type = typeNode != null ? FindType(typeNode.Value.Value, path) : Nullable.GetUnderlyingType(declared) ?? declared;
			if (!declared.IsAssignableFrom(type))
				throw new SnapshotLoadException($"{path}: a {type.Name} does not fit a field of type {declared.Name}.");

			if (typeNode != null)
				RequireSnapshotType(type, path);

			var idNode = yaml.NodeWithKeyOrDefault(SnapshotWriter.IdKey);
			var id = idNode != null ? Parse<int>(idNode.Value.Value, path) : -1;
			if (id < 0 || !objects.TryGetValue(id, out var target))
			{
				if (type.IsAbstract || type.IsInterface)
					throw new SnapshotLoadException($"{path}: a {type.Name} cannot be created without its type.");

				var created = existing == null || existing.GetType() != type;
				target = created ? CreateOwned(type) : existing;
				if (id >= 0)
					objects.Add(id, target);

				// A holder can keep an effect that is not in the world, as a support power keeps its beacon.
				if (created && target is IEffect effect)
					Construct(effect, yaml.Nodes, path);
			}

			WriteFields(target, yaml.Nodes, false, path);
			return target;
		}

		// Its constructor runs when it takes no arguments, so unsaved members start as the type declares them.
		static object CreateOwned(Type type)
		{
			var constructor = type.IsValueType ? null
				: type.GetConstructor(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, Type.EmptyTypes);

			return constructor != null ? constructor.Invoke(null) : RuntimeHelpers.GetUninitializedObject(type);
		}

		object Collection(Type declared, MiniYaml yaml, object existing, SnapshotPath path)
		{
			var value = yaml.Value ?? "";
			if (yaml.Nodes.Length == 0 && value.StartsWith(SnapshotFormat.References.Object, StringComparison.Ordinal))
			{
				var shared = Reference(value, path);
				if (!declared.IsInstanceOfType(shared))
					throw new SnapshotLoadException($"{path}: {value} is a {shared.GetType().Name}, which does not fit a field of type {declared.Name}.");

				return shared;
			}

			var idNode = yaml.NodeWithKeyOrDefault(SnapshotWriter.IdKey);
			if (idNode == null)
				return CollectionContents(declared, yaml, existing, path);

			var id = Parse<int>(idNode.Value.Value, path);
			if (objects.TryGetValue(id, out var known))
				existing = known;

			var contents = new MiniYaml(yaml.Value, yaml.Nodes.Where(n => n.Key != SnapshotWriter.IdKey));
			var collection = CollectionContents(declared, contents, existing, path);
			objects[id] = collection;
			return collection;
		}

		object CollectionContents(Type declared, MiniYaml yaml, object existing, SnapshotPath path)
		{
			if (existing != null && SavePolicy.IsDefaultImmutableArray(existing))
				existing = null;

			var type = existing?.GetType() ?? declared;

			if (SavePolicy.IsPersistentImmutable(type))
				throw new SnapshotLoadException($"{path}: a {type.Name} does not change, so a loader cannot fill it.");

			var elementTypes = SavePolicy.ElementTypes(declared) ?? SavePolicy.ElementTypes(type)
				?? throw new SnapshotLoadException($"{path}: {type.Name} is not a collection.");

			var elementKinds = elementTypes.Select(e => SavePolicy.Classify(e, false)).ToArray();
			if (elementTypes.Length == 2)
				return Dictionary(type, elementTypes, elementKinds, yaml, existing, path);

			// PERF: Reads numbers directly into an array or a cell layer.
			if (SavePolicy.IsOneLine(elementTypes[0]))
			{
				Array numbers;
				try
				{
					numbers = SnapshotFormat.ParseOneLine(elementTypes[0], yaml.Value ?? "", path);
				}
				catch (FormatException e)
				{
					throw new SnapshotLoadException(e.Message, e);
				}

				return FillOneLine(type, elementTypes[0], numbers, existing, path);
			}

			if (SavePolicy.CellLayerBase(type) != null && existing != null)
				return SparseCellLayer(elementTypes[0], elementKinds[0], yaml, existing, path);

			var old = existing != null ? ((IEnumerable)existing).Cast<object>().ToArray() : [];
			var items = yaml.Nodes
				.Select((node, i) => Decode(elementKinds[0], elementTypes[0], node.Value, i < old.Length ? old[i] : null, path.Element(node.Key)))
				.ToArray();

			return Fill(type, elementTypes[0], items, existing, old, path);
		}

		// Empties every cell that the snapshot does not list.
		object SparseCellLayer(Type elementType, SaveKind elementKind, MiniYaml yaml, object existing, SnapshotPath path)
		{
			var cells = ((ICellLayerEntries)existing).Entries;
			var items = yaml.Nodes.Select(node =>
			{
				var index = Parse<int>(node.Key, path);
				if (index < 0 || index >= cells.Length)
					throw new SnapshotLoadException($"{path}: cell {index} is outside the map, which has {cells.Length} cells.");

				return (Index: index, Item: Decode(elementKind, elementType, node.Value, cells.GetValue(index), path.Element(index)));
			}).ToArray();

			Array.Clear(cells);
			foreach (var (index, item) in items)
				cells.SetValue(item, index);

			return existing;
		}

		static object FillOneLine(Type type, Type elementType, Array items, object existing, SnapshotPath path)
		{
			if (type.IsArray)
			{
				if (existing is not Array array || array.Length != items.Length)
					return items;

				Array.Copy(items, array, items.Length);
				return array;
			}

			if (SavePolicy.CellLayerBase(type) != null && existing != null)
				return FillCells(items, existing, path);

			return Fill(type, elementType, items.Cast<object>().ToArray(), existing, [], path);
		}

		static object FillCells(Array items, object existing, SnapshotPath path)
		{
			var cells = ((ICellLayerEntries)existing).Entries;
			if (cells.Length != items.Length)
				throw new SnapshotLoadException($"{path}: the snapshot has {items.Length} cells and the map has {cells.Length}.");

			Array.Copy(items, cells, items.Length);
			return existing;
		}

		// Fills the collection that the field holds, so whatever else refers to it sees the loaded contents.
		static object Fill(Type type, Type elementType, object[] items, object existing, object[] old, SnapshotPath path)
		{
			if (type.IsArray)
			{
				if (existing is not Array array || array.Length != items.Length)
					array = Array.CreateInstance(type.GetElementType(), items.Length);

				Array.Copy(items, array, items.Length);
				return array;
			}

			// Builds an immutable collection again. A frozen set keeps the comparer of the set it replaces.
			if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(ImmutableArray<>))
				return CreateImmutableArray.MakeGenericMethod(elementType).Invoke(null, [Typed(elementType, items)]);

			if (SavePolicy.GenericBase(type, typeof(FrozenSet<>)) != null)
				return CreateFrozenSet.MakeGenericMethod(elementType).Invoke(null, [Typed(elementType, items), Comparer(existing)]);

			if (SavePolicy.CellLayerBase(type) != null && existing != null)
				return FillCells(items, existing, path);

			var target = existing ?? Create(type, path);
			if (target is IList { IsFixedSize: false, IsReadOnly: false } list)
			{
				list.Clear();
				foreach (var item in items)
					list.Add(item);

				return target;
			}

			if (SavePolicy.IsHashSet(type))
			{
				SnapshotFormat.FillSet(target, items);
				return target;
			}

			// A stack enumerates from its top, so the reader fills it from its bottom.
			var clear = type.GetMethod("Clear", Type.EmptyTypes);
			var push = type.GetMethod("Push", [elementType]);
			var add = push ?? type.GetMethod("Add", [elementType]) ?? type.GetMethod("Enqueue", [elementType]) ?? type.GetMethod("AddLast", [elementType]);
			if (clear == null || add == null)
			{
				// Writing each element in place already loaded a fixed collection.
				if (existing != null && items.Length == old.Length && items.Zip(old).All(p => ReferenceEquals(p.First, p.Second)))
					return existing;

				// The reader fills a collection that can only grow while it is empty.
				if (add != null && old.Length == 0)
				{
					foreach (var item in items)
						add.Invoke(target, [item]);

					return target;
				}

				throw new SnapshotLoadException($"{path}: a {type.Name} cannot be cleared and filled.");
			}

			clear.Invoke(target, null);
			foreach (var item in push != null ? items.Reverse() : items)
				add.Invoke(target, [item]);

			return target;
		}

		object Dictionary(Type type, Type[] elementTypes, SaveKind[] elementKinds, MiniYaml yaml, object existing, SnapshotPath path)
		{
			var old = new Dictionary<object, object>();
			if (existing != null)
				foreach (var (key, value) in SavePolicy.Entries(existing))
					old[key] = value;

			var entries = yaml.Nodes.Select(node =>
			{
				var entryPath = path.Element(node.Key);
				var key = Decode(elementKinds[0], elementTypes[0], new MiniYaml(node.Value.Value), null, entryPath);
				var saved = node.Value.NodeWithKey(SnapshotFormat.Keys.EntryValue).Value;
				var value = Decode(elementKinds[1], elementTypes[1], saved, old.GetValueOrDefault(key), entryPath);
				return (Key: key, Value: value);
			}).ToArray();

			if (SavePolicy.GenericBase(type, typeof(FrozenDictionary<,>)) != null)
			{
				var pairType = typeof(KeyValuePair<,>).MakeGenericType(elementTypes);
				var pairs = entries.Select(e => Activator.CreateInstance(pairType, e.Key, e.Value)).ToArray();
				return CreateFrozenDictionary.MakeGenericMethod(elementTypes).Invoke(null, [Typed(pairType, pairs), Comparer(existing)]);
			}

			if ((existing ?? Create(type, path)) is not IDictionary { IsReadOnly: false } target)
				throw new SnapshotLoadException($"{path}: a {type.Name} cannot be cleared and filled.");

			target.Clear();
			foreach (var (key, value) in entries)
				target.Add(key, value);

			return target;
		}

		static object Create(Type type, SnapshotPath path)
		{
			if (type.IsAbstract || type.IsInterface || type.GetConstructor(Type.EmptyTypes) == null)
				throw new SnapshotLoadException($"{path}: the field holds no {type.Name} to fill, and one cannot be created.");

			return Activator.CreateInstance(type);
		}

		static Array Typed(Type elementType, object[] items)
		{
			var typed = Array.CreateInstance(elementType, items.Length);
			Array.Copy(items, typed, items.Length);
			return typed;
		}

		static object Comparer(object collection)
		{
			return collection?.GetType().GetProperty("Comparer")?.GetValue(collection);
		}

		object Reference(string reference, SnapshotPath path)
		{
			switch (reference)
			{
				case SnapshotFormat.References.World: return world;
				case SnapshotFormat.References.Map: return world.Map;
				case SnapshotFormat.References.SharedRandom: return world.SharedRandom;
				case SnapshotFormat.References.LocalRandom: return world.LocalRandom;
			}

			if (ruleNames.RuleOf.TryGetValue(reference, out var rule) || playerRules.TryGetValue(reference, out rule))
				return rule;

			if (reference.StartsWith(SnapshotFormat.References.Player, StringComparison.Ordinal))
				return players.TryGetValue(reference[SnapshotFormat.References.Player.Length..], out var player) ? player
					: throw new SnapshotLoadException($"{path}: {reference} names no player in the world.");

			if (reference.StartsWith(SnapshotFormat.References.Activity, StringComparison.Ordinal))
				return Indexed(activities, reference, SnapshotFormat.References.Activity, "activity", path);

			if (reference.StartsWith(SnapshotFormat.References.Effect, StringComparison.Ordinal))
				return Indexed(effects, reference, SnapshotFormat.References.Effect, "effect", path);

			if (reference.StartsWith(SnapshotFormat.References.Frozen, StringComparison.Ordinal))
				return FrozenActor(reference[SnapshotFormat.References.Frozen.Length..], path);

			if (reference.StartsWith(SnapshotFormat.References.Object, StringComparison.Ordinal))
				return objects.TryGetValue(Parse<int>(reference[SnapshotFormat.References.Object.Length..], path), out var shared) ? shared
					: throw new SnapshotLoadException($"{path}: {reference} is used before the object it names.");

			// The rest is @<actor ID> or @<actor ID>/<trait key>. !disposed follows the ID of a dead actor.
			if (!reference.StartsWith(SnapshotFormat.References.Start, StringComparison.Ordinal))
				throw new SnapshotLoadException($"{path}: \"{reference}\" is not a reference this engine reads.");

			var slash = reference.IndexOf(SnapshotFormat.References.TraitSeparator);
			var actorPart = slash < 0 ? reference[SnapshotFormat.References.Start.Length..] : reference[SnapshotFormat.References.Start.Length..slash];
			var isDisposed = actorPart.EndsWith(SnapshotFormat.References.Disposed, StringComparison.Ordinal);
			if (isDisposed)
				actorPart = actorPart[..^SnapshotFormat.References.Disposed.Length];

			if (!uint.TryParse(actorPart, out var id))
				throw new SnapshotLoadException($"{path}: {reference} is not a reference this engine reads.");

			if (!(isDisposed ? disposedActors : (IDictionary<uint, Actor>)actors).TryGetValue(id, out var actor))
				throw new SnapshotLoadException($"{path}: {reference} names no actor in the snapshot.");

			return slash < 0 ? actor : Trait(actor, reference[(slash + 1)..], path);
		}

		static T Indexed<T>(List<T> list, string reference, string prefix, string description, SnapshotPath path)
		{
			var index = Parse<int>(reference[prefix.Length..], path);
			return index >= 0 && index < list.Count ? list[index]
				: throw new SnapshotLoadException($"{path}: {reference} names no {description} in the snapshot.");
		}

		internal static T Parse<T>(string text, SnapshotPath path)
		{
			return (T)Parse(typeof(T), text, path);
		}

		static object Parse(Type type, string text, SnapshotPath path)
		{
			try
			{
				return SnapshotFormat.ParseValue(type, text);
			}
			catch (FormatException e)
			{
				throw new SnapshotLoadException($"{path}: {e.Message}", e);
			}
		}

		// A person can change a file by hand, so the reader builds only a type that a writer names.
		static void RequireSnapshotType(Type type, SnapshotPath path)
		{
			if (!SavePolicy.IsSnapshotType(type))
				throw new SnapshotLoadException($"{path}: a snapshot does not hold a {type.Name}, so a loader does not build one.");
		}

		// A type of the engine, the running mod or HostAssemblies, by full name.
		Type FindType(string fullName, SnapshotPath path)
		{
			if (types.TryGetValue(fullName, out var type))
				return type;

			type = Game.ModData.ObjectCreator.Assemblies.Concat(HostAssemblies)
				.Select(a => a.GetType(fullName))
				.FirstOrDefault(t => t != null)
				?? throw new SnapshotLoadException($"{path}: there is no type {fullName}.");

			types.Add(fullName, type);
			return type;
		}
	}
}
