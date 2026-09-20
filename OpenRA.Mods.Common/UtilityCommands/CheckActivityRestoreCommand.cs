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
using System.Reflection;
using OpenRA.Activities;
using OpenRA.GameSaves;

namespace OpenRA.Mods.Common.UtilityCommands
{
	public class CheckActivityRestoreCommand : IUtilityCommand
	{
		string IUtilityCommand.Name => "--check-activity-restore";

		bool IUtilityCommand.ValidateArguments(string[] args)
		{
			return args.Length == 1;
		}

		int violationCount;

		[Desc("Check that every saveable activity can be restored from a game save.")]
		void IUtilityCommand.Run(Utility utility, string[] args)
		{
			var objectCreator = utility.ModData.ObjectCreator;
			var names = new Dictionary<string, Type>();

			foreach (var type in ActivityRegistry.ActivityTypes(objectCreator))
			{
				var saveable = type.IsDefined(typeof(SaveableActivityAttribute), false);
				var restoreCtor = ActivityRegistry.RestoreCtor(type);
				var savesState = SavesState(type);

				if (saveable)
				{
					if (restoreCtor == null)
						OnViolation(type, "is marked [SaveableActivity] but declares no restore constructor " +
							"(Actor, SnapshotReader, MiniYaml).");

					if (!savesState)
						OnViolation(type, "is marked [SaveableActivity] but does not override SaveState.");

					var name = ActivityRegistry.NameOf(type);
					if (names.TryGetValue(name, out var other))
						OnViolation(type, $"saves under the same name '{name}' as {other.FullName}.");
					else
						names.Add(name, type);
				}
				else
				{
					if (savesState)
						OnViolation(type, "overrides SaveState but is not marked [SaveableActivity], so it would " +
							"never be saved.");

					if (restoreCtor != null)
						OnViolation(type, "declares a restore constructor but is not marked [SaveableActivity], so it " +
							"would never be restored.");
				}
			}

			if (violationCount == 0)
			{
				try
				{
					var registry = new ActivityRegistry(objectCreator);
					var saveable = ActivityRegistry.SaveableActivityTypes(objectCreator).Count(registry.IsSaveable);
					Console.WriteLine($"Saveable activities: {saveable}");
				}
				catch (InvalidOperationException e)
				{
					Console.WriteLine(e.Message);
					violationCount++;
				}
			}

			ReportUnsaveable(objectCreator);

			if (violationCount > 0)
			{
				Console.WriteLine($"Activity restore violations: {violationCount}");
				Environment.Exit(1);
			}
		}

		static bool SavesState(Type type)
		{
			var method = type.GetMethod(nameof(Activity.SaveState), [typeof(Actor), typeof(SnapshotWriter)]);
			return method != null && method.DeclaringType != typeof(Activity);
		}

		static void ReportUnsaveable(ObjectCreator objectCreator)
		{
			var unsaveable = new List<string>();
			foreach (var type in ActivityRegistry.ActivityTypes(objectCreator))
			{
				if (type.IsDefined(typeof(SaveableActivityAttribute), false))
					continue;

				var fields = type
					.GetFields(BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
					.Where(f => typeof(Delegate).IsAssignableFrom(f.FieldType))
					.Select(f => f.Name)
					.ToList();

				if (fields.Count > 0)
					unsaveable.Add($"{type.FullName}: {fields.JoinWith(", ")}");
			}

			if (unsaveable.Count > 0)
			{
				Console.WriteLine($"Activities holding a delegate, which cannot be saved ({unsaveable.Count}):");
				foreach (var line in unsaveable)
					Console.WriteLine("  " + line);
			}
		}

		void OnViolation(Type type, string message)
		{
			var originalColor = Console.ForegroundColor;
			Console.ForegroundColor = ConsoleColor.Red;
			Console.WriteLine($"{type.FullName} {message}");
			Console.ForegroundColor = originalColor;
			violationCount++;
		}
	}
}
