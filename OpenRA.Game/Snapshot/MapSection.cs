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
using System.Globalization;
using System.Linq;

namespace OpenRA.Snapshot
{
	// What the simulation writes into the map: terrain types of resources, bridges and buildable ground, and bridge tiles.
	static class MapSection
	{
		const char TileSeparator = ' ';
		const char IndexSeparator = '.';

		/// <summary>
		/// The tiles row by row, and each cell that has a terrain type of its own, by name.
		/// </summary>
		public static List<MiniYamlNode> Write(Map map)
		{
			var width = map.Tiles.Size.Width;
			var tiles = map.Tiles.AsReadOnlyMemory().Span;
			var rows = new List<MiniYamlNode>();
			var row = new string[width];
			for (var v = 0; v < map.Tiles.Size.Height; v++)
			{
				for (var u = 0; u < width; u++)
				{
					var tile = tiles[v * width + u];
					row[u] = tile.Type.ToString(CultureInfo.InvariantCulture) + IndexSeparator + tile.Index.ToString(CultureInfo.InvariantCulture);
				}

				rows.Add(new MiniYamlNode(FieldSaver.FormatValue(v), string.Join(TileSeparator, row)));
			}

			var terrainTypes = map.Rules.TerrainInfo.TerrainTypes;
			var customTerrain = map.CustomTerrain.AsReadOnlyMemory().Span;
			var cells = new List<MiniYamlNode>();
			for (var i = 0; i < customTerrain.Length; i++)
				if (customTerrain[i] != byte.MaxValue)
					cells.Add(new MiniYamlNode(FieldSaver.FormatValue(i), terrainTypes[customTerrain[i]].Type));

			return
			[
				new(SnapshotFormat.Keys.Tiles, new MiniYaml("", rows)),
				new(SnapshotFormat.Keys.CustomTerrain, new MiniYaml("", cells)),
			];
		}

		/// <summary>
		/// Gives each cell its saved tile and terrain type through its layer, which tells the map, the locomotors and the
		/// path graph. A reader calls this before it writes the saved values, as listeners change saved state.
		/// </summary>
		public static void Read(Map map, MiniYaml section)
		{
			ReadTiles(map, section.NodeWithKey(SnapshotFormat.Keys.Tiles).Value);
			ReadCustomTerrain(map, section.NodeWithKey(SnapshotFormat.Keys.CustomTerrain).Value);
		}

		static void ReadTiles(Map map, MiniYaml rows)
		{
			var layer = map.Tiles;
			var name = $"{SnapshotFormat.Sections.Map}.{SnapshotFormat.Keys.Tiles}";
			if (rows.Nodes.Length != layer.Size.Height)
				throw new SnapshotLoadException($"{name} has {rows.Nodes.Length} rows of tiles, and the map has {layer.Size.Height}.");

			for (var i = 0; i < rows.Nodes.Length; i++)
			{
				var node = rows.Nodes[i];
				var path = $"{name}[{node.Key}]";
				var v = SnapshotReader.Parse<int>(node.Key, path);
				var row = (node.Value.Value ?? "").Split(TileSeparator);
				if (v != i || row.Length != layer.Size.Width)
					throw new SnapshotLoadException($"{path} is not a row of the map in its place. "
						+ $"The map has {layer.Size.Height} rows of {layer.Size.Width} tiles, and row {i} is expected here.");

				for (var u = 0; u < row.Length; u++)
				{
					var separator = row[u].IndexOf(IndexSeparator);
					if (separator < 0 || !ushort.TryParse(row[u].AsSpan(0, separator), NumberStyles.None, CultureInfo.InvariantCulture, out var type)
						|| !byte.TryParse(row[u].AsSpan(separator + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var index))
						throw new SnapshotLoadException($"{path}: \"{row[u]}\" is not a tile.");

					var cell = new MPos(u, v);
					var tile = layer[cell];
					if (tile.Type != type || tile.Index != index)
						layer[cell] = new TerrainTile(type, index);
				}
			}
		}

		static void ReadCustomTerrain(Map map, MiniYaml cells)
		{
			var layer = map.CustomTerrain;
			var terrainTypes = map.Rules.TerrainInfo.TerrainTypes.Select((t, i) => (t.Type, Index: i)).ToDictionary(t => t.Type, t => t.Index);
			var saved = new byte[layer.Size.Width * layer.Size.Height];
			Array.Fill(saved, byte.MaxValue);
			foreach (var node in cells.Nodes)
			{
				var path = $"{SnapshotFormat.Sections.Map}.{SnapshotFormat.Keys.CustomTerrain}[{node.Key}]";
				var index = SnapshotReader.Parse<int>(node.Key, path);
				if (index < 0 || index >= saved.Length)
					throw new SnapshotLoadException($"{path}: the cell is outside the map, which has {saved.Length} cells.");

				if (!terrainTypes.TryGetValue(node.Value.Value ?? "", out var terrain))
					throw new SnapshotLoadException($"{path}: the rules have no terrain type {node.Value.Value}.");

				saved[index] = (byte)terrain;
			}

			for (var i = 0; i < saved.Length; i++)
			{
				var cell = new MPos(i % layer.Size.Width, i / layer.Size.Width);
				if (layer[cell] != saved[i])
					layer[cell] = saved[i];
			}
		}
	}
}
