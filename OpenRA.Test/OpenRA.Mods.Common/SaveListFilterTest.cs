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
using System.IO;
using System.Linq;
using NUnit.Framework;
using OpenRA.Mods.Common.Widgets.Logic;

namespace OpenRA.Test
{
	[TestFixture]
	sealed class SaveListFilterTest
	{
		static SaveEntry Entry(
			string path = "save.orasav",
			DateTime? lastWrite = null,
			TimeSpan? duration = null,
			bool isAutosave = false,
			string mapTitle = "Exodus",
			string[] factions = null)
		{
			return new SaveEntry
			{
				Path = path,
				LastWrite = lastWrite ?? DateTime.Now,
				Duration = duration,
				IsAutosave = isAutosave,
				MapTitle = mapTitle,
				Factions = factions ?? ["soviet"]
			};
		}

		[TestCase(TestName = "An untouched filter accepts every save")]
		public void EmptyFilterAcceptsEverything()
		{
			var filter = new Filter();

			Assert.That(filter.IsEmpty, Is.True);
			Assert.That(filter.Matches(Entry()), Is.True);
			Assert.That(filter.Matches(Entry(duration: TimeSpan.FromMinutes(90), isAutosave: true)), Is.True);
		}

		[TestCase(TestName = "The save-type filter separates autosaves from manual saves")]
		public void TypeFilter()
		{
			var autosave = Entry(isAutosave: true);
			var manual = Entry(isAutosave: false);

			Assert.That(new Filter { Type = SaveType.Autosave }.Matches(autosave), Is.True);
			Assert.That(new Filter { Type = SaveType.Autosave }.Matches(manual), Is.False);

			Assert.That(new Filter { Type = SaveType.Manual }.Matches(manual), Is.True);
			Assert.That(new Filter { Type = SaveType.Manual }.Matches(autosave), Is.False);

			Assert.That(new Filter { Type = SaveType.Any }.Matches(autosave), Is.True);
		}

		[TestCase(TestName = "A save with no readable duration is kept by every duration filter")]
		public void UnknownDurationIsKept()
		{
			var unknown = Entry(duration: null);

			foreach (var duration in new[] { DurationType.VeryShort, DurationType.Short, DurationType.Medium, DurationType.Long })
			{
				Assert.That(new Filter { Duration = duration }.Matches(unknown), Is.True,
					$"A save with no duration must not be hidden by the {duration} filter, or a save the player " +
					"can see on disk would be missing from the list.");
			}
		}

		[TestCase(TestName = "A known duration is placed in exactly one band")]
		public void DurationBands()
		{
			var cases = new[]
			{
				(Minutes: 1.0, Expected: DurationType.VeryShort),
				(Minutes: 4.9, Expected: DurationType.VeryShort),
				(Minutes: 5.0, Expected: DurationType.Short),
				(Minutes: 19.9, Expected: DurationType.Short),
				(Minutes: 20.0, Expected: DurationType.Medium),
				(Minutes: 59.9, Expected: DurationType.Medium),
				(Minutes: 60.0, Expected: DurationType.Long),
				(Minutes: 600.0, Expected: DurationType.Long)
			};

			foreach (var (minutes, expected) in cases)
			{
				var entry = Entry(duration: TimeSpan.FromMinutes(minutes));

				foreach (var band in new[] { DurationType.VeryShort, DurationType.Short, DurationType.Medium, DurationType.Long })
				{
					Assert.That(new Filter { Duration = band }.Matches(entry), Is.EqualTo(band == expected),
						$"A save of {minutes} minutes belongs in {expected} and nowhere else.");
				}
			}
		}

		[TestCase(TestName = "The save-name filter matches a part of the name, ignoring case")]
		public void SaveNameIsSubstringAndCaseInsensitive()
		{
			var entry = Entry(path: @"C:\saves\Operation Exodus (2).orasav");

			Assert.That(new Filter { SaveName = "exodus" }.Matches(entry), Is.True);
			Assert.That(new Filter { SaveName = "OPERATION" }.Matches(entry), Is.True);

			Assert.That(new Filter { SaveName = "orasav" }.Matches(entry), Is.False);
			Assert.That(new Filter { SaveName = "nowhere" }.Matches(entry), Is.False);
		}

