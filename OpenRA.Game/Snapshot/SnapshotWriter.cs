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
using System.Collections.Generic;
using System.Linq;
using OpenRA.Activities;
using OpenRA.Effects;
using OpenRA.Graphics;
using OpenRA.Scripting;
using OpenRA.Support;
using OpenRA.Traits;

namespace OpenRA.Snapshot
{
	/// <summary>The world holds state that a snapshot cannot represent. The message names the field.</summary>
	public class SnapshotRefusedException : Exception
	{
		public SnapshotRefusedException(string message)
			: base(message) { }
	}

	/// <summary>The world runs a script, whose state a snapshot does not hold. The save of every order still works.</summary>
	public sealed class ScriptStateRefusedException : SnapshotRefusedException
	{
		public ScriptStateRefusedException(string message)
			: base(message) { }
	}

	/// <summary>
	/// Writes the simulation state of a world as MiniYaml, following <see cref="SavePolicy"/>. It changes neither the
	/// state nor the sync hash. It does put each hash set and dictionary that it writes into the slot order that a load
	/// builds. A first pass finds each object or collection that two fields reach, and the writer writes it once, with
	/// an Id.
	/// </summary>
	public sealed class SnapshotWriter
	{
		/// <summary>The version of the text. A reader takes only its own.</summary>
		public const int Format = 1;

		/// <summary>The value of a field that holds null. An absent field keeps what creation built.</summary>
		public const string Null = "!null";

		static readonly MiniYamlNode FirstPassValue = new("", "");

		/// <summary>The key of an owned object's runtime type, written when it differs from the declared type.</summary>
		public const string TypeKey = "!Type";

		/// <summary>The key of the number of an owned object or a collection, written when more than one field shares it.</summary>
		public const string IdKey = "!Id";

		/// <summary>An animation that keeps the image of creation and has no frame to save. A load stops its wait.</summary>
		public const string IdleAnimation = "!idle";

		readonly World world;
		readonly Dictionary<object, (Actor Actor, string Key)> traits = new(ReferenceEqualityComparer.Instance);
		readonly Dictionary<object, string> playerRules = new(ReferenceEqualityComparer.Instance);
		SnapshotFormat.RuleNames ruleNames;
		readonly Dictionary<IEffect, int> effects = new(ReferenceEqualityComparer.Instance);
		readonly Dictionary<Activity, int> activityIndex = new(ReferenceEqualityComparer.Instance);
		readonly List<Activity> activities = [];
		readonly Dictionary<FrozenActor, string> frozenIndex = new(ReferenceEqualityComparer.Instance);
		readonly List<FrozenActor> frozenActors = [];
		readonly Dictionary<object, string> disposedTraits = new(ReferenceEqualityComparer.Instance);
		readonly SortedDictionary<uint, Actor> disposedActors = [];

		// The actor and the key of each trait of a dead actor that the state names. Null in the first pass.
		readonly Dictionary<object, (Actor Actor, string Key)> deadTraits;

		// Every owned reference object and collection reached so far. The first pass fills shared from it.
		readonly HashSet<object> owned = new(ReferenceEqualityComparer.Instance);

		// The owned objects and collections that more than one field reaches. Null during the first pass.
		readonly HashSet<object> shared;
		readonly Dictionary<object, int> sharedIds = new(ReferenceEqualityComparer.Instance);
		readonly HashSet<object> foundShared = new(ReferenceEqualityComparer.Instance);

		readonly HashSet<object> tidied = new(ReferenceEqualityComparer.Instance);

		// Why the first pass refuses the snapshot, or null. See Refuse.
		string refusal;

		readonly bool withoutCosmeticState;

		SnapshotWriter(World world, HashSet<object> shared, Dictionary<object, (Actor Actor, string Key)> deadTraits, bool withoutCosmeticState)
		{
			this.world = world;
			this.shared = shared;
			this.deadTraits = deadTraits;
			this.withoutCosmeticState = withoutCosmeticState;
		}

