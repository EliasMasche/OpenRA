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
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Reflection;
using Eluant;
using OpenRA.Activities;
using OpenRA.Effects;
using OpenRA.GameRules;
using OpenRA.Graphics;
using OpenRA.Primitives;
using OpenRA.Traits;

namespace OpenRA.Snapshot
{
	public enum SaveKind
	{
		NotSaved,

		/// <summary>Actor creation builds it again.</summary>
		Rebuilt,

		/// <summary>An IEnumerable query over traits. Saving its items would freeze it.</summary>
		Query,

		/// <summary>The world, its map, the engine, a random generator or a default comparer.</summary>
		Context,

		/// <summary>Drawing, sound or UI state, which the simulation never reads.</summary>
		Presentation,

		Value,
		ActorRef,
		PlayerRef,

		/// <summary>Saved as its player and actor ID. The frozen actor layer saves the frozen actor itself.</summary>
		FrozenActorRef,

		Target,
		RulesRef,
		TraitRef,

		/// <summary>An actor, player or trait held as object.</summary>
		AnyRef,

		/// <summary>An activity or an effect.</summary>
		Structure,

		Collection,

		/// <summary>
		/// An animation that plays once towards a callback. The snapshot holds its frame, and its holder attaches the
		/// callback after a load. Any other animation is a marker.
		/// </summary>
		Animation,

		/// <summary>An object that only this state owns, saved field by field.</summary>
		Owned,

		Unsupported,
	}

	/// <summary>
	/// Decides what a save writes for each field. A load creates actors normally and writes the saved state over
	/// them, so what creation fills is not saved. Activities, effects and owned objects come from the save alone.
	/// </summary>
	public static class SavePolicy
	{
		/// <summary>The fields a type declares itself, public or not. Walk <see cref="TypeChain"/> for the inherited ones.</summary>
		public const BindingFlags Declared = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly;

		static readonly ConcurrentDictionary<(FieldInfo Field, bool HolderCreated), SaveKind> FieldKinds = [];
		static readonly ConcurrentDictionary<(Type Type, bool RebuiltIfCreationFills), SaveKind> TypeKinds = [];
		static readonly ConcurrentDictionary<Assembly, bool> EngineOrModAssemblies = [];

		// Structs that read back as the printed value. WRot is not one: its angles do not give back its quaternion.
		static readonly Type[] ValueStructs =
		[
			typeof(WPos), typeof(WVec), typeof(WDist), typeof(WAngle),
			typeof(CPos), typeof(CVec), typeof(MPos), typeof(PPos), typeof(int2),
			typeof(Color), typeof(Size), typeof(Rectangle),
		];

		// Presentation types in the namespaces of the simulation.
		static readonly Type[] HostTypes =
		[
			typeof(Renderer), typeof(IPlatformWindow), typeof(IGraphicsContext), typeof(IHardwareCursor), typeof(IRenderer),
			typeof(IIndexBuffer), typeof(IShader), typeof(ITexture), typeof(IFrameBuffer), typeof(IFont),
			typeof(Sound), typeof(ISoundEngine), typeof(ISoundSource), typeof(ISound),
		];

		/// <summary><paramref name="holderIsCreated"/> is true for what actor creation builds: traits, actors, players, the world.</summary>
		public static SaveKind Classify(FieldInfo field, bool holderIsCreated)
		{
			return FieldKinds.GetOrAdd((field, holderIsCreated), key => ClassifyField(key.Field, key.HolderCreated));
		}

		static SaveKind ClassifyField(FieldInfo field, bool holderIsCreated)
		{
			if (IsNotSaved(SourceMember(field)))
				return SaveKind.NotSaved;

			if (IsPresentation(field.DeclaringType))
				return SaveKind.Presentation;

			if (holderIsCreated && IsQuery(field.FieldType))
				return SaveKind.Query;

			// Created fills an array of traits, so the field is not readonly, but creation still builds it again. An
			// array that other actors fill needs [SavedTraits].
			if (holderIsCreated && field.FieldType.IsArray && IsTrait(field.FieldType.GetElementType())
				&& !field.IsDefined(typeof(SavedTraitsAttribute), false))
				return SaveKind.Rebuilt;

			return Classify(field.FieldType, field.IsInitOnly && holderIsCreated);
		}

