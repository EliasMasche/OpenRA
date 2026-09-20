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
using System.IO;
using System.Linq;
using OpenRA.Traits;

namespace OpenRA.GameSaves
{
	/// <summary>Converts actors, players, and targets to text, and back.</summary>
	/// <remarks>
	/// A reference is text so that it survives a round trip through YAML. A prefix letter gives the form:
	/// A for an actor, P for a player, F for a frozen actor, C for a cell, and TP for a position.
	/// <para>
	/// A malformed reference throws. A reference that is well formed but names an actor that no longer
	/// exists yields null or <see cref="Target.Invalid"/>, because an actor can leave the world between
	/// the save and the restore.
	/// </para>
	/// </remarks>
	public static class SnapshotRefs
	{
		public const string Null = "-";

		const char Separator = ':';
		const char PositionSeparator = ';';
		const char CoordinateSeparator = ',';

		const string ActorPrefix = "A";
		const string PlayerPrefix = "P";
		const string FrozenActorPrefix = "F";
		const string TerrainCellPrefix = "C";
		const string TerrainPositionPrefix = "TP";
		const string InvalidTarget = "Invalid";

		public static string FormatActor(Actor a)
		{
			if (a == null)
				return Null;

			return ActorPrefix + Separator + a.ActorID.ToStringInvariant();
		}

		public static string FormatActorID(uint actorID)
		{
			return ActorPrefix + Separator + actorID.ToStringInvariant();
		}

		public static Actor ParseActor(World world, string value, Func<uint, Actor> resolveActor = null)
		{
			if (!TryParseActorID(value, out var actorID))
				return null;

			return resolveActor != null ? resolveActor(actorID) : world?.GetActorById(actorID);
		}

		public static bool TryParseActorID(string value, out uint actorID)
		{
			actorID = 0;
			if (string.IsNullOrEmpty(value) || value == Null)
				return false;

			var parts = value.Split(Separator);

			if (parts[0] != ActorPrefix || (parts.Length != 2 && parts.Length != 3))
				throw new InvalidDataException($"Malformed actor reference '{value}'.");

			if (!uint.TryParse(parts[1], NumberStyles.None, NumberFormatInfo.InvariantInfo, out actorID))
				throw new InvalidDataException($"Malformed actor reference '{value}'.");

			return true;
		}

		public static bool IsActorTarget(string value)
		{
			if (string.IsNullOrEmpty(value) || value == Null)
				return false;

			var separator = value.IndexOf(Separator);
			return separator > 0 && value[..separator] == ActorPrefix;
		}

		public static string FormatPlayer(Player p)
		{
			if (p == null)
				return Null;

			return PlayerPrefix + Separator + p.InternalName;
		}

		public static Player ParsePlayer(World world, string value)
		{
			if (string.IsNullOrEmpty(value) || value == Null)
				return null;

			var parts = value.Split(Separator, 2);
			if (parts.Length != 2 || parts[0] != PlayerPrefix || parts[1].Length == 0)
				throw new InvalidDataException($"Malformed player reference '{value}'.");

			return world?.Players.FirstOrDefault(p => p.InternalName == parts[1]);
		}

		public static string FormatTarget(in Target target)
		{
			var state = target.SerializableState;
			switch (state.Type)
			{
				case TargetType.Actor:
					return ActorPrefix + Separator + state.Actor.ActorID.ToStringInvariant() +
						Separator + state.Generation.ToStringInvariant();

				case TargetType.FrozenActor:
				{
					var frozen = target.FrozenActor;
					return FrozenActorPrefix + Separator + frozen.Viewer.InternalName +
						Separator + frozen.ID.ToStringInvariant();
				}

				case TargetType.Terrain:
					if (state.Cell != null)
					{
						var cell = TerrainCellPrefix + Separator + state.Cell.Value.Bits.ToStringInvariant();
						if (state.SubCell != null)
							cell += Separator + ((int)state.SubCell.Value).ToStringInvariant();

						return cell;
					}
					else
					{
						var positions = state.TerrainPositions;

						if (positions.Length == 1 && positions[0] == state.Pos)
							return TerrainPositionPrefix + Separator + FormatPos(state.Pos);

						return TerrainPositionPrefix + Separator + FormatPos(state.Pos) + PositionSeparator +
							string.Join(PositionSeparator, positions.Select(FormatPos));
					}

				case TargetType.Invalid:
				default:
					return InvalidTarget;
			}
		}

