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

using System.Collections.Generic;
using System.IO;
using OpenRA.GameSaves;
using OpenRA.Traits;

namespace OpenRA.Mods.Common.Traits
{
	[Desc("Saves the map layers that change while a game runs, so a snapshot can restore them.",
		"Attach this to the world actor.")]
	[TraitLocation(SystemActors.World | SystemActors.EditorWorld)]
	public class MapLayerSnapshotInfo : TraitInfo
	{
		public override object Create(ActorInitializer init) { return new MapLayerSnapshot(init.World); }
	}

	public class MapLayerSnapshot : IWorldSaveState
	{
		readonly Map map;

		readonly CellLayer<TerrainTile> initialTiles;
		readonly CellLayer<byte> initialCustomTerrain;

		public MapLayerSnapshot(World world)
		{
			map = world.Map;

			initialTiles = new CellLayer<TerrainTile>(map);
			initialTiles.CopyValuesFrom(map.Tiles);

			initialCustomTerrain = new CellLayer<byte>(map);
			initialCustomTerrain.CopyValuesFrom(map.CustomTerrain);
		}

		string IWorldSaveState.SectionName => "MapLayers";

		void IWorldSaveState.SaveState(Actor self, Stream s, SnapshotWriter w)
		{
			var writer = new BinaryWriter(s);

			writer.Write(map.MapSize.Width);
			writer.Write(map.MapSize.Height);

			var changedTiles = new List<MPos>();
			var changedTerrain = new List<MPos>();

			foreach (var uv in map.AllCells.MapCoords)
			{
				if (!map.Tiles[uv].Equals(initialTiles[uv]))
					changedTiles.Add(uv);

				if (map.CustomTerrain[uv] != initialCustomTerrain[uv])
					changedTerrain.Add(uv);
			}

			writer.Write(changedTiles.Count);
			foreach (var uv in changedTiles)
			{
				var tile = map.Tiles[uv];
				writer.Write(uv.U);
				writer.Write(uv.V);
				writer.Write(tile.Type);
				writer.Write(tile.Index);
			}

			writer.Write(changedTerrain.Count);
			foreach (var uv in changedTerrain)
			{
				writer.Write(uv.U);
				writer.Write(uv.V);
				writer.Write(map.CustomTerrain[uv]);
			}
		}

		void IWorldSaveState.LoadState(Actor self, Stream s, SnapshotReader r)
		{
			var reader = new BinaryReader(s);

			var width = reader.ReadInt32();
			var height = reader.ReadInt32();
			if (width != map.MapSize.Width || height != map.MapSize.Height)
				throw new InvalidDataException(
					$"Snapshot holds a {width}x{height} map, but this one is {map.MapSize.Width}x{map.MapSize.Height}.");

			foreach (var uv in map.AllCells.MapCoords)
			{
				if (!map.Tiles[uv].Equals(initialTiles[uv]))
					map.Tiles[uv] = initialTiles[uv];

				if (map.CustomTerrain[uv] != initialCustomTerrain[uv])
					map.CustomTerrain[uv] = initialCustomTerrain[uv];
			}

			var tileCount = reader.ReadInt32();
			for (var i = 0; i < tileCount; i++)
			{
				var uv = new MPos(reader.ReadInt32(), reader.ReadInt32());
				map.Tiles[uv] = new TerrainTile(reader.ReadUInt16(), reader.ReadByte());
			}

			var terrainCount = reader.ReadInt32();
			for (var i = 0; i < terrainCount; i++)
			{
				var uv = new MPos(reader.ReadInt32(), reader.ReadInt32());
				map.CustomTerrain[uv] = reader.ReadByte();
			}
		}
	}
}
