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
using OpenRA.Hosting;

namespace OpenRA.UtilityCommands
{
	sealed class CheckReplaySyncCommand : IUtilityCommand
	{
		string IUtilityCommand.Name => "--check-replay-sync";

		bool IUtilityCommand.ValidateArguments(string[] args)
		{
			return args.Length >= 2;
		}

		[Desc("REPLAYFILE [REPLAYFILE...]",
			"Replay each recording without a display and compare every sync hash with this build's simulation. Exits 1 on any desync.")]
		void IUtilityCommand.Run(Utility utility, string[] args)
		{
			ReplaySyncChecker.UseMod(utility.ModData);
			var outcomes = new List<ReplaySyncOutcome>();
			foreach (var replay in args.Skip(1))
			{
				var result = ReplaySyncChecker.Check(replay);
				var reason = result.Reason != null ? $"  ({result.Reason})" : "";
				Console.WriteLine($"{result.Outcome,-11} frame {result.NetFrame}/{result.RecordedFrames}  {replay}{reason}");
				outcomes.Add(result.Outcome);
			}

			var counts = outcomes.GroupBy(o => o).OrderBy(g => g.Key).Select(g => $"{g.Count()} {g.Key.ToString().ToLowerInvariant()}");
			Console.WriteLine(string.Join(", ", counts) + ".");
			if (outcomes.Any(o => o != ReplaySyncOutcome.Passed))
				Environment.ExitCode = 1;
		}
	}
}