		/// <summary>
		/// Writes the world between ticks. <paramref name="withoutCosmeticState"/> leaves out [CosmeticState], for a comparison
		/// of two worlds. That text does not load.
		/// </summary>
		public static List<MiniYamlNode> Write(World world, bool withoutCosmeticState = false)
		{
			if (world.WorldActor.Info.HasTraitInfo<IWorldScriptInfo>())
				throw new ScriptStateRefusedException("the map runs a Lua script, and a snapshot does not hold its state.");

			var orders = OrdersSection.Write(world.OrderManager);

			// PERF: The first pass only walks the fields. It writes no map, no orders and no list of the dead actors.
			var firstPass = new SnapshotWriter(world, null, null, withoutCosmeticState);
			firstPass.WriteWorld(null);
			return new SnapshotWriter(world, firstPass.foundShared, firstPass.DeadActorTraits(), withoutCosmeticState).WriteWorld(orders);
		}

		// The first pass notes the first reason and continues. So it tidies every collection that it reaches, as each
		// client of a game of more people does on the same tick. The second pass throws at once.
		MiniYaml Refuse(string reason)
		{
			if (shared != null)
				throw new SnapshotRefusedException(reason);

			refusal ??= reason;
			return new MiniYaml(Null);
		}

		Dictionary<object, (Actor Actor, string Key)> DeadActorTraits()
		{
			var owners = new Dictionary<object, (Actor Actor, string Key)>(ReferenceEqualityComparer.Instance);
			foreach (var actor in disposedActors.Values)
			{
				var keys = SnapshotFormat.TraitKeys(actor.Info);
				var traitList = actor.TraitsAfterDisposal;
				if (traitList == null || traitList.Count != keys.Length)
					throw new SnapshotRefusedException($"actor {actor.ActorID} ({actor.Info.Name}) has died, and the traits it kept "
						+ "do not match its rules.");

				// A marker trait shares one boxed instance between actors, so the first key stands for all.
				for (var i = 0; i < keys.Length; i++)
					owners.TryAdd(traitList[i], (actor, keys[i]));
			}

			return owners;
		}