		/// <summary>Whether a snapshot holds a field of this kind. Creation gives a created holder its context.</summary>
		public static bool IsHeld(SaveKind kind, bool holderIsCreated)
		{
			return kind is not (SaveKind.NotSaved or SaveKind.Rebuilt or SaveKind.Presentation or SaveKind.Query)
				&& !(kind == SaveKind.Context && holderIsCreated);
		}

		/// <summary><paramref name="rebuiltIfCreationFills"/> is true for a readonly field of a created holder.</summary>
		public static SaveKind Classify(Type type, bool rebuiltIfCreationFills)
		{
			return TypeKinds.GetOrAdd((type, rebuiltIfCreationFills), key => ClassifyType(key.Type, key.RebuiltIfCreationFills));
		}

		static SaveKind ClassifyType(Type type, bool rebuiltIfCreationFills)
		{
			if (type == typeof(Animation))
				return SaveKind.Animation;

			if (IsPresentation(type))
				return SaveKind.Presentation;

			if (type.IsDefined(typeof(NotSavedAttribute), false))
				return SaveKind.NotSaved;

			if (typeof(Delegate).IsAssignableFrom(type))
				return rebuiltIfCreationFills ? SaveKind.Rebuilt : SaveKind.Unsupported;

			// Before the collections: a Lua table is a dictionary to .NET.
			if (typeof(LuaValue).IsAssignableFrom(type))
				return SaveKind.Unsupported;

			if (IsMemo(type) || IsPlayerMask(type))
				return SaveKind.Rebuilt;

			if (IsWorldContext(type) || SnapshotFormat.ComparedType(type) != null)
				return SaveKind.Context;

			if (rebuiltIfCreationFills && IsFilledByCreation(type))
				return SaveKind.Rebuilt;

			if (IsValue(type))
				return SaveKind.Value;

			if (type == typeof(Actor))
				return SaveKind.ActorRef;

			if (type == typeof(Player))
				return SaveKind.PlayerRef;

			if (type == typeof(object))
				return SaveKind.AnyRef;

			if (type == typeof(FrozenActor))
				return SaveKind.FrozenActorRef;

			if (type == typeof(Target))
				return SaveKind.Target;

			if (IsRules(type))
				return SaveKind.RulesRef;

			if (typeof(Activity).IsAssignableFrom(type) || typeof(IEffect).IsAssignableFrom(type))
				return SaveKind.Structure;

			if (IsTrait(type))
				return SaveKind.TraitRef;

			// Its capacity and start are state too.
			if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(RingBuffer<>))
				return SaveKind.Owned;

			var elements = ElementTypes(type);
			if (elements != null)
			{
				var kinds = elements.Select(e => Classify(e, false)).ToArray();
				if (kinds.Contains(SaveKind.Presentation))
					return SaveKind.Presentation;

				if (kinds.Contains(SaveKind.NotSaved))
					return SaveKind.NotSaved;

				return kinds.Contains(SaveKind.Unsupported) ? SaveKind.Unsupported : SaveKind.Collection;
			}

			if (type.IsValueType || IsEngineOrMod(type))
				return SaveKind.Owned;

			return SaveKind.Unsupported;
		}

		/// <summary>Whether a snapshot names <paramref name="value"/> wherever a field holds it, as the world holds it once.</summary>
		public static bool HasName(object value)
		{
			return Classify(value.GetType(), false)
				is SaveKind.ActorRef or SaveKind.PlayerRef or SaveKind.FrozenActorRef or SaveKind.Structure or SaveKind.TraitRef;
		}

		public static bool IsNotSaved(MemberInfo member)
		{
			for (var m = member; m != null; m = m.DeclaringType)
				if (m.IsDefined(typeof(NotSavedAttribute), false))
					return true;

			return false;
		}

		public static bool IsCosmetic(MemberInfo member)
		{
			for (var m = member; m != null; m = m.DeclaringType)
				if (m.IsDefined(typeof(CosmeticStateAttribute), false))
					return true;

			return false;
		}

