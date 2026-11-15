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
using OpenRA.Hosting;
using OpenRA.Snapshot;

namespace OpenRA.Mods.Common.UtilityCommands
{
	sealed class CheckSaveRoundtripCommand : IUtilityCommand
	{
		const string Differs = "Differs";
		const int MaxLineLength = 120;

		string IUtilityCommand.Name => "--check-save-roundtrip";

		bool IUtilityCommand.ValidateArguments(string[] args)
		{
			return args.Length >= 3;
		}

		[Desc("REPLAYFILE SNAPSHOT [SNAPSHOT...]",
			"Load each snapshot of the replay and continue the replay from it without a display. "
			+ "The snapshots have to come from one run of --save-snapshot. "
			+ "Every later sync hash of the recording is compared with this build's simulation. "
			+ "The loaded world has to save the snapshot again, and at the tick of each later snapshot "
			+ "it has to save what the world loaded from that snapshot saves. "
			+ "Exits 1 if a snapshot cannot be loaded, the replay desyncs after it, or the state differs.")]
		void IUtilityCommand.Run(Utility utility, string[] args)
		{
			ReplaySyncChecker.UseMod(utility.ModData);
			var replay = args[1];
			var snapshots = args.Skip(2)
				.Select(file => (File: file, Text: File.Exists(file) ? File.ReadAllText(file) : null))
				.Select(s => (s.File, s.Text, Tick: TickOf(s.Text)))
				.OrderByDescending(s => s.Tick)
				.ToArray();

			// The command compares a world that continued from a snapshot with the worlds that load from the later
			// snapshots. So it loads the last snapshot first, and each load leaves its saves for the earlier ones.
			var ticks = snapshots.Select(s => s.Tick).Where(t => t > 0).ToHashSet();
			var states = new Dictionary<int, string>();
			var verdicts = new List<string>();
			foreach (var (file, text, _) in snapshots)
			{
				var (verdict, line) = text == null
					? (nameof(ReplaySyncOutcome.Error), "the snapshot file does not exist")
					: Continue(replay, text, ticks, states);

				Console.WriteLine($"{verdict,-11} {line}  {file}");
				verdicts.Add(verdict);
			}

			Console.WriteLine(string.Join(", ", verdicts.GroupBy(v => v).Select(g => $"{g.Count()} {g.Key.ToLowerInvariant()}")) + ".");
			if (verdicts.Any(v => v != nameof(ReplaySyncOutcome.Passed)))
				Environment.ExitCode = 1;
		}

		static int TickOf(string snapshot)
		{
			try
			{
				var world = MiniYaml.FromString(snapshot ?? "", "snapshot").FirstOrDefault(n => n.Key == SnapshotFormat.Sections.World);
				var tick = world?.Value.NodeWithKeyOrDefault(SnapshotFormat.Keys.Tick)?.Value.Value;
				return Exts.TryParseInt32Invariant(tick, out var parsed) ? parsed : 0;
			}
			catch (Exception)
			{
				return 0;
			}
		}

		// The sync hash covers little of a snapshot, so the command also compares the text that each world saves. Right
		// after the load, that text is the snapshot. Later it leaves out cosmetic state, on which two worlds never
		// agree. A save fills the sets and dictionaries of its world again. So two worlds give the same text only when
		// they saved at the same ticks. This world saves at the tick of each later snapshot, as the recording world
		// did, also when that snapshot did not load.
		static (string Verdict, string Line) Continue(string replay, string snapshot, HashSet<int> ticks, Dictionary<int, string> states)
		{
			var loadedAt = -1;
			var compared = new List<int>();
			string difference = null;

			// The command loads the snapshots from the last one back, so this world meets states of later ticks.
			var result = ReplaySyncChecker.Check(replay, world =>
			{
				if (difference == null && world.WorldTick > loadedAt && ticks.Contains(world.WorldTick))
				{
					var actual = State(world);
					if (states.TryGetValue(world.WorldTick, out var expected))
					{
						compared.Add(world.WorldTick);
						if (actual != expected)
							difference = $"the state differs at tick {world.WorldTick}, {FirstDifference(expected, actual, "the world loaded there")}";
					}
				}

				return true;
			}, StartupActors.FromSnapshot, world =>
			{
				SnapshotReader.Load(world, snapshot);
				loadedAt = world.WorldTick;
				var saved = SnapshotWriter.Write(world).WriteToString();
				if (saved != snapshot)
					difference = $"the loaded world saves another snapshot, {FirstDifference(snapshot, saved, "the snapshot")}";

				states[loadedAt] = State(world);
			});

			var from = loadedAt >= 0 ? $"from tick {loadedAt}" : "not loaded";
			var line = $"frame {result.NetFrame}/{result.RecordedFrames}  {from}";
			if (result.Outcome != ReplaySyncOutcome.Passed)
				return (result.Outcome.ToString(), line + (result.Reason != null ? $"  ({result.Reason})" : ""));

			if (difference != null)
				return (Differs, $"{line}  ({difference})");

			var equalAt = compared.Count > 0 ? $", and the state is equal at each of {compared.Count} later snapshots up to tick {compared[^1]}" : "";
			return (nameof(ReplaySyncOutcome.Passed), $"{line}, the loaded world saves the snapshot again{equalAt}");
		}

		static string State(World world)
		{
			return SnapshotWriter.Write(world, withoutCosmeticState: true).WriteToString();
		}

		static string FirstDifference(string expected, string actual, string expectedIs)
		{
			var a = expected.Split('\n');
			var b = actual.Split('\n');
			var line = 0;
			while (line < a.Length && line < b.Length && a[line] == b[line])
				line++;

			var parents = new List<string>();
			var depth = line < a.Length ? Depth(a[line]) : 0;
			for (var i = line - 1; i >= 0 && depth > 0; i--)
			{
				if (Depth(a[i]) >= depth)
					continue;

				parents.Insert(0, Shorten(a[i]));
				depth = Depth(a[i]);
			}

			var first = line < a.Length ? Shorten(a[line]) : "(no line)";
			var second = line < b.Length ? Shorten(b[line]) : "(no line)";
			return $"line {line + 1} under {string.Join(" > ", parents)}: \"{first}\" in {expectedIs}, \"{second}\" in this world";
		}

		static int Depth(string line)
		{
			return line.Length - line.TrimStart('\t').Length;
		}

		static string Shorten(string line)
		{
			var text = line.Trim();
			return text.Length > MaxLineLength ? text[..MaxLineLength] + "..." : text;
		}
	}
}
