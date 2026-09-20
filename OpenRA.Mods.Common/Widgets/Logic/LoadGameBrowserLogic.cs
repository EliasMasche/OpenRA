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
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using OpenRA.GameSaves;
using OpenRA.Network;
using OpenRA.Primitives;
using OpenRA.Traits;
using OpenRA.Widgets;

namespace OpenRA.Mods.Common.Widgets.Logic
{
	[IncludeStaticFluentReferences(typeof(GameSaveUtils))]
	public class LoadGameBrowserLogic : ChromeLogic
	{
		[FluentReference]
		const string RenameSaveTitle = "dialog-rename-save.title";

		[FluentReference]
		const string RenameSavePrompt = "dialog-rename-save.prompt";

		[FluentReference]
		const string RenameSaveAccept = "dialog-rename-save.confirm";

		[FluentReference]
		const string DeleteSaveTitle = "dialog-delete-save.title";

		[FluentReference("save")]
		const string DeleteSavePrompt = "dialog-delete-save.prompt";

		[FluentReference]
		const string DeleteSaveAccept = "dialog-delete-save.confirm";

		[FluentReference]
		const string DeleteAllSavesTitle = "dialog-delete-all-saves.title";

		[FluentReference("count")]
		const string DeleteAllSavesPrompt = "dialog-delete-all-saves.prompt";

		[FluentReference]
		const string DeleteAllSavesAccept = "dialog-delete-all-saves.confirm";

		[FluentReference("savePath")]
		const string SaveDeletionFailed = "notification-save-deletion-failed";

		[FluentReference]
		const string Players = "label-players";

		[FluentReference("team")]
		const string TeamNumber = "label-team-name";

		[FluentReference]
		const string NoTeam = "label-no-team";

		[FluentReference]
		const string SaveTypeAutosave = "options-save-type.autosave";

		[FluentReference]
		const string SaveTypeManual = "options-save-type.manual";

		[FluentReference]
		const string Today = "options-replay-date.today";

		[FluentReference]
		const string LastWeek = "options-replay-date.last-week";

		[FluentReference]
		const string LastFortnight = "options-replay-date.last-fortnight";

		[FluentReference]
		const string LastMonth = "options-replay-date.last-month";

		[FluentReference]
		const string SaveDurationVeryShort = "options-replay-duration.very-short";

		[FluentReference]
		const string SaveDurationShort = "options-replay-duration.short";

		[FluentReference]
		const string SaveDurationMedium = "options-replay-duration.medium";

		[FluentReference]
		const string SaveDurationLong = "options-replay-duration.long";

		[FluentReference("name", "number")]
		const string EnumeratedBotName = "enumerated-bot-name";

		[FluentReference]
		const string HumanPlayer = "label-load-game-browser-panel-human-player";

		[FluentReference]
		const string CannotLoadDifferentMap = "notification-cannot-load-different-map";

		[FluentReference]
		const string CannotLoadMapUnavailable = "notification-cannot-load-map-unavailable";

		readonly Widget panel;
		readonly ScrollPanelWidget gameList;
		readonly ScrollPanelWidget playerList;
		readonly ScrollItemWidget playerTemplate;
		readonly ScrollItemWidget playerHeader;
		readonly ScrollItemWidget gameTemplate;
		readonly ScrollItemWidget dateHeaderTemplate;
		readonly Action onStart;
		readonly ModData modData;

		readonly SaveListModel saveList;

		readonly Dictionary<string, ScrollItemWidget> saveItems = [];

		MapPreview map;
		SaveFileInfo selectedSave;
		bool filtersVisible;

		readonly string sessionMapUid;

		readonly Action<string, string> loadAction;