		// The first pass walks the fields only and returns null.
		List<MiniYamlNode> WriteWorld(List<MiniYamlNode> orders)
		{
			if (world.FrameEndActions.Count > 0)
				Refuse("frame-end tasks are pending. Write a snapshot between ticks.");

			IndexRules();
			var actors = IndexTraits();

			// Leaves out presentation effects, such as smoke. The cosmetic random decides when they appear.
			var effectList = world.Effects.Where(e => !SavePolicy.IsLeftOut(e.GetType())).ToList();
			for (var i = 0; i < effectList.Count; i++)
				effects.Add(effectList[i], i);

			var worldNode = new List<MiniYamlNode>
			{
				new(SnapshotFormat.Keys.Tick, FieldSaver.FormatValue(world.WorldTick)),
				new(SnapshotFormat.Keys.NextActorId, FieldSaver.FormatValue(world.NextActorId)),
				new(SnapshotFormat.Keys.SharedRandom, new MiniYaml("", Fields(world.SharedRandom, false, "World.SharedRandom"))),
				new(SnapshotFormat.Keys.LocalRandom, new MiniYaml("", Fields(world.LocalRandom, false, "World.LocalRandom"))),

				// Whether the world stands still is not in the sync hash, but it decides whether the next tick advances.
				new(SnapshotFormat.Keys.Paused, FieldSaver.FormatValue(world.Paused)),
				new(SnapshotFormat.Keys.PredictedPaused, FieldSaver.FormatValue(world.PredictedPaused)),
				new(SnapshotFormat.Keys.GameOver, FieldSaver.FormatValue(world.IsGameOver)),
			};

			var players = world.Players
				.Select(p => new MiniYamlNode(p.InternalName, new MiniYaml(FieldSaver.FormatValue(p.PlayerActor.ActorID),
					Fields(p, true, $"Player {p.InternalName}"))))
				.ToList();

			var actorNodes = actors.ConvertAll(WriteActor);
			var effectNodes = Entities(effectList, (_, i) => FieldSaver.FormatValue(i), e => e.GetType().FullName, "Effect");

			// The writer finds activities and frozen actors while it writes their holders, and they can name more.
			var activityNodes = Entities(activities, (_, i) => FieldSaver.FormatValue(i), a => a.GetType().FullName, "Activity");
			var frozenNodes = Entities(frozenActors, (fa, _) => frozenIndex[fa], _ => "", "FrozenActor");

			if (activityNodes.Count != activities.Count)
				Refuse("a frozen actor refers to an activity.");

			// Walks every field, so it tidies every collection.
			if (refusal != null)
				throw new SnapshotRefusedException(refusal);

			if (shared == null)
				return null;

			var header = new List<MiniYamlNode>
			{
				new(SnapshotFormat.Keys.Format, FieldSaver.FormatValue(Format)),
				new(SnapshotFormat.Keys.Mod, Game.ModData.Manifest.Id),
				new(SnapshotFormat.Keys.Map, world.Map.Uid),
			};

			var sections = new List<MiniYamlNode>
			{
				new(SnapshotFormat.Sections.Header, new MiniYaml("", header)),
				new(SnapshotFormat.Sections.World, new MiniYaml("", worldNode)),
				new(SnapshotFormat.Sections.Map, new MiniYaml("", MapSection.Write(world.Map))),
				new(SnapshotFormat.Sections.Players, new MiniYaml("", players)),
				new(SnapshotFormat.Sections.Actors, new MiniYaml("", actorNodes)),
				new(SnapshotFormat.Sections.Effects, new MiniYaml("", effectNodes)),
				new(SnapshotFormat.Sections.Activities, new MiniYaml("", activityNodes)),
				new(SnapshotFormat.Sections.FrozenActors, new MiniYaml("", frozenNodes)),
				new(SnapshotFormat.Sections.DisposedActors, new MiniYaml("", disposedActors.Values
					.Select(a => new MiniYamlNode(SnapshotFormat.ActorKey(a.ActorID), new MiniYaml(a.Info.Name, CreationInputs(a))))
					.ToList())),
				new(SnapshotFormat.Sections.Orders, new MiniYaml("", orders)),
			};

			return sections;
		}

		// The owner and the location, which are what a loader creates an actor from.
		static List<MiniYamlNode> CreationInputs(Actor actor)
		{
			var nodes = new List<MiniYamlNode> { new(SnapshotFormat.Keys.Owner, PlayerRef(actor.Owner)) };
			if (actor.OccupiesSpace != null)
				nodes.Add(new MiniYamlNode(SnapshotFormat.Keys.Location, SnapshotFormat.FormatValue(actor.OccupiesSpace.TopLeft)));

			return nodes;
		}

		// One node per entity. The list can grow during the write.
		List<MiniYamlNode> Entities<T>(List<T> list, Func<T, int, string> key, Func<T, string> value, string kind)
		{
			var nodes = new List<MiniYamlNode>();
			for (var i = 0; i < list.Count; i++)
			{
				var entity = list[i];
				var k = key(entity, i);
				nodes.Add(new MiniYamlNode(k, new MiniYaml(value(entity), Fields(entity, false, $"{kind} {k} {entity.GetType().Name}"))));
			}

			return nodes;
		}

		// Keys every trait of every actor that has traits, also outside the world. Refuses an actor whose traits are not those of its rules.
		List<Actor> IndexTraits()
		{
			var actors = new List<Actor>();
			foreach (var actor in world.TraitDict.ActorsWithTrait<object>().Select(p => p.Actor).Distinct().OrderBy(a => a.ActorID))
			{
				var infos = actor.Info.TraitsInConstructOrder().ToArray();
				var instances = world.TraitDict.WithInterface<object>(actor).ToArray();
				if (infos.Length != instances.Length)
				{
					Refuse($"actor {actor.ActorID} ({actor.Info.Name}) has {instances.Length} traits but its rules define {infos.Length}.");
					continue;
				}

				// A marker trait shares one boxed instance between actors, so the first key stands for all.
				var keys = SnapshotFormat.TraitKeys(actor.Info);
				for (var i = 0; i < infos.Length; i++)
					traits.TryAdd(instances[i], (actor, keys[i]));

				actors.Add(actor);
			}

			return actors;
		}

