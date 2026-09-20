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
using System.Globalization;
using System.Linq;
using System.Text;

namespace OpenRA.GameSaves
{
	public sealed class SnapshotDiff
	{
		public readonly record struct Entry(uint ActorID, string ActorType, string Owner, bool InWorld, string Trait, int Instance, int Hash);

		const string RandomKey = "Random";

		readonly List<Entry> entries;
		readonly int randomLast;
		readonly int randomCount;

		public IReadOnlyList<Entry> Entries => entries;

		SnapshotDiff(List<Entry> entries, int randomLast, int randomCount)
		{
			this.entries = entries;
			this.randomLast = randomLast;
			this.randomCount = randomCount;
		}

		public static SnapshotDiff Capture(World world)
		{
			ArgumentNullException.ThrowIfNull(world);

			var entries = new List<Entry>();

			foreach (var a in world.Actors.OrderBy(a => a.ActorID))
				entries.Add(new Entry(a.ActorID, a.Info.Name, a.Owner?.InternalName, a.IsInWorld, "<actor>", 0, Sync.HashActor(a)));

			foreach (var a in world.ActorsHavingTrait<ISync>().OrderBy(a => a.ActorID))
			{
				var instances = new Dictionary<string, int>();
				foreach (var sh in a.SyncHashes)
				{
					var name = sh.Trait.GetType().Name;
					instances.TryGetValue(name, out var instance);
					instances[name] = instance + 1;

					entries.Add(new Entry(a.ActorID, a.Info.Name, a.Owner?.InternalName, a.IsInWorld,
						name, instance, sh.Hash()));
				}
			}

			var effectIndex = 0;
			foreach (var effect in world.SyncedEffects)
				entries.Add(new Entry(0, "<effect>", null, true,
					$"{effect.GetType().Name}[{effectIndex++}]", 0, Sync.Hash(effect)));

			foreach (var player in world.Players)
				if (player.UnlockedRenderPlayer)
					entries.Add(new Entry(player.PlayerActor.ActorID, "<player>", player.InternalName, true,
						"<renderplayer>", 0, Sync.HashPlayer(player)));

			return new SnapshotDiff(entries, world.SharedRandom.Last, world.SharedRandom.TotalCount);
		}

		public List<MiniYamlNode> Save()
		{
			var nodes = new List<MiniYamlNode>(entries.Count + 1)
			{
				new(RandomKey, $"{randomLast.ToStringInvariant()}, {randomCount.ToStringInvariant()}")
			};

			for (var i = 0; i < entries.Count; i++)
			{
				var e = entries[i];

				nodes.Add(new MiniYamlNode(i.ToStringInvariant(),
					$"{e.ActorID.ToStringInvariant()}, {e.ActorType}, {e.Owner}, " +
					$"{(e.InWorld ? "1" : "0")}, {e.Trait}, {e.Instance.ToStringInvariant()}, {e.Hash.ToStringInvariant()}"));
			}

			return nodes;
		}

		public static SnapshotDiff Load(List<MiniYamlNode> nodes)
		{
			if (nodes == null)
				return null;

			try
			{
				var entries = new List<Entry>(nodes.Count);
				var randomLast = 0;
				var randomCount = 0;

				foreach (var node in nodes)
				{
					var parts = node.Value.Value.Split(',');

					if (node.Key == RandomKey)
					{
						randomLast = Exts.ParseInt32Invariant(parts[0].Trim());
						randomCount = Exts.ParseInt32Invariant(parts[1].Trim());
						continue;
					}

					var owner = parts[2].Trim();
					entries.Add(new Entry(
						uint.Parse(parts[0].Trim(), NumberStyles.None, NumberFormatInfo.InvariantInfo),
						parts[1].Trim(),
						owner.Length == 0 ? null : owner,
						parts[3].Trim() == "1",
						parts[4].Trim(),
						Exts.ParseInt32Invariant(parts[5].Trim()),
						Exts.ParseInt32Invariant(parts[6].Trim())));
				}

				return new SnapshotDiff(entries, randomLast, randomCount);
			}
			catch (Exception)
			{
				return null;
			}
		}

		public static string Compare(SnapshotDiff expected, SnapshotDiff actual)
		{
			ArgumentNullException.ThrowIfNull(expected);
			ArgumentNullException.ThrowIfNull(actual);

			var report = new StringBuilder();

			if (expected.randomLast != actual.randomLast)
				report.AppendLine(CultureInfo.InvariantCulture, $"  SharedRandom.Last: expected {expected.randomLast}, got {actual.randomLast}");

			if (expected.randomCount != actual.randomCount)
				report.AppendLine(CultureInfo.InvariantCulture,
					$"  SharedRandom.TotalCount: expected {expected.randomCount}, got {actual.randomCount}");

			var expectedByKey = expected.entries.ToLookup(e => (e.ActorID, e.Trait, e.Instance));
			var actualByKey = actual.entries.ToLookup(e => (e.ActorID, e.Trait, e.Instance));

			foreach (var e in expected.entries)
			{
				var match = actualByKey[(e.ActorID, e.Trait, e.Instance)].ToList();
				if (match.Count == 0)
				{
					report.AppendLine(CultureInfo.InvariantCulture, $"  missing: {Describe(e)}");
					continue;
				}

				if (match[0].Hash != e.Hash)
					report.AppendLine(CultureInfo.InvariantCulture, $"  {Describe(e)}: expected hash {e.Hash}, got {match[0].Hash}");

				if (match[0].InWorld != e.InWorld)
					report.AppendLine(CultureInfo.InvariantCulture, $"  {Describe(e)}: expected InWorld {e.InWorld}, got {match[0].InWorld}");

				if (match[0].Owner != e.Owner)
					report.AppendLine(CultureInfo.InvariantCulture, $"  {Describe(e)}: expected owner {e.Owner}, got {match[0].Owner}");
			}

			foreach (var a in actual.entries)
				if (!expectedByKey[(a.ActorID, a.Trait, a.Instance)].Any())
					report.AppendLine(CultureInfo.InvariantCulture, $"  unexpected: {Describe(a)}");

			return report.ToString();
		}

		static string Describe(Entry e)
		{
			if (e.ActorType == "<effect>")
				return $"effect {e.Trait}";

			if (e.ActorType == "<player>")
				return $"player {e.Owner} renders";

			var trait = e.Instance == 0 ? e.Trait : $"{e.Trait}#{e.Instance}";
			return $"actor {e.ActorID} ({e.ActorType}) trait {trait}";
		}
	}
}