		[ObjectCreator.UseCtor]
		public LoadGameBrowserLogic(Widget widget, ModData modData, Action onExit, Action onStart,
			Action<string, string> loadAction, string sessionMapUid = null)
		{
			this.loadAction = loadAction;
			this.sessionMapUid = sessionMapUid;

			// Reset filters to their neutral state every time the panel opens.
			map = MapCache.UnknownMap;
			panel = widget;

			this.modData = modData;
			this.onStart = onStart;
			Game.BeforeGameStart += OnGameStart;

			saveList = new SaveListModel(SavePaths.BaseSaveDirectory(modData.Manifest),
				uid => modData.MapCache[uid].Title);

			saveList.SelectionChanged += OnSelectionChanged;

			saveList.DeleteFailed += savePath =>
				TextNotificationsManager.Debug(FluentProvider.GetMessage(SaveDeletionFailed, "savePath", savePath));

			panel.Get<ButtonWidget>("CANCEL_BUTTON").OnClick = () =>
			{
				Ui.CloseWindow();
				onExit();
			};

			playerList = panel.Get<ScrollPanelWidget>("PLAYER_LIST");
			playerHeader = playerList.Get<ScrollItemWidget>("HEADER");
			playerTemplate = playerList.Get<ScrollItemWidget>("TEMPLATE");
			playerList.RemoveChildren();

			var loadButton = panel.Get<ButtonWidget>("LOAD_BUTTON");
			loadButton.IsDisabled = () => selectedSave == null || modData.MapCache[selectedSave.GlobalSettings.Map].Status != MapStatus.Available;
			loadButton.OnClick = Load;

			var mapPreviewRoot = panel.Get("MAP_PREVIEW_ROOT");
			mapPreviewRoot.IsVisible = () => saveList.SelectedPath != null;
			var saveInfo = panel.Get("SAVE_INFO");
			saveInfo.IsVisible = () => saveList.SelectedPath != null;

			var incompatibleTitleLabel = saveInfo.Get<LabelWidget>("INCOMPATIBLE_TITLE_LABEL");
			incompatibleTitleLabel.IsVisible = () => saveList.SelectedPath != null && selectedSave == null;

			var incompatibleLabelA = saveInfo.Get<LabelWidget>("INCOMPATIBLE_LABEL_A");
			incompatibleLabelA.IsVisible = () => saveList.SelectedPath != null && selectedSave == null;

			var incompatibleLabelB = saveInfo.Get<LabelWidget>("INCOMPATIBLE_LABEL_B");
			incompatibleLabelB.IsVisible = () => saveList.SelectedPath != null && selectedSave == null;

			var savegameInfoDate = saveInfo.GetOrNull<LabelWidget>("SAVEGAME_INFO_DATE");
			if (savegameInfoDate != null)
			{
				savegameInfoDate.GetText = () => selectedSave != null && saveList.SelectedPath != null
					? "Date created: " + File.GetCreationTime(saveList.SelectedPath).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
					: string.Empty;
				savegameInfoDate.IsVisible = () => selectedSave != null;
			}

			var savegameInfoDuration = saveInfo.GetOrNull<LabelWidget>("SAVEGAME_INFO_DURATION");
			if (savegameInfoDuration != null)
			{
				savegameInfoDuration.GetText = () => "Duration: " + GameSaveUtils.FormatGameDuration(GameSaveUtils.GetGameDuration(selectedSave));
				savegameInfoDuration.IsVisible = () => selectedSave != null;
			}

			var playerListWidget = saveInfo.Get<ScrollPanelWidget>("PLAYER_LIST");
			playerListWidget.IsVisible = () => saveList.SelectedPath != null;

			var spawnOccupants = new CachedTransform<SaveFileInfo, Dictionary<int, SpawnOccupant>>(_ => GetSpawnOccupants());

			Ui.LoadWidget("MAP_PREVIEW", mapPreviewRoot, new WidgetArgs
			{
				{ "orderManager", null },
				{ "getMap", (Func<(MapPreview, Session.MapStatus)>)(() => (map, Session.MapStatus.Playable)) },
				{ "onMouseDown", null },
				{ "getSpawnOccupants", (Func<Dictionary<int, SpawnOccupant>>)(() => spawnOccupants.Update(selectedSave)) },
				{ "getDisabledSpawnPoints", () => FrozenSet<int>.Empty },
				{ "showUnoccupiedSpawnpoints", false },
				{ "mapUpdatesEnabled", false },
				{ "onMapUpdate", (Action<string>)(_ => { }) },
			});

			gameList = panel.Get<ScrollPanelWidget>("GAME_LIST");
			gameTemplate = panel.Get<ScrollItemWidget>("GAME_TEMPLATE");
			dateHeaderTemplate = panel.Get<ScrollItemWidget>("DATE_HEADER");

			SetupFilters();
			SetupManagement(onExit);

			LoadGames();

			SetupSaveDependentFilters();
			saveList.ApplyFilter();
		}