		void IndexRules()
		{
			ruleNames = SnapshotFormat.NamesOf(world.Map.Rules);
			foreach (var (rule, name) in SnapshotFormat.PlayerRules(world))
				playerRules.TryAdd(rule, name);
		}

		string RuleName(object value)
		{
			return ruleNames.NameOf.GetValueOrDefault(value) ?? playerRules.GetValueOrDefault(value);
		}

		MiniYamlNode WriteActor(Actor actor)
		{
			var path = $"Actor {actor.ActorID} ({actor.Info.Name})";

			// The creation inputs come first, because a loader creates the actor before it writes the fields.
			var nodes = CreationInputs(actor);
			nodes.Add(new MiniYamlNode(SnapshotFormat.Keys.Actor, new MiniYaml("", Fields(actor, true, path))));

			var traitNodes = world.TraitDict.WithInterface<object>(actor)
				.Where(t => traits[t].Actor == actor)
				.Select(t => new MiniYamlNode(traits[t].Key, new MiniYaml("", Fields(t, true, $"{path} {traits[t].Key}"))))
				.ToList();

			nodes.Add(new MiniYamlNode(SnapshotFormat.Keys.Traits, new MiniYaml("", traitNodes)));
			return new MiniYamlNode(SnapshotFormat.ActorKey(actor.ActorID), new MiniYaml(actor.Info.Name, nodes));
		}

		// The saved fields of holder, in the order and under the keys of SavePolicy.SavedFields.
		List<MiniYamlNode> Fields(object holder, bool isCreated, SnapshotPath path)
		{
			var nodes = new List<MiniYamlNode>();
			foreach (var (key, field, source, kind, isEvent) in SavePolicy.SavedFields(holder.GetType(), isCreated))
			{
				var type = field.DeclaringType;

				// Creation subscribes an event marked [NotSaved] again. Any other subscriber is a script.
				if (isEvent)
				{
					if (HasSubscribers((Delegate)field.GetValue(holder), holder, type))
						Refuse($"{path.Field(source.Name, type.Name)} is an event with subscribers.");

					continue;
				}

				if (withoutCosmeticState && SavePolicy.IsCosmetic(source))
					continue;

				// PERF: The first pass looks for shared objects only. A refusal counts the nodes of a holder, so a node stands in.
				if (kind == SaveKind.Value)
				{
					nodes.Add(shared == null ? FirstPassValue : Encode(key, kind, field.FieldType, field.GetValue(holder), path));
					continue;
				}

				// A field keeps what creation built when it holds an object that a save leaves out.
				var value = field.GetValue(holder);
				if (kind is SaveKind.Structure or SaveKind.Owned && value != null && SavePolicy.IsLeftOut(value.GetType()))
					continue;

				nodes.Add(Encode(key, kind, field.FieldType, value, path.Field(source.Name, type.Name)));
			}

			return nodes;
		}

		// Whether something outside the holder subscribed. A handler of the holder itself, or a lambda without state, does not count.
		static bool HasSubscribers(Delegate handlers, object holder, Type declaring)
		{
			return handlers != null && handlers.GetInvocationList().Any(h => !ReferenceEquals(h.Target, holder)
				&& (h.Method.DeclaringType?.DeclaringType != declaring
					|| !h.Method.DeclaringType.IsDefined(typeof(System.Runtime.CompilerServices.CompilerGeneratedAttribute), false)));
		}

