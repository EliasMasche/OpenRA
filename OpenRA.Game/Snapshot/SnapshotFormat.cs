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
using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;

namespace OpenRA.Snapshot
{
	/// <summary>The names that a snapshot gives to traits and rules, and how a written value reads back.</summary>
	public static class SnapshotFormat
	{
		const string QuotedStart = "!\"";

		static readonly MethodInfo ParseByFieldLoaderMethod =
			typeof(SnapshotFormat).GetMethod(nameof(ParseByFieldLoader), BindingFlags.NonPublic | BindingFlags.Static);
		static readonly ConcurrentDictionary<Type, Func<string, object>> Parsers = [];
		static readonly ConditionalWeakTable<ActorInfo, string[]> TraitKeysByActor = [];

		public const string ActorKeyPrefix = "Actor";

		public static string ActorKey(uint actorId)
		{
			return ActorKeyPrefix + actorId.ToString(CultureInfo.InvariantCulture);
		}

		/// <summary>
		/// Prints a value as FieldSaver does. Quotes and escapes a string that one line of MiniYaml cannot hold. Prints
		/// with the invariant culture when the current culture has another minus sign.
		/// </summary>
		public static string FormatValue(object value)
		{
			if (value is not string text)
			{
				var culture = CultureInfo.CurrentCulture;
				if (culture.NumberFormat.NegativeSign == NumberFormatInfo.InvariantInfo.NegativeSign)
					return Print(value);

				CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
				try
				{
					return Print(value);
				}
				finally
				{
					CultureInfo.CurrentCulture = culture;
				}
			}

			var plain = text.Length == 0 || (!char.IsWhiteSpace(text[0]) && !char.IsWhiteSpace(text[^1])
				&& text[0] != '!' && text.AsSpan().IndexOfAny('\\', '\n', '\r') < 0);

			return plain ? text : QuotedStart + text.Replace("\\", "\\\\").Replace("\n", "\\n").Replace("\r", "\\r") + "\"";
		}

		// PERF: FieldSaver looks for a type converter for each value.
		static string Print(object value)
		{
			return value switch
			{
				bool v => v ? bool.TrueString : bool.FalseString,
				int v => v.ToString(NumberFormatInfo.InvariantInfo),
				byte v => v.ToString(NumberFormatInfo.InvariantInfo),
				uint v => v.ToString(NumberFormatInfo.InvariantInfo),
				float v => v.ToString("R", NumberFormatInfo.InvariantInfo),
				CPos or PPos or WPos or int2 or WAngle or Enum => value.ToString(),
				_ => FieldSaver.FormatValue(value),
			};
		}

		static string Unquote(string text)
		{
			if (text.Length < QuotedStart.Length + 1 || !text.StartsWith(QuotedStart, StringComparison.Ordinal) || text[^1] != '"')
				return text;

			var result = new System.Text.StringBuilder(text.Length);
			for (var i = QuotedStart.Length; i < text.Length - 1; i++)
			{
				if (text[i] != '\\' || i + 1 >= text.Length - 1)
				{
					result.Append(text[i]);
					continue;
				}

				var escaped = text[++i];
				result.Append(escaped == 'n' ? '\n' : escaped == 'r' ? '\r' : escaped);
			}

			return result.ToString();
		}

		/// <summary>Reads a value that <see cref="FormatValue"/> printed. The exception names <paramref name="path"/>.</summary>
		public static object ParseValue(Type type, string text, string path)
		{
			try
			{
				return ParseValue(type, text);
			}
			catch (FormatException e)
			{
				throw new FormatException($"{path}: {e.Message}", e.InnerException ?? e);
			}
		}

		public static object ParseValue(Type type, string text)
		{
			if (type == typeof(string))
				return Unquote(text);

			// PERF: FieldLoader reads a number through reflection.
			if (type == typeof(int) && Exts.TryParseInt32Invariant(text, out var number))
				return number;