		void LoadGames()
		{
			gameList.RemoveChildren();
			saveItems.Clear();

			saveList.Reload();

			var byDate = saveList.Saves.GroupBy(s => s.LastWrite.Date);

			foreach (var group in byDate)
			{
				var dateLabel = group.Key.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
				var header = ScrollItemWidget.Setup(dateHeaderTemplate, () => false, () => { });
				header.Get<LabelWidget>("LABEL").GetText = () => dateLabel;

				// The header is visible only if at least one save in this group is visible.
				// Assigned after all entries in the group are created.
				var groupEntries = new List<SaveEntry>();
				header.IsVisible = () => groupEntries.Any(e => e.Visible);
				gameList.AddChild(header);

				foreach (var entry in group)
				{
					var savePath = entry.Path;
					var save = SaveFileInfo.Read(savePath);
					groupEntries.Add(entry);

					var item = gameTemplate.Clone();
					item.ItemKey = savePath;
					item.IsSelected = () => saveList.SelectedPath == item.ItemKey;
					item.OnClick = () => saveList.SelectSave(item.ItemKey);
					item.OnDoubleClick = Load;

					var title = Path.GetFileNameWithoutExtension(savePath);
					var label = item.Get<LabelWithTooltipWidget>("TITLE");
					WidgetUtils.TruncateLabelToTooltip(label, title);
					var tooltipText = GameSaveUtils.BuildSaveTooltipText(savePath, save, modData);
					label.GetTooltipText = () => tooltipText;

					var creationTimeLabel = item.GetOrNull<LabelWidget>("CREATION_TIME");
					if (creationTimeLabel != null)
					{
						creationTimeLabel.GetText = () => entry.CreationTime.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
						creationTimeLabel.IsVisible = () => item.IsSelected();
					}

					saveItems[savePath] = item;
					item.IsVisible = () => entry.Visible;

					gameList.AddChild(item);
				}
			}
		}