		MiniYamlNode Encode(string key, SaveKind kind, Type declared, object value, SnapshotPath path)
		{
			if (value == null)
				return new MiniYamlNode(key, Null);

			switch (kind)
			{
				// PERF: The first pass formats no value.
				case SaveKind.Value:
					return new MiniYamlNode(key, shared == null ? "" : SnapshotFormat.FormatValue(value));

				case SaveKind.Collection:
					return new MiniYamlNode(key, Collection(declared, value, path));

				case SaveKind.Owned:
				case SaveKind.Target:
					if (SavePolicy.HasName(value) || RuleName(value) != null)
						return new MiniYamlNode(key, Reference(kind, declared, value, path));

					return new MiniYamlNode(key, OwnedObject(declared, value, path));

				case SaveKind.Animation:
					var frame = AnimationFrame((Animation)value);
					return frame.Count == 0 ? new MiniYamlNode(key, IdleAnimation) : new MiniYamlNode(key, new MiniYaml("", frame));

				case SaveKind.Unsupported:
					return new MiniYamlNode(key, Refuse($"{path} holds a {value.GetType().Name}, which no save rule covers."));

				default:
					return new MiniYamlNode(key, Reference(kind, declared, value, path));
			}
		}

		// The image and frame of an animation that plays once, repeats or stands at the end, as creation can start another
		// sequence. One that plays towards a callback is simulation state. The frame of any other only draws.
		List<MiniYamlNode> AnimationFrame(Animation animation)
		{
			var nodes = new List<MiniYamlNode>();
			if (animation.Name != animation.CreatedName)
				nodes.Add(new(SnapshotFormat.Keys.Image, animation.Name));

			if (!animation.WaitsToFinish && (!animation.AdvancesFrame || withoutCosmeticState))
				return nodes;

			var state = animation.Capture();
			nodes.Add(new(SnapshotFormat.Keys.Sequence, state.Sequence));
			nodes.Add(new(SnapshotFormat.Keys.Frame, FieldSaver.FormatValue(state.Frame)));
			nodes.Add(new(SnapshotFormat.Keys.Backwards, FieldSaver.FormatValue(state.Backwards)));
			nodes.Add(new(SnapshotFormat.Keys.TimeUntilNextFrame, FieldSaver.FormatValue(state.TimeUntilNextFrame)));
			if (state.Repeats)
				nodes.Add(new(SnapshotFormat.Keys.Repeats, FieldSaver.FormatValue(true)));
			else if (!state.WaitsToFinish)
				nodes.Add(new(SnapshotFormat.Keys.Finished, FieldSaver.FormatValue(true)));

			return nodes;
		}

		// The reference to an object or a collection that another field reached first, or null. id is its number, or -1.
		MiniYaml SharedReference(object value, SnapshotPath path, out int id)
		{
			id = -1;
			if (sharedIds.TryGetValue(value, out var sharedId))
				return new MiniYaml($"{SnapshotFormat.References.Object}{sharedId}");

			if (!owned.Add(value))
			{
				if (shared != null)
					throw new InvalidOperationException($"{path}: the first pass did not find this shared {value.GetType().Name}.");

				foundShared.Add(value);
				return new MiniYaml("");
			}

			// Numbered before its contents, so a field inside it that reaches it again finds the number.
			if (shared != null && shared.Contains(value))
			{
				id = sharedIds.Count;
				sharedIds.Add(value, id);
			}

			return null;
		}

		MiniYaml OwnedObject(Type declared, object value, SnapshotPath path)
		{
			var type = value.GetType();
			var id = -1;
			if (!type.IsValueType)
			{
				var reference = SharedReference(value, path, out id);
				if (reference != null)
					return reference;
			}

			var nodes = Fields(value, false, path);
			if (type != declared && type != Nullable.GetUnderlyingType(declared))
				nodes.Insert(0, new MiniYamlNode(TypeKey, type.FullName));

			if (id >= 0)
				nodes.Insert(0, new MiniYamlNode(IdKey, FieldSaver.FormatValue(id)));

			return new MiniYaml("", nodes);
		}

