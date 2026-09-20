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
using OpenRA.GameSaves;
using OpenRA.Network;
using OpenRA.Widgets;

namespace OpenRA.Mods.Common.Widgets.Logic
{
	public class LobbyLoadSaveLogic : ChromeLogic
	{
		[FluentReference]
		const string UploadingSave = "label-lobby-uploading-save";

		readonly ModData modData;
		readonly OrderManager orderManager;
		readonly ScrollPanelWidget saveList;
		readonly ScrollItemWidget template;

		string selectedSave;

		string uploading;

		[ObjectCreator.UseCtor]
		public LobbyLoadSaveLogic(Widget widget, ModData modData, OrderManager orderManager, Action onExit)
		{
			this.modData = modData;
			this.orderManager = orderManager;

			saveList = widget.Get<ScrollPanelWidget>("SAVE_LIST");
			template = saveList.Get<ScrollItemWidget>("SAVE_TEMPLATE");

			var loadButton = widget.Get<ButtonWidget>("LOAD_BUTTON");
			loadButton.IsDisabled = () => selectedSave == null || uploading != null;
			loadButton.OnClick = Upload;

			var status = widget.GetOrNull<LabelWidget>("STATUS_LABEL");
			if (status != null)
			{
				status.IsVisible = () => uploading != null;
				status.GetText = () => FluentProvider.GetMessage(UploadingSave);
			}

			widget.Get<ButtonWidget>("BACK_BUTTON").OnClick = () => { Ui.CloseWindow(); onExit(); };

			orderManager.GameSaved += OnGameSaved;

			EnumerateSaves();
		}

		void OnGameSaved(string filename)
		{
			if (uploading == null)
				return;

			Log.Write("debug", $"Lobby save upload '{uploading}' finished: the server holds '{filename}'.");
			uploading = null;

			EnumerateSaves();
		}

		protected override void Dispose(bool disposing)
		{
			if (disposing)
				orderManager.GameSaved -= OnGameSaved;

			base.Dispose(disposing);
		}

		void EnumerateSaves()
		{
			saveList.RemoveChildren();
			selectedSave = null;

			var dir = SavePaths.BaseSaveDirectory(modData.Manifest);
			if (!Directory.Exists(dir))
				return;

			foreach (var path in Directory.GetFiles(dir, "*" + SavePaths.Extension).OrderByDescending(File.GetLastWriteTime))
			{
				var info = SaveFileInfo.Read(path);
				if (info == null)
					continue;

				var filename = Path.GetFileName(path);
				var item = ScrollItemWidget.Setup(template,
					() => selectedSave == filename,
					() => selectedSave = filename,
					Upload);

				item.Get<LabelWidget>("TITLE").GetText = () => Path.GetFileNameWithoutExtension(filename);

				var mapTitle = modData.MapCache[info.GlobalSettings.Map].Title;
				var details = info.Duration != null
					? $"{mapTitle} - {info.Duration.Value:hh\\:mm\\:ss}"
					: mapTitle;

				item.Get<LabelWidget>("DETAILS").GetText = () => details;

				saveList.AddChild(item);
			}
		}

		void Upload()
		{
			if (selectedSave == null || uploading != null)
				return;

			byte[] payload;
			try
			{
				payload = File.ReadAllBytes(SavePaths.ResolveSaveFile(modData.Manifest, selectedSave));
			}
			catch (Exception e)
			{
				Log.Write("debug", $"Failed to read save '{selectedSave}':");
				Log.Write("debug", e);
				return;
			}

			uploading = selectedSave;
			orderManager.UploadSnapshotThenLoad(payload, selectedSave);
		}
	}
}
