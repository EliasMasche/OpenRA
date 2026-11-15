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
using System.IO;
using System.Linq;
using System.Text;
using OpenRA.Server;
using OpenRA.Snapshot;

namespace OpenRA.Network
{
	/// <summary>
	/// The copy of a save of a game of more people. The admin reports its own copy, each other client the file of the
	/// admin. The server hands the packets in transit of a copy to the person in the slot of its writer.
	/// </summary>
	public static class GameSaveCopy
	{
		public const string OrderString = "GameSaveCopy";

		const string Tick = "Tick";
		const string SyncHash = "SyncHash";

		// The characters that Windows refuses in a file name, on every platform, so that every client saves or none does.
		static readonly char[] InvalidCopyNameChars =
			[.. Enumerable.Range(0, 32).Select(c => (char)c), '"', '<', '>', '|', '?', '*', ':', '/', '\\'];

		// Before Windows 11, a file name of a device with an extension names the device.
		static readonly FrozenSet<string> DeviceNames = new[] { "CON", "PRN", "AUX", "NUL" }
			.Concat(Enumerable.Range(0, 10).SelectMany(i => new[] { $"COM{i}", $"LPT{i}" }))
			.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

		internal static bool IsCopyName(string name)
		{
			return !string.IsNullOrEmpty(name) && name.IndexOfAny(InvalidCopyNameChars) < 0 && Path.GetExtension(name) == ".orasav"
				&& !DeviceNames.Contains(name.Split('.')[0].TrimEnd(' '));
		}

		internal static string SavePath(string name)
		{
			var mod = Game.ModData.Manifest;
			return Path.Combine(Platform.SupportDir, "Saves", mod.Id, mod.Metadata.Version, Path.GetFileName(name));
		}

		// The report of the copy in file, or null. The log says why.
		internal static string Report(Session.Global settings, string file)
		{
			try
			{
				return ReportOf(CheckedSnapshot(settings, new GameSave(SavePath(file))), []);
			}
			catch (Exception e)
			{
				Log.Write("client", $"This client has no copy of the save {settings.SnapshotSave} of the lobby in the file {file}: {e.Message}");
				return null;
			}
		}

		// The snapshot of save, when it is the save of the lobby. Throws otherwise.
		internal static SnapshotFile CheckedSnapshot(Session.Global settings, GameSave save)
		{
			if (!save.HasSnapshot || save.GlobalSettings.GameUid != settings.GameUid)
				throw new InvalidDataException("the file is not a save of this game.");

			var snapshot = SnapshotFile.Read(save.ReadSnapshot());
			if (snapshot.Mod != Game.ModData.Manifest.Id || snapshot.Map != settings.Map)
				throw new InvalidDataException($"the save is of the map {snapshot.Map} of the mod {snapshot.Mod}, and the lobby is of the map {settings.Map}.");

			if (snapshot.Tick != settings.SnapshotSaveTick || snapshot.SyncHash != settings.SnapshotSaveSyncHash)
				throw new InvalidDataException($"the save is of tick {snapshot.Tick}, and the save of the lobby of tick {settings.SnapshotSaveTick}.");

			return snapshot;
		}

		internal static OrderManagerPosition PositionIn(string report, Session.Global settings)
		{
			var (tick, syncHash, position) = Read(report);
			if (tick != settings.SnapshotSaveTick || syncHash != settings.SnapshotSaveSyncHash)
				throw new InvalidDataException("The copy is of another save.");

			return position;
		}

		internal static (int Tick, int SyncHash, OrderManagerPosition Position) Read(string report)
		{
			var nodes = MiniYaml.FromString(report, OrderString);
			int Number(string key) => FieldLoader.GetValue<int>(key, nodes.First(n => n.Key == key).Value.Value);
			return (Number(Tick), Number(SyncHash), OrdersSection.ReadPosition(nodes.First(n => n.Key == SnapshotFormat.Sections.Orders).Value));
		}

		// A report has to fit one order.
		static string ReportOf(SnapshotFile snapshot, List<MiniYamlNode> first)
		{
			var report = first.Concat(
			[
				new(Tick, FieldSaver.FormatValue(snapshot.Tick)),
				new(SyncHash, FieldSaver.FormatValue(snapshot.SyncHash)),
				new(SnapshotFormat.Sections.Orders, snapshot.Section(SnapshotFormat.Sections.Orders)),
			]).WriteToString();

			if (Encoding.UTF8.GetByteCount(report) > Connection.MaxOrderLength / 2)
				throw new InvalidDataException("its packets in transit are too long for one order.");

			return report;
		}
	}
}