		// Writes a shared collection once, so a change through one field is visible through the others. An empty array
		// is never shared.
		MiniYaml Collection(Type declared, object value, SnapshotPath path)
		{
			var id = -1;
			if (!value.GetType().IsValueType && value is not Array { Length: 0 })
			{
				var reference = SharedReference(value, path, out id);
				if (reference != null)
					return reference;
			}

			var contents = CollectionContents(declared, value, path);
			return id < 0 ? contents
				: new MiniYaml(contents.Value, contents.Nodes.Prepend(new MiniYamlNode(IdKey, FieldSaver.FormatValue(id))));
		}

		// Numbers and plain enums share one line. Any other element has a child, as it can print a comma. An entry holds its key as the value.
		MiniYaml CollectionContents(Type declared, object value, SnapshotPath path)
		{
			Tidy(value);
			var elementTypes = SavePolicy.ElementTypes(declared) ?? SavePolicy.ElementTypes(value.GetType());
			var elementKinds = elementTypes.Select(e => SavePolicy.Classify(e, false)).ToArray();
			if (elementTypes.Length == 2)
			{
				return new MiniYaml("", SavePolicy.Entries(value).Select((entry, i) =>
				{
					var entryPath = path.Element(i);
					var key = Encode(SnapshotFormat.Keys.EntryKey, elementKinds[0], elementTypes[0], entry.Key, entryPath);
					if (key.Value.Nodes.Length > 0)
						return new MiniYamlNode(FieldSaver.FormatValue(i), Refuse($"{entryPath} has a key that is not a single value."));

					return new MiniYamlNode(FieldSaver.FormatValue(i),
						new MiniYaml(key.Value.Value, [Encode(SnapshotFormat.Keys.EntryValue, elementKinds[1], elementTypes[1], entry.Value, entryPath)]));
				}).ToList());
			}

			if (SavePolicy.IsDefaultImmutableArray(value))
				return new MiniYaml(Null);

			if (SavePolicy.IsOneLine(elementTypes[0]))
				return new MiniYaml(shared == null ? "" : SnapshotFormat.FormatOneLine(value, elementTypes[0]));

			var items = ((IEnumerable)value).Cast<object>();

			var sparse = SavePolicy.CellLayerBase(value.GetType()) != null;
			return new MiniYaml("", items
				.Select((item, i) => (Item: item, Index: i))
				.Where(e => !sparse || e.Item != null)
				.Select(e => Encode(FieldSaver.FormatValue(e.Index), elementKinds[0], elementTypes[0], e.Item, path.Element(e.Index)))
				.ToList());
		}