		void SetupFilters()
		{
			saveList.ResetFilter();

			TextFieldWidget nameInput = null;

			// Save name
			{
				nameInput = panel.GetOrNull<TextFieldWidget>("FLT_NAME_INPUT");
				if (nameInput != null)
				{
					nameInput.Text = saveList.Filter.SaveName ?? string.Empty;
					nameInput.OnEscKey = _ =>
					{
						saveList.Filter.SaveName = nameInput.Text = null;
						saveList.ApplyFilter();
						return true;
					};
					nameInput.OnTextEdited = () =>
					{
						saveList.Filter.SaveName = string.IsNullOrEmpty(nameInput.Text) ? null : nameInput.Text;
						saveList.ApplyFilter();
					};
				}
			}

			// Save type
			{
				var ddb = panel.GetOrNull<DropDownButtonWidget>("FLT_TYPE_DROPDOWNBUTTON");
				if (ddb != null)
				{
					(SaveType SaveType, string Text)[] options =
					[
						(SaveType.Any, ddb.GetText()),
						(SaveType.Autosave, FluentProvider.GetMessage(SaveTypeAutosave)),
						(SaveType.Manual, FluentProvider.GetMessage(SaveTypeManual))
					];

					var lookup = options.ToFrozenDictionary(kvp => kvp.SaveType, kvp => kvp.Text);

					ddb.GetText = () => lookup[saveList.Filter.Type];
					ddb.OnMouseDown = _ =>
					{
						ScrollItemWidget SetupItem((SaveType SaveType, string Text) option, ScrollItemWidget tpl)
						{
							var item = ScrollItemWidget.Setup(
								tpl,
								() => saveList.Filter.Type == option.SaveType,
								() => { saveList.Filter.Type = option.SaveType; saveList.ApplyFilter(); });
							item.Get<LabelWidget>("LABEL").GetText = () => option.Text;
							return item;
						}

						ddb.ShowDropDown("LABEL_DROPDOWN_TEMPLATE", 330, options, SetupItem);
					};
				}
			}

			// Date
			{
				var ddb = panel.GetOrNull<DropDownButtonWidget>("FLT_DATE_DROPDOWNBUTTON");
				if (ddb != null)
				{
					(DateType DateType, string Text)[] options =
					[
						(DateType.Any, ddb.GetText()),
						(DateType.Today, FluentProvider.GetMessage(Today)),
						(DateType.LastWeek, FluentProvider.GetMessage(LastWeek)),
						(DateType.LastFortnight, FluentProvider.GetMessage(LastFortnight)),
						(DateType.LastMonth, FluentProvider.GetMessage(LastMonth))
					];

					var lookup = options.ToFrozenDictionary(kvp => kvp.DateType, kvp => kvp.Text);

					ddb.GetText = () => lookup[saveList.Filter.Date];
					ddb.OnMouseDown = _ =>
					{
						ScrollItemWidget SetupItem((DateType DateType, string Text) option, ScrollItemWidget tpl)
						{
							var item = ScrollItemWidget.Setup(
								tpl,
								() => saveList.Filter.Date == option.DateType,
								() => { saveList.Filter.Date = option.DateType; saveList.ApplyFilter(); });
							item.Get<LabelWidget>("LABEL").GetText = () => option.Text;
							return item;
						}

						ddb.ShowDropDown("LABEL_DROPDOWN_TEMPLATE", 330, options, SetupItem);
					};
				}
			}

			// Duration
			{
				var ddb = panel.GetOrNull<DropDownButtonWidget>("FLT_DURATION_DROPDOWNBUTTON");
				if (ddb != null)
				{
					(DurationType DurationType, string Text)[] options =
					[
						(DurationType.Any, ddb.GetText()),
						(DurationType.VeryShort, FluentProvider.GetMessage(SaveDurationVeryShort)),
						(DurationType.Short, FluentProvider.GetMessage(SaveDurationShort)),
						(DurationType.Medium, FluentProvider.GetMessage(SaveDurationMedium)),
						(DurationType.Long, FluentProvider.GetMessage(SaveDurationLong))
					];

					var lookup = options.ToFrozenDictionary(kvp => kvp.DurationType, kvp => kvp.Text);

					ddb.GetText = () => lookup[saveList.Filter.Duration];
					ddb.OnMouseDown = _ =>
					{
						ScrollItemWidget SetupItem((DurationType DurationType, string Text) option, ScrollItemWidget tpl)
						{
							var item = ScrollItemWidget.Setup(
								tpl,
								() => saveList.Filter.Duration == option.DurationType,
								() => { saveList.Filter.Duration = option.DurationType; saveList.ApplyFilter(); });
							item.Get<LabelWidget>("LABEL").GetText = () => option.Text;
							return item;
						}

						ddb.ShowDropDown("LABEL_DROPDOWN_TEMPLATE", 330, options, SetupItem);
					};
				}
			}

			// Reset
			{
				var button = panel.Get<ButtonWidget>("FLT_RESET_BUTTON");
				button.IsDisabled = () => saveList.Filter.IsEmpty;
				button.OnClick = () =>
				{
					saveList.ResetFilter();
					if (nameInput != null)
						nameInput.Text = string.Empty;
					SetupSaveDependentFilters();
					saveList.ApplyFilter();
				};
			}
		}

