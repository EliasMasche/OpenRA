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
using System.IO;
using System.Linq;

namespace OpenRA.Mods.Common.Widgets.Logic
{
	public enum SaveType
	{
		Any,
		Autosave,
		Manual
	}

	public enum DateType
	{
		Any,
		Today,
		LastWeek,
		LastFortnight,
		LastMonth
	}

	public enum DurationType
	{
		Any,
		VeryShort,
		Short,
		Medium,
		Long
	}

	public sealed class SaveEntry
	{
		public string Path;

		public DateTime LastWrite;

		public DateTime CreationTime;
		public TimeSpan? Duration;
		public bool IsAutosave;
		public string MapTitle;
		public IReadOnlyList<string> Factions = [];
		public bool Visible = true;
	}

	public sealed class Filter
	{
		public SaveType Type;
		public DateType Date;
		public DurationType Duration;
		public string SaveName;
		public string MapName;
		public string Faction;

		public bool IsEmpty =>
			Type == default
			&& Date == default
			&& Duration == default
			&& string.IsNullOrEmpty(SaveName)
			&& string.IsNullOrEmpty(MapName)
			&& string.IsNullOrEmpty(Faction);

		public bool Matches(SaveEntry entry)
		{
			ArgumentNullException.ThrowIfNull(entry);

			if (Type != SaveType.Any)
			{
				if (Type == SaveType.Autosave && !entry.IsAutosave)
					return false;

				if (Type == SaveType.Manual && entry.IsAutosave)
					return false;
			}

			if (Date != DateType.Any)
			{
				TimeSpan t;
				switch (Date)
				{
					case DateType.Today:
						t = TimeSpan.FromDays(1d);
						break;

					case DateType.LastWeek:
						t = TimeSpan.FromDays(7d);
						break;

					case DateType.LastFortnight:
						t = TimeSpan.FromDays(14d);
						break;

					case DateType.LastMonth:
					default:
						t = TimeSpan.FromDays(30d);
						break;
				}

				if (entry.LastWrite < DateTime.Now - t)
					return false;
			}

			if (Duration != DurationType.Any)
			{
				if (!entry.Duration.HasValue)
					return true;

				var minutes = entry.Duration.Value.TotalMinutes;
				switch (Duration)
				{
					case DurationType.VeryShort:
						if (minutes >= 5)
							return false;
						break;

					case DurationType.Short:
						if (minutes < 5 || minutes >= 20)
							return false;
						break;

					case DurationType.Medium:
						if (minutes < 20 || minutes >= 60)
							return false;
						break;

					case DurationType.Long:
						if (minutes < 60)
							return false;
						break;
				}
			}

			if (!string.IsNullOrEmpty(SaveName))
			{
				var saveName = Path.GetFileNameWithoutExtension(entry.Path);
				if (!saveName.Contains(SaveName, StringComparison.OrdinalIgnoreCase))
					return false;
			}

			if (!string.IsNullOrEmpty(MapName) &&
				!string.Equals(MapName, entry.MapTitle, StringComparison.CurrentCultureIgnoreCase))
				return false;

			if (!string.IsNullOrEmpty(Faction) &&
				!entry.Factions.Any(f => string.Equals(Faction, f, StringComparison.CurrentCultureIgnoreCase)))
				return false;

			return true;
		}
	}

	public sealed class SaveListModel
	{
		public const string Extension = ".orasav";

		readonly List<SaveEntry> saves = [];
		readonly Func<string, string> mapTitleForUid;

		public Filter Filter { get; private set; } = new();

		public string BaseSavePath { get; }

		public IReadOnlyList<SaveEntry> Saves => saves;

		public string SelectedPath { get; private set; }

		public event Action SelectionChanged = () => { };

		public event Action<string> DeleteFailed = _ => { };

		public SaveListModel(string baseSavePath, Func<string, string> mapTitleForUid)
		{
			ArgumentNullException.ThrowIfNull(baseSavePath);

			BaseSavePath = baseSavePath;
			this.mapTitleForUid = mapTitleForUid ?? (_ => null);
		}

		public void ResetFilter()
		{
			Filter = new Filter();
		}

		public IReadOnlyList<SaveEntry> Reload()
		{
			saves.Clear();

			if (!Directory.Exists(BaseSavePath))
			{
				SelectedPath = null;
				return saves;
			}

			var savePaths = Directory.GetFiles(BaseSavePath, "*" + Extension)
				.OrderByDescending(File.GetLastWriteTime)
				.ToList();

			foreach (var savePath in savePaths)
			{
				var save = SaveFileInfo.Read(savePath);
				var mapUid = save?.GlobalSettings.Map;

				saves.Add(new SaveEntry
				{
					Path = savePath,
					LastWrite = File.GetLastWriteTime(savePath),
					CreationTime = File.GetCreationTime(savePath),
					Duration = GameSaveUtils.GetGameDuration(save),

					IsAutosave = save?.IsAutosave ?? Path.GetFileNameWithoutExtension(savePath)
						.StartsWith("autosave", StringComparison.OrdinalIgnoreCase),
					MapTitle = mapUid != null ? mapTitleForUid(mapUid) : null,
					Factions = save?.SlotClients.Values
						.Select(sc => sc.Faction)
						.Where(f => !string.IsNullOrEmpty(f))
						.ToList() ?? []
				});
			}

			return saves;
		}

		public void ApplyFilter()
		{
			foreach (var entry in saves)
				entry.Visible = Filter.Matches(entry);

			if (SelectedPath == null)
			{
				SelectFirstVisible();
				return;
			}

			if (saves.All(s => s.Path != SelectedPath || !s.Visible))
			{
				var firstVisible = saves.FirstOrDefault(s => s.Visible);
				if (firstVisible != null)
					SelectSave(firstVisible.Path);
				else
					SelectSave(null);
			}
		}

		public SaveEntry Selected => saves.FirstOrDefault(s => s.Path == SelectedPath);

		public void SelectFirstVisible()
		{
			SelectSave(saves.FirstOrDefault(s => s.Visible)?.Path);
		}

		public void SelectSave(string savePath)
		{
			SelectedPath = savePath;
			SelectionChanged();
		}

		public string Rename(string oldName, string newName)
		{
			var entry = saves.FirstOrDefault(s => s.Path == Path.Combine(BaseSavePath, oldName + Extension));
			if (entry == null)
				return null;

			try
			{
				var oldPath = entry.Path;
				var newPath = Path.Combine(BaseSavePath, newName + Extension);
				File.Move(oldPath, newPath);
				entry.Path = newPath;

				if (SelectedPath == oldPath)
					SelectedPath = newPath;

				return newPath;
			}
			catch (Exception ex)
			{
				Log.Write("debug", ex.ToString());
				return null;
			}
		}

		public bool Delete(string savePath)
		{
			var entry = saves.FirstOrDefault(s => s.Path == savePath);
			if (entry == null)
				return false;

			try
			{
				File.Delete(savePath);
			}
			catch (Exception ex)
			{
				Log.Write("debug", ex.ToString());
			}

			if (File.Exists(savePath))
			{
				DeleteFailed(savePath);
				return false;
			}

			if (SelectedPath == savePath)
				SelectSave(null);

			saves.Remove(entry);
			return true;
		}
	}
}