		// A hash set or a dictionary reuses freed slots, and a loaded one has none, so the two enumerate differently.
		// So the writer fills each one again in its order, which is the layout that a load builds. TrimExcess keeps
		// freed slots.
		void Tidy(object collection)
		{
			// The first pass reaches every collection before the second pass writes it.
			if (shared != null || !tidied.Add(collection))
				return;

			var type = collection.GetType();
			if (SavePolicy.IsHashSet(type))
				SnapshotFormat.FillSet(collection, ((IEnumerable)collection).Cast<object>().ToArray());
			else if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Dictionary<,>))
			{
				var dictionary = (IDictionary)collection;
				var entries = new DictionaryEntry[dictionary.Count];
				dictionary.CopyTo(entries, 0);
				dictionary.Clear();
				foreach (var entry in entries)
					dictionary.Add(entry.Key, entry.Value);
			}
		}

		MiniYaml Reference(SaveKind kind, Type declared, object value, SnapshotPath path)
		{
			switch (value)
			{
				case Actor actor:
					return new MiniYaml(ActorRef(actor));
				case Player player:
					return new MiniYaml(PlayerRef(player));
				case FrozenActor frozen:
					return new MiniYaml(FrozenRef(frozen));
				case Activity activity:
					return new MiniYaml(ActivityRef(activity));
				case IEffect effect when SavePolicy.IsLeftOut(effect.GetType()):
					return new MiniYaml(Null);
				case IEffect effect:
					// An effect that is not in the list of the world belongs to the holder that adds and removes it.
					return effects.TryGetValue(effect, out var index)
						? new MiniYaml($"{SnapshotFormat.References.Effect}{index}")
						: OwnedObject(declared, value, path);
				case World:
					return new MiniYaml(SnapshotFormat.References.World);
				case Map:
					return new MiniYaml(SnapshotFormat.References.Map);
				case MersenneTwister random:
					if (random == world.SharedRandom)
						return new MiniYaml(SnapshotFormat.References.SharedRandom);

					if (random == world.LocalRandom)
						return new MiniYaml(SnapshotFormat.References.LocalRandom);

					return Refuse($"{path} holds a random generator the world does not own.");
			}

			if (traits.TryGetValue(value, out var trait))
				return new MiniYaml($"{SnapshotFormat.References.Start}{trait.Actor.ActorID}{SnapshotFormat.References.TraitSeparator}{trait.Key}");

			var rule = RuleName(value);
			if (rule != null)
				return new MiniYaml(rule);

			if (SnapshotFormat.ComparedType(declared) != null && ReferenceEquals(value, SnapshotFormat.DefaultComparerFor(declared)))
				return new MiniYaml(SnapshotFormat.DefaultComparer);

			if (kind == SaveKind.Context)
				return Refuse($"{path} holds engine context ({value.GetType().Name}) that a snapshot cannot name.");

			var valueKind = SavePolicy.Classify(value.GetType(), false);
			if (valueKind is SaveKind.TraitRef or SaveKind.Presentation or SaveKind.NotSaved)
				return DisposedTrait(value, valueKind == SaveKind.TraitRef, path);

			// Takes an interface from a traits namespace for a trait. An object behind it that is not a trait is state
			// of its holders. An example is the run that the aircraft of one support power share.
			if (valueKind == SaveKind.Owned)
				return OwnedObject(declared, value, path);

			return Refuse($"{path} holds a {value.GetType().Name} that is not an actor, player, trait or rule.");
		}

		// A trait of a dead actor, such as the armament that a bullet follows. The writer writes it where it first
		// reaches it, under the dead actor and its key. It only names a presentation trait.
		MiniYaml DisposedTrait(object trait, bool writeFields, SnapshotPath path)
		{
			if (disposedTraits.TryGetValue(trait, out var name))
				return new MiniYaml(name);

			// The first pass only finds the dead actors that the state names, so it cannot name their traits yet.
			if (deadTraits == null)
				name = "";
			else if (deadTraits.TryGetValue(trait, out var owner))
				name = $"{ActorRef(owner.Actor)}{SnapshotFormat.References.TraitSeparator}{owner.Key}";
			else
				return Refuse($"{path} holds a {trait.GetType().Name} that belongs to no actor in the world, "
					+ "and to no actor that has died and that the snapshot names.");

			disposedTraits.Add(trait, name);
			return writeFields ? new MiniYaml(name, Fields(trait, true, path)) : new MiniYaml(name);
		}

		// Names an actor that no longer exists with !disposed, and lists it under DisposedActors.
		string ActorRef(Actor actor)
		{
			if (!actor.Disposed)
				return $"{SnapshotFormat.References.Start}{actor.ActorID}";

			disposedActors.TryAdd(actor.ActorID, actor);
			return $"{SnapshotFormat.References.Start}{actor.ActorID}{SnapshotFormat.References.Disposed}";
		}

		static string PlayerRef(Player player)
		{
			return SnapshotFormat.References.Player + player.InternalName;
		}

		string FrozenRef(FrozenActor frozen)
		{
			if (!frozenIndex.TryGetValue(frozen, out var key))
			{
				key = $"{frozen.Viewer.InternalName}/{frozen.ID}";
				frozenIndex.Add(frozen, key);
				frozenActors.Add(frozen);
			}

			return SnapshotFormat.References.Frozen + key;
		}

		string ActivityRef(Activity activity)
		{
			if (!activityIndex.TryGetValue(activity, out var index))
			{
				index = activities.Count;
				activityIndex.Add(activity, index);
				activities.Add(activity);
			}

			return $"{SnapshotFormat.References.Activity}{index}";
		}
	}
}
