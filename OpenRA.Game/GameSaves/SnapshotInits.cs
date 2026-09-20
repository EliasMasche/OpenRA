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
using System.Runtime.CompilerServices;
using OpenRA.Primitives;
using OpenRA.Traits;

namespace OpenRA.GameSaves
{
	public static class SnapshotInits
	{
		public const string InitsKey = "Inits";

		const string LocationInitName = "Location";
		const string SubCellInitName = "SubCell";
		const string CenterPositionInitName = "CenterPosition";
		const string FacingInitName = "Facing";

		public static List<MiniYamlNode> Save(Actor actor)
		{
			ArgumentNullException.ThrowIfNull(actor);

			var ios = actor.OccupiesSpace;
			if (ios == null)
				return null;

			var nodes = new List<MiniYamlNode>
			{
				new(LocationInitName, FieldSaver.FormatValue(ios.TopLeft)),
				new(CenterPositionInitName, FieldSaver.FormatValue(ios.CenterPosition))
			};

			var occupied = ios.OccupiedCells();
			if (occupied.Length == 1 && occupied[0].SubCell != SubCell.Invalid && occupied[0].SubCell != SubCell.FullCell)
			{
				var subCell = ((int)occupied[0].SubCell).ToStringInvariant();
				nodes.Add(new MiniYamlNode(SubCellInitName, subCell));
			}

			var facing = actor.TraitOrDefault<IFacing>();
			if (facing != null)
				nodes.Add(new MiniYamlNode(FacingInitName, FieldSaver.FormatValue(facing.Facing)));

			return nodes;
		}

		public static void Load(World world, TypeDictionary inits, MiniYaml yaml)
		{
			ArgumentNullException.ThrowIfNull(world);
			ArgumentNullException.ThrowIfNull(inits);

			var node = yaml?.NodeWithKeyOrDefault(InitsKey);
			if (node == null)
				return;

			foreach (var init in node.Value.Nodes)
			{
				var type = world.ObjectCreator.FindType(init.Key + "Init");
				if (type == null)
					continue;

				inits.Add(Create(type, init.Key, init.Value));
			}
		}

		static ActorInit Create(Type type, string name, MiniYaml yaml)
		{
			var loader = type.GetMethod("Initialize", [typeof(MiniYaml)])
				?? throw new InvalidDataException($"{name}Init does not define a yaml-assignable type.");

			var init = (ActorInit)RuntimeHelpers.GetUninitializedObject(type);
			loader.Invoke(init, [yaml]);
			return init;
		}
	}
}
