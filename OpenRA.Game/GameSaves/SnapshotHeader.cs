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
using System.Text;

namespace OpenRA.GameSaves
{
	/// <summary>The fields at the start of a snapshot, before the sections.</summary>
	/// <remarks>
	/// The reader checks the format version while it reads the header, and <see cref="WorldRestorer"/>
	/// checks the map id before it reads a section. A snapshot from another format or another map
	/// therefore fails early. The header also carries the sync hash that the restore compares at the end.
	/// </remarks>
	public sealed class SnapshotHeader
	{
		public const int CurrentFormatVersion = 2;

		const int MaxStringLength = 1024;

		public readonly int FormatVersion;
		public readonly string EngineVersion;
		public readonly string ModId;
		public readonly string ModVersion;
		public readonly string MapUid;

		public readonly int WorldTick;

		public readonly int SyncHash;

		public readonly int ReportedFrame;

		public readonly int ReportedSyncHash;

		public readonly ulong ReportedDefeatState;

		public readonly DateTime SavedUtc;
		public readonly SnapshotFlags Flags;

		public readonly int DroppedActivities;

		public readonly int DroppedEffects;

		public SnapshotHeader(string engineVersion, string modId, string modVersion, string mapUid,
			int worldTick, int syncHash, int reportedFrame, int reportedSyncHash, ulong reportedDefeatState,
			DateTime savedUtc, SnapshotFlags flags, int droppedActivities = 0, int droppedEffects = 0)
		{
			FormatVersion = CurrentFormatVersion;
			EngineVersion = engineVersion ?? "";
			ModId = modId ?? "";
			ModVersion = modVersion ?? "";
			MapUid = mapUid ?? "";
			WorldTick = worldTick;
			SyncHash = syncHash;
			ReportedFrame = reportedFrame;
			ReportedSyncHash = reportedSyncHash;
			ReportedDefeatState = reportedDefeatState;
			SavedUtc = savedUtc;
			Flags = flags;
			DroppedActivities = droppedActivities;
			DroppedEffects = droppedEffects;
		}

		internal SnapshotHeader(Stream s)
		{
			FormatVersion = s.ReadInt32();
			if (FormatVersion != CurrentFormatVersion)
				throw new InvalidDataException($"Unsupported snapshot format version {FormatVersion}.");

			EngineVersion = s.ReadLengthPrefixedString(Encoding.UTF8, MaxStringLength);
			ModId = s.ReadLengthPrefixedString(Encoding.UTF8, MaxStringLength);
			ModVersion = s.ReadLengthPrefixedString(Encoding.UTF8, MaxStringLength);
			MapUid = s.ReadLengthPrefixedString(Encoding.UTF8, MaxStringLength);
			WorldTick = s.ReadInt32();
			SyncHash = s.ReadInt32();
			ReportedFrame = s.ReadInt32();
			ReportedSyncHash = s.ReadInt32();
			ReportedDefeatState = (ulong)SnapshotContainer.ReadInt64(s);

			try
			{
				SavedUtc = DateTime.FromBinary(SnapshotContainer.ReadInt64(s));
			}
			catch (ArgumentException e)
			{
				throw new InvalidDataException("Snapshot header contains an invalid timestamp.", e);
			}

			Flags = (SnapshotFlags)s.ReadInt32();
			DroppedActivities = s.ReadInt32();
			DroppedEffects = s.ReadInt32();
		}

		internal void Write(Stream s)
		{
			s.Write(FormatVersion);
			s.WriteLengthPrefixedString(Encoding.UTF8, EngineVersion);
			s.WriteLengthPrefixedString(Encoding.UTF8, ModId);
			s.WriteLengthPrefixedString(Encoding.UTF8, ModVersion);
			s.WriteLengthPrefixedString(Encoding.UTF8, MapUid);
			s.Write(WorldTick);
			s.Write(SyncHash);
			s.Write(ReportedFrame);
			s.Write(ReportedSyncHash);
			s.Write(ReportedDefeatState);
			s.Write(SavedUtc.ToBinary());
			s.Write((int)Flags);
			s.Write(DroppedActivities);
			s.Write(DroppedEffects);
		}
	}
}