		/// <summary>The property that owns an auto-property's backing field. [NotSaved] goes there.</summary>
		public static MemberInfo SourceMember(FieldInfo field)
		{
			var end = field.Name.IndexOf(">k__BackingField", StringComparison.Ordinal);
			if (field.Name.StartsWith('<') && end > 0)
				return field.DeclaringType.GetProperty(field.Name[1..end], Declared) ?? (MemberInfo)field;

			return field;
		}

		/// <summary>A field-like event's backing field. The event, not the field, carries [NotSaved].</summary>
		public static bool IsEventField(FieldInfo field)
		{
			return field.DeclaringType.GetEvent(field.Name, Declared) != null;
		}

		/// <summary><paramref name="type"/> and its base types, base first, without object and ValueType.</summary>
		public static IEnumerable<Type> TypeChain(Type type)
		{
			var chain = new List<Type>();
			for (var t = type; t != null && t != typeof(object) && t != typeof(ValueType); t = t.BaseType)
				chain.Add(t);

			chain.Reverse();
			return chain;
		}

		static readonly ConcurrentDictionary<Type, FieldKey[]> FieldKeyCache = [];

		public readonly record struct FieldKey(string Key, FieldInfo Field, MemberInfo Source, bool IsEvent);

		/// <summary>
		/// Every instance field, base type first, with its key. When a derived type declares a field with a name that
		/// is already in use, the key adds the declaring type.
		/// </summary>
		public static FieldKey[] FieldKeys(Type type)
		{
			return FieldKeyCache.GetOrAdd(type, t =>
			{
				var keys = new List<FieldKey>();
				var names = new HashSet<string>();
				foreach (var declaring in TypeChain(t))
				{
					foreach (var field in declaring.GetFields(Declared))
					{
						var source = SourceMember(field);
						keys.Add(new FieldKey(names.Add(source.Name) ? source.Name : $"{declaring.Name}.{source.Name}", field, source, IsEventField(field)));
					}
				}

				return keys.ToArray();
			});
		}

		static readonly ConcurrentDictionary<(Type Type, bool HolderCreated), SavedField[]> SavedFieldCache = [];

		public readonly record struct SavedField(string Key, FieldInfo Field, MemberInfo Source, SaveKind Kind, bool IsEvent);

		/// <summary>The held fields, and each event that creation does not subscribe again.</summary>
		public static SavedField[] SavedFields(Type type, bool holderIsCreated)
		{
			return SavedFieldCache.GetOrAdd((type, holderIsCreated), key => FieldKeys(key.Type)
				.Select(k => new SavedField(k.Key, k.Field, k.Source, k.IsEvent ? default : Classify(k.Field, key.HolderCreated), k.IsEvent))
				.Where(f => f.IsEvent
					? !IsNotSaved(f.Field.DeclaringType.GetEvent(f.Field.Name, Declared))
					: IsHeld(f.Kind, key.HolderCreated))
				.ToArray());
		}

		/// <summary>
		/// The constructors of an effect whose parameters each name a held field, most parameters first. A load runs
		/// one to build what a save leaves out, such as the animation of the effect.
		/// </summary>
		public static ConstructorInfo[] LoadConstructors(Type effectType)
		{
			var held = FieldKeys(effectType).Where(k => IsHeld(Classify(k.Field, false), false)).Select(k => k.Key).ToArray();
			const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
			return effectType.GetConstructors(Flags)
				.Where(c => c.GetParameters().All(p => held.Contains(p.Name, StringComparer.OrdinalIgnoreCase)))
				.OrderByDescending(c => c.GetParameters().Length)
				.ToArray();
		}

		/// <summary>The fields of an effect that only a constructor builds: an animation, and what it draws with.</summary>
		public static string[] BuiltByConstructor(Type effectType)
		{
			return FieldKeys(effectType)
				.Where(k => Classify(k.Field, false) is SaveKind.Animation or SaveKind.Presentation)
				.Select(k => k.Key)
				.ToArray();
		}