		[TestCase(TestName = "Filters combine, and every one set must be satisfied")]
		public void FiltersCombine()
		{
			var entry = Entry(duration: TimeSpan.FromMinutes(10), isAutosave: false, mapTitle: "Exodus", factions: ["soviet"]);

			Assert.That(new Filter { Type = SaveType.Manual, MapName = "Exodus" }.Matches(entry), Is.True);

			Assert.That(new Filter { Type = SaveType.Manual, Duration = DurationType.Long }.Matches(entry), Is.False);
			Assert.That(new Filter { Type = SaveType.Autosave, MapName = "Exodus" }.Matches(entry), Is.False);
			Assert.That(new Filter { MapName = "Exodus", Faction = "allies" }.Matches(entry), Is.False);
		}

		[TestCase(TestName = "The map filter compares the whole title, not a part of it")]
		public void MapNameIsExact()
		{
			var entry = Entry(mapTitle: "Exodus");

			Assert.That(new Filter { MapName = "Exodus" }.Matches(entry), Is.True);
			Assert.That(new Filter { MapName = "exodus" }.Matches(entry), Is.True);

			Assert.That(new Filter { MapName = "Exo" }.Matches(entry), Is.False);
			Assert.That(new Filter { MapName = "Exodus II" }.Matches(entry), Is.False);
		}

		[TestCase(TestName = "The faction filter matches any faction the save holds, ignoring case")]
		public void FactionFilter()
		{
			var entry = Entry(factions: ["soviet", "allies"]);

			Assert.That(new Filter { Faction = "Allies" }.Matches(entry), Is.True);
			Assert.That(new Filter { Faction = "soviet" }.Matches(entry), Is.True);
			Assert.That(new Filter { Faction = "germany" }.Matches(entry), Is.False);
		}

		[TestCase(TestName = "The date filter excludes saves older than its window")]
		public void DateFilter()
		{
			var recent = Entry(lastWrite: DateTime.Now - TimeSpan.FromHours(1));
			var old = Entry(lastWrite: DateTime.Now - TimeSpan.FromDays(45));

			Assert.That(new Filter { Date = DateType.Today }.Matches(recent), Is.True);

			foreach (var date in new[] { DateType.Today, DateType.LastWeek, DateType.LastFortnight, DateType.LastMonth })
				Assert.That(new Filter { Date = date }.Matches(old), Is.False, $"{date} must not accept a 45-day-old save.");

			Assert.That(new Filter { Date = DateType.LastMonth }.Matches(recent), Is.True);
		}

		[TestCase(TestName = "IsEmpty is false as soon as any filter is set")]
		public void IsEmptyTracksEveryField()
		{
			Assert.That(new Filter { Type = SaveType.Manual }.IsEmpty, Is.False);
			Assert.That(new Filter { Date = DateType.Today }.IsEmpty, Is.False);
			Assert.That(new Filter { Duration = DurationType.Long }.IsEmpty, Is.False);
			Assert.That(new Filter { SaveName = "x" }.IsEmpty, Is.False);
			Assert.That(new Filter { MapName = "x" }.IsEmpty, Is.False);
			Assert.That(new Filter { Faction = "x" }.IsEmpty, Is.False);
		}
	}

	[TestFixture]
	sealed class SaveListModelTest
	{
		string dir;

		[SetUp]
		public void SetUp()
		{
			dir = Path.Combine(Path.GetTempPath(), "openra-savelist-" + Guid.NewGuid().ToString("N"));
			Directory.CreateDirectory(dir);
		}

		[TearDown]
		public void TearDown()
		{
			if (Directory.Exists(dir))
				Directory.Delete(dir, recursive: true);
		}

		string WriteSave(string name, DateTime lastWrite)
		{
			var path = Path.Combine(dir, name + SaveListModel.Extension);
			File.WriteAllText(path, "not a save");
			File.SetLastWriteTime(path, lastWrite);
			return path;
		}

		SaveListModel Model() => new(dir, _ => "Some Map");

		[TestCase(TestName = "Only save files are listed, newest first")]
		public void ListsSaveFilesNewestFirst()
		{
			WriteSave("older", DateTime.Now - TimeSpan.FromDays(2));
			WriteSave("newer", DateTime.Now - TimeSpan.FromHours(1));

			File.WriteAllText(Path.Combine(dir, "notes.txt"), "ignore me");

			var model = Model();
			model.Reload();

			Assert.That(model.Saves.Select(s => Path.GetFileNameWithoutExtension(s.Path)),
				Is.EqualTo(new[] { "newer", "older" }),
				"The list must show only saves, newest first.");
		}

		[TestCase(TestName = "A save whose contents cannot be read is still listed")]
		public void UnreadableSaveIsStillListed()
		{
			WriteSave("broken", DateTime.Now);

			var model = Model();
			model.Reload();

			Assert.That(model.Saves, Has.Count.EqualTo(1));
			Assert.That(model.Saves[0].Duration, Is.Null);
			Assert.That(model.Saves[0].Factions, Is.Empty);
		}

