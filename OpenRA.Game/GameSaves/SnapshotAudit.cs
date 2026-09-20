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
using System.Text;

namespace OpenRA.GameSaves
{
	public sealed class SnapshotAudit
	{
		public const string LogDirectory = "Logs";

		public static string LogFile => $"snapshot.{Environment.ProcessId}.log";

		readonly Dictionary<string, int> counts = [];
		readonly Dictionary<string, uint> lowestId = [];

		public int Total { get; private set; }

		public static void Write(string text)
		{
			var path = Path.Combine(Platform.SupportDir, LogDirectory, LogFile);

			try
			{
				Directory.CreateDirectory(Path.GetDirectoryName(path));
				File.AppendAllText(path, text + Environment.NewLine, Encoding.UTF8);
			}
			catch (Exception e)
			{
				Console.WriteLine($"Could not write {path}: {e.Message}");
			}
		}

		public void Add(Actor actor)
		{
			ArgumentNullException.ThrowIfNull(actor);

			var type = actor.Info.Name;

			counts[type] = counts.GetValueOrDefault(type) + 1;
			Total++;

			if (!lowestId.TryGetValue(type, out var id) || actor.ActorID < id)
				lowestId[type] = actor.ActorID;
		}

		public static SnapshotAudit OfWorld(World world)
		{
			ArgumentNullException.ThrowIfNull(world);

			var audit = new SnapshotAudit();
			var players = world.Players.Select(p => p.PlayerActor).ToHashSet();

			foreach (var actor in world.Actors.OrderBy(a => a.ActorID))
				if (actor != world.WorldActor && !players.Contains(actor))
					audit.Add(actor);

			return audit;
		}

		public IEnumerable<string> DifferencesFrom(SnapshotAudit other)
		{
			ArgumentNullException.ThrowIfNull(other);

			foreach (var (type, count) in counts.OrderBy(kv => kv.Key, StringComparer.Ordinal))
			{
				var theirs = other.counts.GetValueOrDefault(type);
				if (theirs == count)
					continue;

				var delta = count - theirs;
				yield return $"{type}: {count} here, {theirs} there ({(delta > 0 ? "-" : "+")}{Math.Abs(delta)})";
			}

			foreach (var (type, count) in other.counts.OrderBy(kv => kv.Key, StringComparer.Ordinal))
				if (!counts.ContainsKey(type))
					yield return $"{type}: 0 here, {count} there (+{count})";
		}

		public string Describe(string heading, string indent = "  ")
		{
			var sb = new StringBuilder();
			sb.Append(heading).Append(": ").Append(Total).Append(" actor(s), ")
				.Append(counts.Count).Append(" type(s)").AppendLine();

			foreach (var (type, count) in counts.OrderBy(kv => kv.Key, StringComparer.Ordinal))
				sb.Append(indent).Append(type).Append(" x").Append(count)
					.Append("  (lowest id ").Append(lowestId[type]).Append(')').AppendLine();

			return sb.ToString();
		}
	}
}