			if (type == typeof(short) && short.TryParse(text, NumberStyles.Integer, NumberFormatInfo.InvariantInfo, out var shortNumber))
				return shortNumber;

			if (type == typeof(uint) && uint.TryParse(text, NumberStyles.Integer, NumberFormatInfo.InvariantInfo, out var unsignedNumber))
				return unsignedNumber;

			if (type == typeof(byte) && byte.TryParse(text, NumberStyles.Integer, NumberFormatInfo.InvariantInfo, out var byteNumber))
				return byteNumber;

			if (type == typeof(bool) && bool.TryParse(text, out var flag))
				return flag;

			if (type == typeof(long) && long.TryParse(text, NumberStyles.Integer, NumberFormatInfo.InvariantInfo, out var longNumber))
				return longNumber;

			try
			{
				return Parsers.GetOrAdd(type, t => ParseByFieldLoaderMethod.MakeGenericMethod(t).CreateDelegate<Func<string, object>>())(text);
			}
			catch (Exception e)
			{
				throw new FormatException($"\"{text}\" does not read as {type.Name}: {e.Message}", e);
			}
		}

		static object ParseByFieldLoader<T>(string text)
		{
			return FieldLoader.GetValue<T>("value", text);
		}

		/// <summary>The name of the default comparer of a type. No other comparer can be saved.</summary>
		public const string DefaultComparer = "@default-comparer";

		public static Type ComparedType(Type type)
		{
			return type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IComparer<>) ? type.GetGenericArguments()[0] : null;
		}

		/// <summary>The snapshot holds only the default comparer, so the declared type finds it.</summary>
		public static object DefaultComparerFor(Type declared)
		{
			return typeof(Comparer<>).MakeGenericType(ComparedType(declared)).GetProperty(nameof(Comparer<int>.Default)).GetValue(null);
		}

		static readonly MethodInfo FormatOneLineOfMethod = typeof(SnapshotFormat).GetMethod(nameof(FormatOneLineOf), BindingFlags.NonPublic | BindingFlags.Static);
		static readonly MethodInfo ParseOneLineOfMethod = typeof(SnapshotFormat).GetMethod(nameof(ParseOneLineOf), BindingFlags.NonPublic | BindingFlags.Static);
		static readonly ConcurrentDictionary<Type, Func<object, string>> OneLineFormatters = [];
		static readonly ConcurrentDictionary<Type, Func<string, SnapshotPath, Array>> OneLineParsers = [];

		/// <summary>The separator of a value and the number of elements in a row that hold it, as in "False*120".</summary>
		public const char Run = '*';

		// A run names its length, and a file can be changed by hand.
		const int MaxOneLineElements = 1 << 24;

		/// <summary>
		/// The elements of a one-line collection, joined by ", ". Equal elements in a row are one run, as in "False*120".
		/// PERF: booleans, integers and enums print without a box.
		/// </summary>
		public static string FormatOneLine(object collection, Type elementType)
		{
			return OneLineFormatters.GetOrAdd(elementType,
				t => FormatOneLineOfMethod.MakeGenericMethod(t).CreateDelegate<Func<object, string>>())(collection);
		}

		/// <summary>Reads what <see cref="FormatOneLine"/> printed. The exception names <paramref name="path"/>.</summary>
		public static Array ParseOneLine(Type elementType, string text, SnapshotPath path)
		{
			return OneLineParsers.GetOrAdd(elementType,
				t => ParseOneLineOfMethod.MakeGenericMethod(t).CreateDelegate<Func<string, SnapshotPath, Array>>())(text, path);
		}

		static string FormatOneLineOf<T>(object collection)
		{
			var text = new StringBuilder();
			var comparer = EqualityComparer<T>.Default;
			var value = default(T);
			var count = 0;
			foreach (var item in (IEnumerable<T>)collection)
			{
				if (count > 0 && comparer.Equals(item, value))
				{
					count++;
					continue;
				}

				AppendRun(text, value, count);
				value = item;
				count = 1;
			}

			AppendRun(text, value, count);
			return text.ToString();
		}