		void SetupSaveDependentFilters()
		{
			// Map name
			{
				var ddb = panel.GetOrNull<DropDownButtonWidget>("FLT_MAPNAME_DROPDOWNBUTTON");
				if (ddb != null)
				{
					var mapNames = saveList.Saves
						.Select(s => s.MapTitle)
						.Where(t => t != null)
						.Distinct(StringComparer.OrdinalIgnoreCase)
						.OrderBy(t => t, StringComparer.OrdinalIgnoreCase)
						.ToList();

					mapNames.Insert(0, null);

					var anyText = ddb.GetText();
					ddb.GetText = () => string.IsNullOrEmpty(saveList.Filter.MapName) ? anyText : saveList.Filter.MapName;
					ddb.OnMouseDown = _ =>
					{
						ScrollItemWidget SetupItem(string option, ScrollItemWidget tpl)
						{
							var item = ScrollItemWidget.Setup(
								tpl,
								() => string.Equals(saveList.Filter.MapName, option, StringComparison.CurrentCultureIgnoreCase),
								() => { saveList.Filter.MapName = option; saveList.ApplyFilter(); });
							item.Get<LabelWidget>("LABEL").GetText = () => option ?? anyText;
							return item;
						}

						ddb.ShowDropDown("LABEL_DROPDOWN_TEMPLATE", 330, mapNames, SetupItem);
					};
				}
			}

			// Faction
			{
				var ddb = panel.GetOrNull<DropDownButtonWidget>("FLT_FACTION_DROPDOWNBUTTON");
				if (ddb != null)
				{
					var factionInfo = modData.DefaultRules.Actors[SystemActors.World].TraitInfos<FactionInfo>();
					var factionDisplayNames = factionInfo.ToFrozenDictionary(
						f => f.InternalName,
						f => FluentProvider.GetMessage(f.Name),
						StringComparer.OrdinalIgnoreCase);

					string ResolveFactionName(string internalName) =>
						factionDisplayNames.GetValueOrDefault(internalName, internalName);

					var factions = saveList.Saves
						.SelectMany(s => s.Factions)
						.Distinct(StringComparer.OrdinalIgnoreCase)
						.OrderBy(ResolveFactionName, StringComparer.CurrentCultureIgnoreCase)
						.ToList();

					factions.Insert(0, null);

					var anyText = ddb.GetText();
					ddb.GetText = () => string.IsNullOrEmpty(saveList.Filter.Faction) ? anyText : ResolveFactionName(saveList.Filter.Faction);
					ddb.OnMouseDown = _ =>
					{
						ScrollItemWidget SetupItem(string option, ScrollItemWidget tpl)
						{
							var item = ScrollItemWidget.Setup(
								tpl,
								() => string.Equals(saveList.Filter.Faction, option, StringComparison.CurrentCultureIgnoreCase),
								() => { saveList.Filter.Faction = option; saveList.ApplyFilter(); });
							var label = option != null ? ResolveFactionName(option) : anyText;
							item.Get<LabelWidget>("LABEL").GetText = () => label;
							return item;
						}

						ddb.ShowDropDown("LABEL_DROPDOWN_TEMPLATE", 330, factions, SetupItem);
					};
				}
			}
		}

		void ApplyFilter()
		{
			saveList.ApplyFilter();

			gameList.Layout.AdjustChildren();
			gameList.ScrollToSelectedItem();

			OnSelectionChanged();
		}

		void SetupFiltersToggle()
		{
			var filtersToggle = panel.GetOrNull<CheckboxWidget>("FILTERS_TOGGLE");
			if (filtersToggle == null)
				return;

			var filterContainer = panel.GetOrNull("FILTER_AND_MANAGE_CONTAINER") ?? panel.GetOrNull("FILTERS");
			var saveListContainer = panel.GetOrNull("SAVE_LIST_CONTAINER");
			if (filterContainer == null || saveListContainer == null)
				return;

			filterContainer.IsVisible = () => filtersVisible;
			filtersToggle.IsChecked = () => filtersVisible;

			// The YAML lays the save list out as if the filter panel were visible.
			var saveListNormalBounds = saveListContainer.Bounds;
			var expandedX = filterContainer.Bounds.X;
			var expandedWidth = saveListNormalBounds.Right - expandedX;

			void ApplyFiltersVisibility(bool reloadGames)
			{
				var newX = filtersVisible ? saveListNormalBounds.X : expandedX;
				var newWidth = filtersVisible ? saveListNormalBounds.Width : expandedWidth;
				var widthDelta = newWidth - saveListContainer.Bounds.Width;

				saveListContainer.Bounds = new WidgetBounds(newX, saveListNormalBounds.Y, newWidth, saveListNormalBounds.Height);

				var saveListLabel = saveListContainer.GetOrNull<LabelWidget>("SAVE_LIST_LABEL");
				if (saveListLabel != null)
					saveListLabel.Bounds.Width += widthDelta;

				gameList.Bounds.Width += widthDelta;

				gameTemplate.Bounds.Width += widthDelta;
				foreach (var child in gameTemplate.Children)
					child.Bounds.Width += widthDelta;

				dateHeaderTemplate.Bounds.Width += widthDelta;
				foreach (var child in dateHeaderTemplate.Children)
					child.Bounds.Width += widthDelta;

				if (reloadGames)
				{
					LoadGames();
					ApplyFilter();
				}
			}

			filtersToggle.OnClick = () =>
			{
				filtersVisible = !filtersVisible;
				ApplyFiltersVisibility(reloadGames: true);
			};

			// Filters are hidden by default, so widen the save list to fill the freed space.
			if (!filtersVisible)
				ApplyFiltersVisibility(reloadGames: false);
		}

