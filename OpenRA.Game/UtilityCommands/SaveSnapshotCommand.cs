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

namespace OpenRA.UtilityCommands
{
	sealed class SaveSnapshotCommand : IUtilityCommand
	{
		string IUtilityCommand.Name => "--save-snapshot";

		bool IUtilityCommand.ValidateArguments(string[] args)
		{
			return args.Length >= 4 && args.Length % 2 == 0 && Ticks(args).All(t => t > 0) && Ticks(args).Distinct().Count() == args.Length / 2 - 1;
		}

		static IEnumerable<int> Ticks(string[] args)
		{
			for (var i = 2; i < args.Length; i += 2)
				yield return Exts.TryParseInt32Invariant(args[i], out var tick) ? tick : 0;
		}

		[Desc("REPLAYFILE TICK OUTFILE [TICK OUTFILE...]",
			"Replay a recording without a display and write its simulation state at each world tick TICK as a snapshot. "
			+ "An OUTFILE that ends in .yaml gets the text of the snapshot, and any other the bytes of a save. "
			+ "The snapshots of one run are of one world, so a later one is what an earlier one has to become. "
			+ "Exits 1 if the replay ends, desyncs or cannot be written before the last tick.")]
		void IUtilityCommand.Run(Utility utility, string[] args)
		{
			ReplaySyncChecker.UseMod(utility.ModData);
			var replay = args[1];
			var outputs = Ticks(args).Select((tick, i) => (tick, File: args[3 + i * 2])).ToDictionary(o => o.tick, o => o.File);
			var last = outputs.Keys.Max();
			var written = new HashSet<int>();

			// A refusal is thrown from inside the replay, which reports it as an error with its message.
			var result = ReplaySyncChecker.Check(replay, world =>
			{
				// A paused world stays on its tick, and this runs again on each frame.
				if (outputs.TryGetValue(world.WorldTick, out var output) && written.Add(world.WorldTick))
				{
					var snapshot = SnapshotWriter.Write(world);
					if (output.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase))
						File.WriteAllText(output, snapshot.WriteToString());
					else
						File.WriteAllBytes(output, SnapshotFile.Write(snapshot, world));

					Console.WriteLine($"Wrote tick {world.WorldTick} of {replay} to {output} ({new FileInfo(output).Length} bytes).");
				}

				return world.WorldTick < last;
			});

			if (written.Count == outputs.Count && result.Outcome is ReplaySyncOutcome.Stopped or ReplaySyncOutcome.Passed)
				return;

			var reason = result.Reason != null ? $" ({result.Reason})" : "";
			var missing = outputs.Keys.Except(written).Order().ToArray();
			var consequence = missing.Length > 0
				? $"no snapshot of tick {string.Join(", ", missing)} written"
				: "the last snapshot is of a world that the replay does not vouch for";

			Console.WriteLine($"{result.Outcome} at frame {result.NetFrame}/{result.RecordedFrames}{reason}: {consequence}.");
			Environment.ExitCode = 1;
		}
	}
}