		/// <summary>Whether a load may build an object of a type that a snapshot names. A writer names no other type.</summary>
		public static bool IsSnapshotType(Type type)
		{
			if (type.IsAbstract || type.IsInterface)
				return false;

			return Classify(type, false) is SaveKind.Structure or SaveKind.Owned or SaveKind.Target;
		}

		public static bool IsLeftOut(Type type)
		{
			return Classify(type, false) is SaveKind.NotSaved or SaveKind.Presentation;
		}

		/// <summary>A persistent immutable collection, such as ImmutableList. ImmutableArray and builders are not.</summary>
		public static bool IsPersistentImmutable(Type type)
		{
			return type.Namespace == typeof(ImmutableList).Namespace && !type.IsNested
				&& !(type.IsGenericType && type.GetGenericTypeDefinition() == typeof(ImmutableArray<>));
		}

		public static bool IsDefaultImmutableArray(object value)
		{
			var type = value.GetType();
			return type.IsGenericType && type.GetGenericTypeDefinition() == typeof(ImmutableArray<>)
				&& (bool)type.GetProperty(nameof(ImmutableArray<int>.IsDefault)).GetValue(value);
		}

		/// <summary>The key and value types of a dictionary, the element type of another sequence, or null.</summary>
		public static Type[] ElementTypes(Type type)
		{
			if (type.IsArray)
				return [type.GetElementType()];

			if (type == typeof(string) || !typeof(IEnumerable).IsAssignableFrom(type))
				return null;

			// A player dictionary is both a list and a read-only dictionary. Its players do not change, so it is a
			// list.
			var collection = GenericInterface(type, typeof(IDictionary<,>)) ?? GenericInterface(type, typeof(IReadOnlyList<>))
				?? GenericInterface(type, typeof(IReadOnlyDictionary<,>)) ?? GenericInterface(type, typeof(IEnumerable<>));
			return collection?.GetGenericArguments();
		}

