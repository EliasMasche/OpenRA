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
using System.Globalization;
using System.Linq;
using OpenRA.GameSaves;
using OpenRA.Widgets;

namespace OpenRA.Mods.Common.Widgets.Logic
{
	public class SaveLoadDebugLogic : ChromeLogic
	{
		[FluentReference]
		const string DiffCaptured = "label-debug-panel-diff-captured";

		[FluentReference]
		const string DiffNotCaptured = "label-debug-panel-diff-not-captured";

		[FluentReference]
		const string DiffMatches = "label-debug-panel-diff-matches";

		[FluentReference("count")]
		const string DiffDiffers = "label-debug-panel-diff-differs";

		[FluentReference]
		const string SaveFormatSnapshot = "label-debug-panel-save-format-snapshot";

		[FluentReference]
		const string SaveFormatReplay = "label-debug-panel-save-format-replay";

		const string SavePattern = "debug-";

		readonly World world;

		SnapshotDiff captured;

		string comparison;

		[ObjectCreator.UseCtor]
		public SaveLoadDebugLogic(Widget widget, World world)
		{
			ArgumentNullException.ThrowIfNull(widget);
			ArgumentNullException.ThrowIfNull(world);

			this.world = world;

			var saveButton = widget.GetOrNull<ButtonWidget>("SAVE_SNAPSHOT");
			if (saveButton != null)
			{
				saveButton.IsDisabled = () => !world.LobbyInfo.GlobalSettings.EnableGameSaves || world.IsReplay
					|| (world.LobbyInfo.NonBotClients.Count() > 1 && !Game.IsHost);

				saveButton.OnClick = Save;
			}

			var hashLabel = widget.GetOrNull<LabelWidget>("SYNC_HASH");
			if (hashLabel != null)
				hashLabel.GetText = () => world.SyncHash().ToStringInvariant();

			var tickLabel = widget.GetOrNull<LabelWidget>("WORLD_TICK");
			if (tickLabel != null)
				tickLabel.GetText = () => world.WorldTick.ToStringInvariant();

			var formatLabel = widget.GetOrNull<LabelWidget>("SAVE_FORMAT");
			if (formatLabel != null)
				formatLabel.GetText = () => FluentProvider.GetMessage(
					world.UseSnapshotSaves ? SaveFormatSnapshot : SaveFormatReplay);

			var captureButton = widget.GetOrNull<ButtonWidget>("CAPTURE_DIFF");
			if (captureButton != null)
			{
				captureButton.OnClick = () =>
				{
					captured = SnapshotDiff.Capture(world);
					comparison = null;
				};
			}

			var compareButton = widget.GetOrNull<ButtonWidget>("COMPARE_DIFF");
			if (compareButton != null)
			{
				compareButton.IsDisabled = () => captured == null;
				compareButton.OnClick = Compare;
			}

			var resultLabel = widget.GetOrNull<LabelWidget>("DIFF_RESULT");
			if (resultLabel != null)
				resultLabel.GetText = () => comparison ?? FluentProvider.GetMessage(
					captured == null ? DiffNotCaptured : DiffCaptured);
		}

		void Save()
		{
			var dateTime = DateTime.UtcNow.ToString("yyyy-MM-ddTHHmmssZ", CultureInfo.InvariantCulture);
			world.RequestGameSave($"{SavePattern}{dateTime}{SavePaths.Extension}", false);
		}

		void Compare()
		{
			var detail = SnapshotDiff.Compare(captured, SnapshotDiff.Capture(world));
			if (string.IsNullOrEmpty(detail))
			{
				comparison = FluentProvider.GetMessage(DiffMatches);
				return;
			}

			var lines = detail.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
			comparison = FluentProvider.GetMessage(DiffDiffers, "count", lines.Length);

			Log.Write("debug", $"Save/load debug comparison, {lines.Length.ToStringInvariant()} differences:");
			Log.Write("debug", detail);
		}
	}
}
