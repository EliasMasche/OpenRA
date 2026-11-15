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

namespace OpenRA.Network
{
	public partial class GameSave
	{
		// The snapshot stays in the file until a caller asks for it, because the save browsers read every save of a
		// folder.
		byte[] snapshot = [];
		string snapshotFile = "";
		long snapshotOffset = -1;
		int snapshotLength;

		void ReadSnapshot(Stream rs, string filepath)
		{
			// A save with a snapshot has one more block, between the trait data and the end of the file.
			var blocksEnd = rs.Length - TrailerLength;
			if (rs.Position < blocksEnd)
			{
				var marker = rs.ReadInt32();
				if (marker == SnapshotMarker)
				{
					snapshotFile = filepath;
					snapshotOffset = rs.Position;
					if (snapshotOffset + 4 > blocksEnd)
						throw new InvalidDataException("Invalid orasav file");

					snapshotLength = rs.ReadInt32();
					if (snapshotOffset + 4 + snapshotLength != blocksEnd)
						throw new InvalidDataException("Invalid orasav file");
				}
				else
					throw new InvalidDataException("Invalid orasav file");
			}
		}

		/// <summary>A save that holds the state of the world in place of every order.</summary>
		public static GameSave FromSnapshot(Session lobbyInfo, MapPreview map, byte[] snapshot, int netFrame)
		{
			if (snapshot == null || snapshot.Length == 0)
				throw new ArgumentException("A save without orders needs a snapshot.", nameof(snapshot));

			var save = new GameSave();
			save.StartGame(lobbyInfo, map);
			save.snapshot = snapshot;

			// The lobby of a client lacks the timestep, which the server sets later.
			var gameSpeeds = Game.ModData.GetOrCreate<GameSpeeds>();
			save.GlobalSettings.GameTimestep = gameSpeeds.Speeds[save.GlobalSettings.OptionOrDefault("gamespeed", gameSpeeds.DefaultSpeed)].Timestep;

			save.LastOrdersFrame = netFrame - 1;
			return save;
		}

		/// <summary>The save in the bytes <paramref name="file"/> of another machine. Throws when it is not a save.</summary>
		public static GameSave FromBytes(byte[] file, string name)
		{
			var save = new GameSave(new MemoryStream(file), name);
			if (save.snapshotOffset >= 0)
			{
				save.snapshot = file.AsSpan((int)save.snapshotOffset + 4, save.snapshotLength).ToArray();
				save.snapshotOffset = -1;
			}

			return save;
		}

		public bool HasSnapshot => snapshot.Length > 0 || snapshotOffset >= 0;

		/// <summary>The bytes of the snapshot, or none. A snapshot block that is not the one that was read is refused.</summary>
		public byte[] ReadSnapshot()
		{
			if (snapshotOffset < 0)
				return snapshot;

			using (var rs = File.OpenRead(snapshotFile))
			{
				if (rs.Length != snapshotOffset + 4 + snapshotLength + TrailerLength)
					throw new InvalidDataException("The save file has another length than when it was read.");

				rs.Seek(snapshotOffset, SeekOrigin.Begin);
				if (rs.ReadInt32() != snapshotLength)
					throw new InvalidDataException("The snapshot of the save has another length than when the save was read.");

				return rs.ReadBytes(snapshotLength);
			}
		}
	}
}