		static Type GenericInterface(Type type, Type definition)
		{
			if (type.IsGenericType && type.GetGenericTypeDefinition() == definition)
				return type;

			return type.GetInterfaces().FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == definition);
		}

		public static Type GenericBase(Type type, Type definition)
		{
			for (var t = type; t != null; t = t.BaseType)
				if (t.IsGenericType && t.GetGenericTypeDefinition() == definition)
					return t;

			return null;
		}

		public static Type CellLayerBase(Type type)
		{
			return GenericBase(type, typeof(CellLayerBase<>));
		}

		public static bool IsHashSet(Type type)
		{
			return type.IsGenericType && type.GetGenericTypeDefinition() == typeof(HashSet<>);
		}

		/// <summary>Whether an element always prints without a comma, so a list of them can share one line.</summary>
		public static bool IsOneLine(Type elementType)
		{
			return elementType.IsPrimitive || (elementType.IsEnum && !elementType.IsDefined(typeof(FlagsAttribute), false));
		}

		internal static IEnumerable<(object Key, object Value)> Entries(object value)
		{
			if (value is IDictionary dictionary)
			{
				foreach (DictionaryEntry entry in dictionary)
					yield return (entry.Key, entry.Value);

				yield break;
			}

			foreach (var pair in (IEnumerable)value)
			{
				var pairType = pair.GetType();
				yield return (pairType.GetProperty("Key").GetValue(pair), pairType.GetProperty("Value").GetValue(pair));
			}
		}

		public static bool IsValueStruct(Type type)
		{
			return ValueStructs.Contains(type);
		}

		public static Type ValueStruct(string name)
		{
			return ValueStructs.FirstOrDefault(t => t.Name == name);
		}

		static bool IsValue(Type type)
		{
			if (type.IsPrimitive || type.IsEnum || type == typeof(string) || type == typeof(decimal) || ValueStructs.Contains(type))
				return true;

			if (!type.IsGenericType)
				return false;

			var definition = type.GetGenericTypeDefinition();
			return definition == typeof(BitSet<>) || (definition == typeof(Nullable<>) && IsValue(type.GetGenericArguments()[0]));
		}

		// Rules or a trait of the actor. The snapshot holds an actor or a player, because creation can read one that
		// changed since.
		static bool IsFilledByCreation(Type type)
		{
			if (type.IsArray)
				return IsRules(type.GetElementType());

			return IsRules(type) || IsTrait(type);
		}

		// A load points each such field at the loaded world's own.
		static bool IsWorldContext(Type type)
		{
			return type == typeof(World) || type == typeof(Map) || type == typeof(ModData) || type == typeof(Network.OrderManager)
				|| type == typeof(Support.MersenneTwister);
		}

		public static bool IsPlayerMask(Type type)
		{
			return type.IsGenericType && type.GetGenericTypeDefinition() == typeof(LongBitSet<>);
		}

		static bool IsQuery(Type type)
		{
			return type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IEnumerable<>);
		}

		/// <summary>A cache or index that the first use or the objects it indexes fill again.</summary>
		public static bool IsMemo(Type type)
		{
			if (!type.IsGenericType)
				return false;

			var definition = type.GetGenericTypeDefinition();
			return definition == typeof(Lazy<>) || definition == typeof(CachedTransform<,>) || definition == typeof(Cache<,>)
				|| definition == typeof(SpatiallyPartitioned<>);
		}

		// A class named *Info counts, such as TerrainTypeInfo. A struct does not: path-search cells are structs named CellInfo.
		static bool IsRules(Type type)
		{
			return typeof(TraitInfo).IsAssignableFrom(type) || typeof(ITraitInfoInterface).IsAssignableFrom(type)
				|| type == typeof(ActorInfo) || type == typeof(WeaponInfo) || type == typeof(Ruleset)
				|| typeof(IWarhead).IsAssignableFrom(type) || typeof(IProjectileInfo).IsAssignableFrom(type)
				|| typeof(ITerrainInfo).IsAssignableFrom(type) || type == typeof(PlayerReference)
				|| (IsEngineOrMod(type) && type.IsClass && type.Name.EndsWith("Info", StringComparison.Ordinal));
		}

		// A trait interface, or a type with a TraitInfo of the same name on it or a base type.
		static bool IsTrait(Type type)
		{
			if (!IsEngineOrMod(type))
				return false;

			if (type.IsInterface)
			{
				var ns = type.Namespace ?? "";
				return ns.EndsWith(".Traits", StringComparison.Ordinal) || ns.Contains(".Traits.");
			}

			// A value type can be a trait: a marker trait can be a struct, and its actors share one boxed instance.
			if (type.IsArray || type.IsPrimitive || type.IsEnum)
				return false;

			for (var t = type; t != null && t != typeof(object); t = t.BaseType)
			{
				var tick = t.Name.IndexOf('`');
				var name = (tick > 0 ? t.Name[..tick] : t.Name) + "Info";
				var info = t.IsNested ? t.DeclaringType.GetNestedType(name, BindingFlags.Public | BindingFlags.NonPublic)
					: t.Assembly.GetType(t.Namespace + "." + name);
				if (info != null && typeof(TraitInfo).IsAssignableFrom(info))
					return true;
			}

			return false;
		}

		// A render or sound trait is not presentation: its counters decide when it draws from the shared random.
		static bool IsPresentation(Type type)
		{
			var ns = type.Namespace ?? "";
			return ns == "OpenRA.Graphics" || ns.EndsWith(".Graphics", StringComparison.Ordinal) || ns.EndsWith(".Widgets", StringComparison.Ordinal)
				|| ns.EndsWith(".Commands", StringComparison.Ordinal)
				|| ns.StartsWith("OpenRA.Platforms", StringComparison.Ordinal)
				|| type == typeof(ScreenMap) || HostTypes.Contains(type) || (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IVertexBuffer<>));
		}

		/// <summary>A type of an assembly that is the engine or references it.</summary>
		public static bool IsEngineOrMod(Type type)
		{
			return EngineOrModAssemblies.GetOrAdd(type.Assembly, assembly => assembly == typeof(Game).Assembly
				|| assembly.GetReferencedAssemblies().Any(a => a.Name == typeof(Game).Assembly.GetName().Name));
		}
	}
}