		[TestCase(TestName = "An autosave is recognised by its name when the file says nothing")]
		public void AutosaveFallsBackToTheName()
		{
			WriteSave("autosave-2026-01-01", DateTime.Now);
			WriteSave("Operation Exodus", DateTime.Now);

			var model = Model();
			model.Reload();

			var autosave = model.Saves.Single(s => Path.GetFileName(s.Path).StartsWith("autosave", StringComparison.OrdinalIgnoreCase));
			var manual = model.Saves.Single(s => !Path.GetFileName(s.Path).StartsWith("autosave", StringComparison.OrdinalIgnoreCase));

			Assert.That(autosave.IsAutosave, Is.True, "A save named autosave-* must be treated as an autosave.");
			Assert.That(manual.IsAutosave, Is.False);
		}

		[TestCase(TestName = "Applying a filter hides the saves it excludes")]
		public void FilterHidesExcludedSaves()
		{
			WriteSave("autosave-1", DateTime.Now);
			WriteSave("Exodus", DateTime.Now);

			var model = Model();
			model.Reload();
			model.Filter.Type = SaveType.Manual;
			model.ApplyFilter();

			var autosave = model.Saves.Single(s => Path.GetFileName(s.Path).StartsWith("autosave", StringComparison.OrdinalIgnoreCase));
			var manual = model.Saves.Single(s => !Path.GetFileName(s.Path).StartsWith("autosave", StringComparison.OrdinalIgnoreCase));

			Assert.That(autosave.Visible, Is.False);
			Assert.That(manual.Visible, Is.True);
		}

		[TestCase(TestName = "A selection the filter hides is moved to the first save it keeps")]
		public void FilterMovesAHiddenSelection()
		{
			WriteSave("autosave-1", DateTime.Now - TimeSpan.FromHours(1));
			WriteSave("Exodus", DateTime.Now - TimeSpan.FromHours(2));

			var model = Model();
			model.Reload();

			var autosave = model.Saves.Single(s => Path.GetFileName(s.Path).StartsWith("autosave", StringComparison.OrdinalIgnoreCase));
			model.SelectSave(autosave.Path);

			model.Filter.Type = SaveType.Manual;
			model.ApplyFilter();

			Assert.That(model.SelectedPath, Is.Not.EqualTo(autosave.Path));
			Assert.That(model.Selected, Is.Not.Null);
			Assert.That(model.Selected.Visible, Is.True);
		}

		[TestCase(TestName = "Deleting a save removes the file and the entry")]
		public void DeleteRemovesFileAndEntry()
		{
			var path = WriteSave("Exodus", DateTime.Now);

			var model = Model();
			model.Reload();
			Assert.That(model.Delete(path), Is.True);

			Assert.That(File.Exists(path), Is.False);
			Assert.That(model.Saves, Is.Empty);
		}

		[TestCase(TestName = "Renaming a save moves the file and follows the selection")]
		public void RenameMovesFileAndSelection()
		{
			var path = WriteSave("Exodus", DateTime.Now);

			var model = Model();
			model.Reload();
			model.SelectSave(path);

			var newPath = model.Rename("Exodus", "Exodus II");

			Assert.That(newPath, Is.Not.Null);
			Assert.That(File.Exists(path), Is.False);
			Assert.That(File.Exists(newPath), Is.True);
			Assert.That(model.Saves.Single().Path, Is.EqualTo(newPath));
			Assert.That(model.SelectedPath, Is.EqualTo(newPath),
				"The selection must follow the file, or the panel would act on a path that no longer exists.");
		}

		[TestCase(TestName = "An empty or missing directory lists nothing rather than failing")]
		public void EmptyDirectoryListsNothing()
		{
			var model = new SaveListModel(Path.Combine(dir, "does-not-exist"), _ => null);
			model.Reload();
			model.ApplyFilter();

			Assert.That(model.Saves, Is.Empty);
			Assert.That(model.SelectedPath, Is.Null);
		}

		[TestCase(TestName = "Resetting the filter clears every field")]
		public void ResetFilterClearsEverything()
		{
			var model = Model();
			model.Filter.Type = SaveType.Autosave;
			model.Filter.MapName = "Exodus";
			model.Filter.SaveName = "x";

			model.ResetFilter();

			Assert.That(model.Filter.IsEmpty, Is.True);
		}
	}
}
