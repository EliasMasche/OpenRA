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

using System.IO;

namespace OpenRA.Traits
{
	public static class TargetCodec
	{
		const byte TagInvalid = 0;
		const byte TagActor = 1;
		const byte TagFrozenActor = 2;
		const byte TagTerrainCell = 3;
		const byte TagTerrainPosition = 4;

		const short ImplicitPositions = -1;

		public static void Write(BinaryWriter w, in Target target)
		{
			var state = target.SerializableState;
			switch (state.Type)
			{
				case TargetType.Actor:
					w.Write(TagActor);
					w.Write(state.Actor.ActorID);
					w.Write(state.Generation);
					break;

				case TargetType.FrozenActor:
					w.Write(TagFrozenActor);
					w.Write(target.FrozenActor.Viewer.PlayerActor.ActorID);

					w.Write(target.FrozenActor.ID);
					break;

				case TargetType.Terrain:
					if (state.Cell != null)
					{
						w.Write(TagTerrainCell);
						w.Write(state.Cell.Value.Bits);
						w.Write((byte)state.SubCell);
					}
					else
					{
						w.Write(TagTerrainPosition);
						w.Write(state.Pos.X);
						w.Write(state.Pos.Y);
						w.Write(state.Pos.Z);

						var positions = state.TerrainPositions;
						if (positions.Length == 1 && positions[0] == state.Pos)
							w.Write(ImplicitPositions);
						else
						{
							w.Write((short)positions.Length);
							foreach (var p in positions)
							{
								w.Write(p.X);
								w.Write(p.Y);
								w.Write(p.Z);
							}
						}
					}

					break;

				case TargetType.Invalid:
				default:
					w.Write(TagInvalid);
					break;
			}
		}

		public static Target Read(World world, BinaryReader r)
		{
			var tag = r.ReadByte();
			switch (tag)
			{
				case TagInvalid:
					return Target.Invalid;

				case TagActor:
				{
					var actorID = r.ReadUInt32();
					var generation = r.ReadInt32();
					var actor = world?.GetActorById(actorID);
					return actor != null ? Target.FromSerializedActor(actor, generation) : Target.Invalid;
				}

				case TagFrozenActor:
				{
					var playerActorID = r.ReadUInt32();
					var frozenActorID = r.ReadUInt32();

					var playerActor = world?.GetActorById(playerActorID);
					var layer = playerActor?.Owner.FrozenActorLayer;
					var frozen = layer?.FromID(frozenActorID);
					return frozen != null ? Target.FromFrozenActor(frozen) : Target.Invalid;
				}

				case TagTerrainCell:
				{
					var cell = new CPos(r.ReadInt32());
					var subCell = (SubCell)r.ReadByte();
					return world != null ? Target.FromCell(world, cell, subCell) : Target.Invalid;
				}

				case TagTerrainPosition:
				{
					var pos = new WPos(r.ReadInt32(), r.ReadInt32(), r.ReadInt32());
					var count = r.ReadInt16();
					if (count == ImplicitPositions)
						return Target.FromPos(pos);

					if (count < 0)
						throw new InvalidDataException($"Invalid terrain position count {count}.");

					var positions = new WPos[count];
					for (var i = 0; i < count; i++)
						positions[i] = new WPos(r.ReadInt32(), r.ReadInt32(), r.ReadInt32());

					return Target.FromSerializedTerrainPosition(pos, positions);
				}

				default:
					throw new InvalidDataException($"Unknown target type tag {tag}.");
			}
		}
	}
}