		void SetupManagement(Action onExit)
		{
			SetupFiltersToggle();

			var renameButton = panel.Get<ButtonWidget>("RENAME_BUTTON");
			renameButton.IsDisabled = () => saveList.SelectedPath == null;
			renameButton.OnClick = () =>
			{
				var initialName = Path.GetFileNameWithoutExtension(saveList.SelectedPath);

				ConfirmationDialogs.TextInputPrompt(modData,
					RenameSaveTitle,
					RenameSavePrompt,
					initialName,
					onAccept: newName => Rename(initialName, newName),
					onCancel: null,
					acceptText: RenameSaveAccept,
					cancelText: null,
					inputValidator: newName => GameSaveUtils.IsValidNewSaveName(newName, initialName, saveList.BaseSavePath));
			};

			var deleteButton = panel.Get<ButtonWidget>("DELETE_BUTTON");
			deleteButton.IsDisabled = () => saveList.SelectedPath == null;
			deleteButton.OnClick = () =>
			{
				ConfirmationDialogs.ButtonPrompt(modData,
					title: DeleteSaveTitle,
					text: DeleteSavePrompt,
					textArguments: ["save", Path.GetFileNameWithoutExtension(saveList.SelectedPath)],
					onConfirm: () =>
					{
						Delete(saveList.SelectedPath);

						if (!saveList.Saves.Any(s => s.Visible))
						{
							Ui.CloseWindow();
							onExit();
						}
						else
							saveList.SelectFirstVisible();
					},
					confirmText: DeleteSaveAccept,
					onCancel: () => { });
			};

			var deleteAllButton = panel.Get<ButtonWidget>("DELETE_ALL_BUTTON");
			deleteAllButton.IsDisabled = () => !saveList.Saves.Any(s => s.Visible);
			deleteAllButton.OnClick = () =>
			{
				var visible = saveList.Saves.Where(s => s.Visible).ToList();

				ConfirmationDialogs.ButtonPrompt(modData,
					title: DeleteAllSavesTitle,
					text: DeleteAllSavesPrompt,
					textArguments: ["count", visible.Count],
					onConfirm: () =>
					{
						foreach (var s in visible)
							Delete(s.Path);

						if (!saveList.Saves.Any(s => s.Visible))
						{
							Ui.CloseWindow();
							onExit();
						}
					},
					confirmText: DeleteAllSavesAccept,
					onCancel: () => { });
			};
		}

		void Rename(string oldName, string newName)
		{
			var oldPath = Path.Combine(saveList.BaseSavePath, oldName + SaveListModel.Extension);
			var newPath = saveList.Rename(oldName, newName);
			if (newPath == null)
				return;

			if (saveItems.Remove(oldPath, out var item))
			{
				item.ItemKey = newPath;
				item.Get<LabelWidget>("TITLE").GetText = () => newName;
				saveItems[newPath] = item;
			}
		}

		void Delete(string savePath)
		{
			if (saveItems.Remove(savePath, out var item))
				gameList.RemoveChild(item);

			if (!saveList.Delete(savePath) && item != null)
			{
				saveItems[savePath] = item;
				gameList.AddChild(item);
			}
		}

		void OnSelectionChanged()
		{
			playerList.RemoveChildren();

			if (saveList.SelectedPath == null)
			{
				selectedSave = null;
				map = MapCache.UnknownMap;
				return;
			}

			selectedSave = SaveFileInfo.Read(saveList.SelectedPath);
			if (selectedSave == null)
			{
				map = MapCache.UnknownMap;
				return;
			}

			var preview = modData.MapCache[selectedSave.GlobalSettings.Map];
			if (preview.Status != MapStatus.Available && selectedSave.MapGenerationArgs != null)
			{
				preview.UpdateFromGenerationArgs(selectedSave.MapGenerationArgs);
				preview.Generate();
			}

			map = preview;

			UpdatePlayerList();
		}

		Dictionary<int, SpawnOccupant> GetSpawnOccupants()
		{
			if (selectedSave == null)
				return [];

			var occupants = new Dictionary<int, SpawnOccupant>();
			foreach (var (_, slotClient) in selectedSave.SlotClients)
			{
				if (slotClient.SpawnPoint == 0)
					continue;

				var client = new Session.Client
				{
					Color = slotClient.Color,
					Faction = slotClient.Faction,
					SpawnPoint = slotClient.SpawnPoint,
					Team = slotClient.Team,
					Bot = slotClient.Bot,
					Name = slotClient.Bot != null ? slotClient.BotName : string.Empty
				};

				occupants[slotClient.SpawnPoint] = new SpawnOccupant(client);
			}

			return occupants;
		}

