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
using OpenRA.GameSaves;
using OpenRA.Network;

namespace OpenRA.Mods.Common.Widgets.Logic
{
	public sealed class SaveFileInfo
	{
		public SaveFileFormat Format { get; private init; }
		public Session.Global GlobalSettings { get; private init; }
		public Dictionary<string, SlotClient> SlotClients { get; private init; }

		public TimeSpan? Duration { get; private init; }

		public bool IsAutosave { get; private init; }

		public MapGenerationArgs MapGenerationArgs { get; private init; }

		public static SaveFileInfo Read(string path)
		{
			try
			{
				return SaveFileFormatDetector.Detect(path) switch
				{
					SaveFileFormat.Snapshot => ReadSnapshot(path),
					SaveFileFormat.LegacyReplay => ReadLegacyReplay(path),
					_ => null,
				};
			}
			catch (Exception e)
			{
				Log.Write("debug", $"Failed to read save '{path}':");
				Log.Write("debug", e);
				return null;
			}
		}

		static SaveFileInfo ReadSnapshot(string path)
		{
			using var stream = File.OpenRead(path);
			using var reader = new SnapshotReader(stream);

			var lobby = SnapshotLobby.Read(reader);
			if (lobby == null)
				return null;

			var timestep = lobby.GlobalSettings.GameTimestep;
			return new SaveFileInfo
			{
				Format = SaveFileFormat.Snapshot,
				GlobalSettings = lobby.GlobalSettings,
				SlotClients = lobby.SlotClients,
				MapGenerationArgs = lobby.MapGenerationArgs,

				Duration = timestep > 0
					? TimeSpan.FromMilliseconds((long)reader.Header.WorldTick * timestep)
					: null,
				IsAutosave = reader.Header.Flags.HasFlag(SnapshotFlags.Autosave)
			};
		}

		static SaveFileInfo ReadLegacyReplay(string path)
		{
			var save = new GameSave(path);
			var timestep = save.GlobalSettings.GameTimestep;
			return new SaveFileInfo
			{
				Format = SaveFileFormat.LegacyReplay,
				GlobalSettings = save.GlobalSettings,
				SlotClients = save.SlotClients,
				MapGenerationArgs = save.MapGenerationArgs,
				Duration = timestep > 0 && save.LastOrdersFrame >= 0
					? TimeSpan.FromMilliseconds((long)save.LastOrdersFrame * timestep)
					: null,

				IsAutosave = Path.GetFileNameWithoutExtension(path)
					.StartsWith("autosave", StringComparison.OrdinalIgnoreCase)
			};
		}
	}
}
