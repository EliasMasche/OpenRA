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
using System.IO;
using OpenRA.Network;
using OpenRA.Snapshot;

namespace OpenRA.UtilityCommands
{
	sealed class DumpSnapshotCommand : IUtilityCommand
	{
		string IUtilityCommand.Name => "--dump-snapshot";

		bool IUtilityCommand.ValidateArguments(string[] args)
		{
			return args.Length is 2 or 3;
		}

		[Desc("FILE [OUTFILE]",
			"Write the snapshot of a save (.orasav) or of a snapshot file as text, to OUTFILE or to the console. "
			+ "The header comes first. Exits 1 if the file is refused, with the reason.")]
		void IUtilityCommand.Run(Utility utility, string[] args)
		{
			try
			{
				var bytes = args[1].EndsWith(".orasav", StringComparison.OrdinalIgnoreCase)
					? new GameSave(args[1]).ReadSnapshot()
					: File.ReadAllBytes(args[1]);

				var file = SnapshotFile.Read(bytes);
				var text = $"# mod {file.Mod}, map {file.Map}, tick {file.Tick}, sync hash {file.SyncHash}, {bytes.Length} bytes\n"
					+ file.Nodes().WriteToString();

				if (args.Length == 3)
					File.WriteAllText(args[2], text);
				else
					Console.Write(text);
			}
			catch (Exception e) when (e is SnapshotLoadException or IOException or InvalidDataException)
			{
				Console.WriteLine($"{args[1]}: {e.Message}");
				Environment.ExitCode = 1;
			}
		}
	}
}