		static void AppendRun<T>(StringBuilder text, T value, int count)
		{
			if (count == 0)
				return;

			if (text.Length > 0)
				text.Append(", ");

			text.Append(OneLine<T>.Format(value));
			if (count > 1)
				text.Append(Run).Append(count.ToString(NumberFormatInfo.InvariantInfo));
		}

		static Array ParseOneLineOf<T>(string text, SnapshotPath path)
		{
			var items = new List<T>();
			foreach (var element in text.AsSpan().Split(','))
			{
				var part = element.Trim();
				if (part.IsEmpty)
					continue;

				var count = 1;
				var run = part.LastIndexOf(Run);
				if (run >= 0)
				{
					if (!int.TryParse(part[(run + 1)..], NumberStyles.None, NumberFormatInfo.InvariantInfo, out count)
						|| count < 2 || count > MaxOneLineElements - items.Count)
						throw new FormatException($"{path}: \"{part}\" is not a run of a value.");

					part = part[..run].TrimEnd();
				}

				if (!OneLine<T>.TryParse(part, out var item))
				{
					try
					{
						item = (T)ParseValue(typeof(T), part.ToString());
					}
					catch (FormatException e)
					{
						throw new FormatException($"{path}: {e.Message}", e.InnerException ?? e);
					}
				}

				for (var i = 0; i < count; i++)
					items.Add(item);

				if (items.Count > MaxOneLineElements)
					throw new FormatException($"{path}: the line has more than {MaxOneLineElements} elements.");
			}

			return items.ToArray();
		}

		delegate bool SpanParser<T>(ReadOnlySpan<char> text, out T value);

		static class OneLine<T>
		{
			public static readonly Func<T, string> Format;
			public static readonly SpanParser<T> TryParse;

			static OneLine()
			{
				var type = typeof(T);
				var ways = type switch
				{
					_ when type == typeof(bool) => ((Func<bool, string>)(v => v ? "True" : "False"),
						(SpanParser<bool>)((ReadOnlySpan<char> s, out bool v) => bool.TryParse(s, out v))),
					_ when type == typeof(int) => Integer<int>(),
					_ when type == typeof(short) => Integer<short>(),
					_ when type == typeof(byte) => Integer<byte>(),
					_ when type == typeof(uint) => Integer<uint>(),
					_ when type == typeof(ushort) => Integer<ushort>(),
					_ when type == typeof(sbyte) => Integer<sbyte>(),
					_ when type == typeof(long) => Integer<long>(),
					_ => (null, null),
				};

				if (ways.Print != null)
				{
					Format = (Func<T, string>)ways.Print;
					TryParse = (SpanParser<T>)ways.Read;
					return;
				}

				if (type.IsEnum)
				{
					var values = Enum.GetValues(type).Cast<T>().Distinct().ToArray();
					var names = values.ToDictionary(v => v, v => FieldSaver.FormatValue(v));
					var lookup = values.ToDictionary(v => names[v], v => v).GetAlternateLookup<ReadOnlySpan<char>>();
					Format = v => names.TryGetValue(v, out var name) ? name : FieldSaver.FormatValue(v);
					TryParse = lookup.TryGetValue;
					return;
				}

				Format = v => FieldSaver.FormatValue(v);
				TryParse = (ReadOnlySpan<char> s, out T v) =>
				{
					v = default;
					return false;
				};
			}

			static (Delegate Print, Delegate Read) Integer<TInteger>()
				where TInteger : IBinaryInteger<TInteger>
			{
				var invariant = NumberFormatInfo.InvariantInfo;
				return ((Func<TInteger, string>)(v => v.ToString(null, invariant)),
					(SpanParser<TInteger>)((ReadOnlySpan<char> s, out TInteger v) => TInteger.TryParse(s, NumberStyles.Integer, invariant, out v)));
			}
		}

