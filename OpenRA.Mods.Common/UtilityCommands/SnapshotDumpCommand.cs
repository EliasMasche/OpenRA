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
using System.Linq;
using OpenRA.GameSaves;
using OpenRA.Mods.Common.Widgets.Logic;

namespace OpenRA.Mods.Common.UtilityCommands
{
	public class SnapshotDumpCommand : IUtilityCommand
	{
		string IUtilityCommand.Name => "--dump-snapshot";

		bool IUtilityCommand.ValidateArguments(string[] args)
		{
			return args.Length is >= 2 and <= 4;
		}

		[Desc("FILE", "[TRAIT]", "[SECTION]",
			"Print the header and section list of a snapshot file.",
			"With TRAIT, also print the saved sync rows that name that trait.",
			"With SECTION, print that section's YAML instead. The two go together: a trait that reports",
			"missing state names a section, and the section says whether the state was ever written.")]
		void IUtilityCommand.Run(Utility utility, string[] args)
		{
			ArgumentNullException.ThrowIfNull(utility);

			if (args.Length == 4 && args[2].StartsWith("--", StringComparison.Ordinal))
			{
				Console.WriteLine("Pass the trait and the section positionally, without a leading --.");
				Environment.Exit(1);
			}

			using (var stream = File.OpenRead(args[1]))
			using (var reader = new SnapshotReader(stream))
			{
				var header = reader.Header;

				Console.WriteLine($"Format version: {header.FormatVersion}");
				Console.WriteLine($"Engine:         {header.EngineVersion}");
				Console.WriteLine($"Mod:            {header.ModId} {header.ModVersion}");
				Console.WriteLine($"Map uid:        {header.MapUid}");
				Console.WriteLine($"World tick:     {header.WorldTick}");
				Console.WriteLine($"Sync hash:      {header.SyncHash}");
				Console.WriteLine($"Saved (UTC):    {header.SavedUtc:u}");
				Console.WriteLine($"Flags:          {header.Flags}");

				Console.WriteLine(
					$"Dropped:        {header.DroppedActivities} activit" +
					$"{(header.DroppedActivities == 1 ? "y" : "ies")}, " +
					$"{header.DroppedEffects} effect" +
					$"{(header.DroppedEffects == 1 ? "" : "s")}");

				Console.WriteLine();
				Console.WriteLine("Sections:");

				foreach (var name in reader.SectionNames.OrderBy(n => n, StringComparer.Ordinal))
					Console.WriteLine($"  {name}");

				if (args.Length == 3)
					PrintDiff(reader, args[2]);

				if (args.Length == 4)
					PrintSection(reader, args[3]);
			}

			var info = SaveFileInfo.Read(args[1]);
			if (info == null)
			{
				Console.WriteLine();
				Console.WriteLine("The file cannot be read back as a save file.");
				return;
			}

			Console.WriteLine();
			Console.WriteLine($"Save file:      {info.Format}");
			Console.WriteLine($"Duration:       {GameSaveUtils.FormatGameDuration(info.Duration)}");
		}

		static void PrintSection(SnapshotReader reader, string section)
		{
			using (var s = reader.OpenSection(section))
			{
				Console.WriteLine();
				if (s == null)
				{
					Console.WriteLine($"The snapshot carries no section '{section}'.");
					return;
				}

				Console.WriteLine($"Section '{section}':");
				Console.WriteLine(s.ReadAllText());
			}
		}

		static void PrintDiff(SnapshotReader reader, string trait)
		{
			var diff = SnapshotDiff.Load(reader.ReadYamlSection(WorldRestorer.DiffSection));
			if (diff == null)
			{
				Console.WriteLine();
				Console.WriteLine("The snapshot carries no sync diff, so a mismatch cannot be attributed.");
				Console.WriteLine($"Set {nameof(Game.Settings.Debug.SnapshotDiagnostics)} to write one.");
				return;
			}

			var rows = diff.Entries
				.Where(e => string.Equals(e.Trait, trait, StringComparison.OrdinalIgnoreCase))
				.ToList();

			Console.WriteLine();
			if (rows.Count == 0)
			{
				Console.WriteLine($"No saved sync row names a trait '{trait}'.");
				return;
			}

			Console.WriteLine($"Saved sync rows for '{trait}':");

			foreach (var e in rows)
				Console.WriteLine(
					$"  actor {e.ActorID} ({e.ActorType}, {e.Owner}) " +
					$"instance {e.Instance}: hash {e.Hash}");

			Console.WriteLine();
			Console.WriteLine("A hash a restore cannot reproduce is the one to check: the trait's own save");
			Console.WriteLine("path is the only thing that puts state back after the rules are applied.");
		}
	}
}