		void UpdatePlayerList()
		{
			playerList.RemoveChildren();

			if (selectedSave == null)
				return;

			var factionInfo = modData.DefaultRules.Actors[SystemActors.World].TraitInfos<FactionInfo>();

			var botOrdinals = selectedSave.SlotClients
				.Where(kv => kv.Value.Bot != null)
				.GroupBy(kv => kv.Value.Bot)
				.SelectMany(g => g.Select((kv, i) => (SlotKey: kv.Key, Ordinal: i + 1)))
				.ToFrozenDictionary(x => x.SlotKey, x => x.Ordinal);

			var slotClientsByTeam = selectedSave.SlotClients
				.GroupBy(kv => kv.Value.Team)
				.OrderBy(g => g.Key)
				.ToList();

			var noTeams = slotClientsByTeam.Count == 1;

			foreach (var teamGroup in slotClientsByTeam)
			{
				var team = teamGroup.Key;
				var label = noTeams ? FluentProvider.GetMessage(Players) : team > 0
					? FluentProvider.GetMessage(TeamNumber, "team", team)
					: FluentProvider.GetMessage(NoTeam);

				if (label.Length > 0)
				{
					var header = ScrollItemWidget.Setup(playerHeader, () => false, () => { });
					header.Get<LabelWidget>("LABEL").GetText = () => label;
					playerList.AddChild(header);
				}

				foreach (var (slotKey, slotClient) in teamGroup)
				{
					var displayName = slotClient.Bot != null
						? FluentProvider.GetMessage(EnumeratedBotName,
							"name", FluentProvider.GetMessage(slotClient.BotName),
							"number", botOrdinals[slotKey])
						: FluentProvider.GetMessage(HumanPlayer);

					var color = slotClient.Color;
					var item = ScrollItemWidget.Setup(playerTemplate, () => false, () => { });

					var nameLabel = item.Get<LabelWidget>("LABEL");
					var font = Game.Renderer.Fonts[nameLabel.Font];
					var name = WidgetUtils.TruncateText(displayName, nameLabel.Bounds.Width, font);
					nameLabel.GetText = () => name;
					nameLabel.GetColor = () => color;

					var flag = item.Get<ImageWidget>("FLAG");
					flag.GetImageCollection = () => "flags";
					var faction = slotClient.Faction;
					flag.GetImageName = () => factionInfo != null && factionInfo.Any(f => f.InternalName == faction) ? faction : "Random";

					playerList.AddChild(item);
				}
			}
		}

		void Load()
		{
			if (selectedSave == null)
				return;

			var mapPreview = modData.MapCache[selectedSave.GlobalSettings.Map];
			if (mapPreview.Status != MapStatus.Available)
				return;

			var refusal = LoadPolicy.CanRestoreInto(
				selectedSave.GlobalSettings.Map,
				sessionMapUid,
				saveMapAvailable: true);

			if (refusal != LoadRefusal.None)
			{
				Log.Write("debug", $"Refused a load of '{saveList.SelectedPath}': {refusal}.");
				TextNotificationsManager.AddSystemLine(refusal == LoadRefusal.DifferentMap
					? CannotLoadDifferentMap
					: CannotLoadMapUnavailable);
				return;
			}

			if (loadAction == null)
			{
				Log.Write("debug", $"Refused a load of '{saveList.SelectedPath}': this panel was opened without a " +
					"load action, so there is nothing to do with the chosen save.");
				return;
			}

			Ui.CloseWindow();

			loadAction(saveList.SelectedPath, mapPreview.Uid);
		}

		void OnGameStart()
		{
			Ui.CloseWindow();
			onStart();
		}

		bool disposed;
		protected override void Dispose(bool disposing)
		{
			if (disposing && !disposed)
			{
				disposed = true;
				Game.BeforeGameStart -= OnGameStart;
			}

			base.Dispose(disposing);
		}

		public static bool IsLoadPanelEnabled(Manifest mod)
		{
			var baseSavePath = SavePaths.BaseSaveDirectory(mod);
			if (!Directory.Exists(baseSavePath))
				return false;

			return Directory.GetFiles(baseSavePath, "*.orasav").Length > 0;
		}
	}
}