		static readonly MethodInfo FillSetOfMethod = typeof(SnapshotFormat).GetMethod(nameof(FillSetOf), BindingFlags.NonPublic | BindingFlags.Static);
		static readonly ConcurrentDictionary<Type, Action<object, object[]>> SetFillers = [];

		/// <summary>Empties a hash set and adds <paramref name="items"/> in their order.</summary>
		public static void FillSet(object set, object[] items)
		{
			var fill = SetFillers.GetOrAdd(set.GetType().GetGenericArguments()[0],
				t => FillSetOfMethod.MakeGenericMethod(t).CreateDelegate<Action<object, object[]>>());

			fill(set, items);
		}

		static void FillSetOf<T>(object set, object[] items)
		{
			var typed = (HashSet<T>)set;
			typed.Clear();
			foreach (var item in items)
				typed.Add((T)item);
		}

		/// <summary>
		/// The key of each trait in construct order. The key is its info type name without "Info", then @ and its
		/// instance name, then ~1, ~2 for a repeat. Only the rules decide it, so a dead actor gets the same keys.
		/// </summary>
		public static string[] TraitKeys(ActorInfo info)
		{
			return TraitKeysByActor.GetValue(info, ComputeTraitKeys);
		}

		static string[] ComputeTraitKeys(ActorInfo info)
		{
			var used = new Dictionary<string, int>();
			return info.TraitsInConstructOrder().Select(trait =>
			{
				var typeName = trait.GetType().Name;
				var name = typeName.EndsWith("Info", StringComparison.Ordinal) ? typeName[..^4] : typeName;
				var key = string.IsNullOrEmpty(trait.InstanceName) ? name : $"{name}@{trait.InstanceName}";
				var count = used.GetValueOrDefault(key);
				used[key] = count + 1;
				return count == 0 ? key : $"{key}~{count}";
			}).ToArray();
		}

		/// <summary>
		/// Every rules object that a snapshot can name. The snapshot prints the first name of an object.
		/// </summary>
		public static IEnumerable<(object Rule, string Name)> Rules(World world)
		{
			return RulesOf(world.Map.Rules).Concat(PlayerRules(world));
		}

		/// <summary>The rules objects of the players of <paramref name="world"/>, which are not in its ruleset.</summary>
		public static IEnumerable<(object Rule, string Name)> PlayerRules(World world)
		{
			foreach (var player in world.Players)
				if (player.PlayerReference != null)
					yield return (player.PlayerReference, $"@rules/player/{player.InternalName}");
		}

		static readonly ConditionalWeakTable<Ruleset, RuleNames> RuleNamesByRuleset = [];

		public sealed class RuleNames
		{
			public readonly FrozenDictionary<object, string> NameOf;
			public readonly FrozenDictionary<string, object> RuleOf;

			internal RuleNames(Ruleset ruleset)
			{
				var names = new Dictionary<object, string>(ReferenceEqualityComparer.Instance);
				var rules = new Dictionary<string, object>();
				foreach (var (rule, name) in RulesOf(ruleset))
				{
					names.TryAdd(rule, name);
					rules.TryAdd(name, rule);
				}

				NameOf = names.ToFrozenDictionary(ReferenceEqualityComparer.Instance);
				RuleOf = rules.ToFrozenDictionary();
			}
		}

		/// <summary>PERF: they change only with the ruleset.</summary>
		public static RuleNames NamesOf(Ruleset ruleset)
		{
			return RuleNamesByRuleset.GetValue(ruleset, r => new RuleNames(r));
		}

