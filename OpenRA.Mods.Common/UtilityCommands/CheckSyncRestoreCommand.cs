#region Copyright & License Information
/*
 * Copyright (c) The OpenRA Developers and Contributors
 * This file is part of OpenRA, which is free software. It is made
 * available to you under the terms of the GNU General Public License
 * as published by the Free Software Foundation, either version 3 of
 * the License, or (at your option) any later version. For more information,
 * see COPYING.
 */
#endregion

using System;
using System.Collections.Generic;
using System.Linq;
using OpenRA.GameSaves;

namespace OpenRA.Mods.Common.UtilityCommands
{
	public class CheckSyncRestoreCommand : IUtilityCommand
	{
		string IUtilityCommand.Name => "--check-sync-restore";

		bool IUtilityCommand.ValidateArguments(string[] args)
		{
			return args.Length == 1;
		}

		[Desc("Check that traits whose synced state feeds the sync hash can restore it.")]
		void IUtilityCommand.Run(Utility utility, string[] args)
		{
			ArgumentNullException.ThrowIfNull(utility);

			var reported = new List<string>();
			var abstained = new List<string>();

			foreach (var type in SyncedTypes(utility.ModData.ObjectCreator))
			{
				var members = SaveStateAudit.UnsavedSyncedMembers(type).ToList();
				if (members.Count == 0)
					continue;

				var line = $"{type.FullName}: {members.JoinWith(", ")}";
				if (SaveStateAudit.Abstentions.TryGetValue(type.Name, out var reason))
					abstained.Add($"{line} — {reason}");
				else
					reported.Add(line);
			}

			if (abstained.Count > 0)
			{
				Console.WriteLine($"Unsaved by design ({abstained.Count.ToStringInvariant()}):");
				foreach (var line in abstained)
					Console.WriteLine("  " + line);
			}

			if (reported.Count == 0)
			{
				Console.WriteLine("Synced types that cannot restore their state: none.");
				return;
			}

			var originalColor = Console.ForegroundColor;
			Console.ForegroundColor = ConsoleColor.Red;
			Console.WriteLine(
				$"Synced types whose state is hashed but never saved ({reported.Count.ToStringInvariant()}):");

			foreach (var line in reported)
				Console.WriteLine("  " + line);

			Console.ForegroundColor = originalColor;
			Console.WriteLine(
				"Each restores to its constructor default, so the restored world hashes differently " +
				"whenever the field held anything else. Implement ISaveState, or add an abstention " +
				"with the reason it cannot diverge.");

			Environment.Exit(1);
		}

		static IEnumerable<Type> SyncedTypes(ObjectCreator objectCreator)
		{
			return objectCreator.GetTypes()
				.Where(t => !t.IsAbstract && typeof(ISync).IsAssignableFrom(t))
				.OrderBy(t => t.FullName, StringComparer.Ordinal);
		}
	}
}