		public static Target ParseTarget(World world, string value, Func<uint, Actor> resolveActor = null)
		{
			if (string.IsNullOrEmpty(value) || value == Null || value == InvalidTarget)
				return Target.Invalid;

			var parts = value.Split(Separator);
			switch (parts[0])
			{
				case ActorPrefix:
				{
					if (parts.Length != 3 ||
						!uint.TryParse(parts[1], NumberStyles.None, NumberFormatInfo.InvariantInfo, out var actorID) ||
						!Exts.TryParseInt32Invariant(parts[2], out var generation))
						throw new InvalidDataException($"Malformed actor target '{value}'.");

					var actor = resolveActor != null ? resolveActor(actorID) : world?.GetActorById(actorID);
					return actor != null ? Target.FromSerializedActor(actor, generation) : Target.Invalid;
				}

				case FrozenActorPrefix:
				{
					var idStart = value.LastIndexOf(Separator);
					if (idStart <= FrozenActorPrefix.Length ||
						!uint.TryParse(value[(idStart + 1)..], NumberStyles.None, NumberFormatInfo.InvariantInfo, out var frozenID))
						throw new InvalidDataException($"Malformed frozen actor target '{value}'.");

					var viewerName = value[(FrozenActorPrefix.Length + 1)..idStart];
					if (viewerName.Length == 0)
						throw new InvalidDataException($"Malformed frozen actor target '{value}'.");

					var viewer = world?.Players.FirstOrDefault(p => p.InternalName == viewerName);
					var frozen = viewer?.FrozenActorLayer?.FromID(frozenID);
					return frozen != null ? Target.FromFrozenActor(frozen) : Target.Invalid;
				}

				case TerrainCellPrefix:
				{
					if ((parts.Length != 2 && parts.Length != 3) || !Exts.TryParseInt32Invariant(parts[1], out var bits))
						throw new InvalidDataException($"Malformed terrain cell target '{value}'.");

					var subCell = SubCell.FullCell;
					if (parts.Length == 3)
					{
						if (!Exts.TryParseInt32Invariant(parts[2], out var rawSubCell))
							throw new InvalidDataException($"Malformed terrain cell target '{value}'.");

						subCell = (SubCell)rawSubCell;
					}

					return world != null ? Target.FromCell(world, new CPos(bits), subCell) : Target.Invalid;
				}

				case TerrainPositionPrefix:
				{
					var payload = value[(TerrainPositionPrefix.Length + 1)..];
					if (value.Length <= TerrainPositionPrefix.Length || payload.Length == 0)
						throw new InvalidDataException($"Malformed terrain position target '{value}'.");

					var chunks = payload.Split(PositionSeparator);
					var center = ParsePos(chunks[0], value);
					if (chunks.Length == 1)
						return Target.FromPos(center);

					var positions = new WPos[chunks.Length - 1];
					for (var i = 0; i < positions.Length; i++)
						positions[i] = ParsePos(chunks[i + 1], value);

					return Target.FromSerializedTerrainPosition(center, positions);
				}

				default:
					throw new InvalidDataException($"Unknown target reference '{value}'.");
			}
		}

		static string FormatPos(WPos p)
		{
			return p.X.ToStringInvariant() + CoordinateSeparator + p.Y.ToStringInvariant() +
				CoordinateSeparator + p.Z.ToStringInvariant();
		}

		static WPos ParsePos(string value, string reference)
		{
			var parts = value.Split(CoordinateSeparator);
			if (parts.Length != 3 ||
				!Exts.TryParseInt32Invariant(parts[0], out var x) ||
				!Exts.TryParseInt32Invariant(parts[1], out var y) ||
				!Exts.TryParseInt32Invariant(parts[2], out var z))
				throw new InvalidDataException($"Malformed position '{value}' in '{reference}'.");

			return new WPos(x, y, z);
		}
	}
}