		static IEnumerable<(object Rule, string Name)> RulesOf(Ruleset ruleset)
		{
			yield return (ruleset, "@rules");
			yield return (ruleset.TerrainInfo, "@rules/terrain");
			foreach (var terrainType in ruleset.TerrainInfo.TerrainTypes)
				yield return (terrainType, $"@rules/terrain/type/{terrainType.Type}");

			foreach (var (name, info) in ruleset.Actors)
			{
				yield return (info, $"@rules/actor/{name}");
				var keys = TraitKeys(info);
				var infos = info.TraitsInConstructOrder().ToArray();
				for (var i = 0; i < infos.Length; i++)
					yield return (infos[i], $"@rules/trait/{name}/{keys[i]}");
			}

			foreach (var (name, weapon) in ruleset.Weapons)
			{
				yield return (weapon, $"@rules/weapon/{name}");
				if (weapon.Projectile != null)
					yield return (weapon.Projectile, $"@rules/weapon/{name}/projectile");

				for (var i = 0; i < weapon.Warheads.Length; i++)
					yield return (weapon.Warheads[i], $"@rules/weapon/{name}/warhead/{i}");
			}

			foreach (var (name, music) in ruleset.Music)
				yield return (music, $"@rules/music/{name}");

			foreach (var (name, voice) in ruleset.Voices)
				yield return (voice, $"@rules/voice/{name}");

			foreach (var (name, notification) in ruleset.Notifications)
				yield return (notification, $"@rules/notification/{name}");
		}

		/// <summary>The sections of a snapshot, in the order the writer puts them.</summary>
		public static class Sections
		{
			public const string Header = "Snapshot";
			public const string World = "World";
			public const string Map = "Map";
			public const string Players = "Players";
			public const string Actors = "Actors";
			public const string Effects = "Effects";
			public const string Activities = "Activities";
			public const string FrozenActors = "FrozenActors";
			public const string DisposedActors = "DisposedActors";
			public const string Orders = "Orders";
		}

		/// <summary>The keys of what is not a field of a saved object. A field uses its own name as its key.</summary>
		public static class Keys
		{
			public const string Format = "Format";
			public const string Mod = "Mod";
			public const string Map = "Map";

			public const string Tick = "Tick";
			public const string NextActorId = "NextActorID";
			public const string SharedRandom = "SharedRandom";
			public const string LocalRandom = "LocalRandom";
			public const string Paused = "Paused";
			public const string PredictedPaused = "PredictedPaused";
			public const string GameOver = "GameOver";

			public const string Tiles = "Tiles";
			public const string CustomTerrain = "CustomTerrain";

			public const string Owner = "Owner";
			public const string Location = "Location";
			public const string Actor = "Actor";
			public const string Traits = "Traits";

			public const string EntryKey = "Key";
			public const string EntryValue = "Value";

			public const string Image = "Image";
			public const string Sequence = "Sequence";
			public const string Frame = "Frame";
			public const string Backwards = "Backwards";
			public const string TimeUntilNextFrame = "TimeUntilNextFrame";
			public const string Repeats = "Repeats";
			public const string Finished = "Finished";

			public const string LocalFrame = "LocalFrame";
			public const string NetFrame = "NetFrame";
			public const string SentOrdersFrame = "SentOrdersFrame";
			public const string Packets = "Packets";
			public const string Client = "Client";
			public const string PacketFrame = "Frame";
			public const string PacketOrders = "Orders";
			public const string LocalOrders = "Local";
			public const string Left = "Left";
		}

		/// <summary>How a field names an object that another entry holds: an actor by its ID, a trait as actor/key.</summary>
		public static class References
		{
			public const string Start = "@";
			public const string Disposed = "!disposed";
			public const char TraitSeparator = '/';

			public const string World = "@world";
			public const string Map = "@map";
			public const string SharedRandom = "@shared-random";
			public const string LocalRandom = "@local-random";

			public const string Player = "@player/";
			public const string Activity = "@activity/";
			public const string Effect = "@effect/";
			public const string Frozen = "@frozen/";
			public const string Object = "@object/";
		}
	}
}
