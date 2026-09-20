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
using OpenRA.GameSaves;
using OpenRA.Primitives;
using OpenRA.Traits;

namespace OpenRA.Mods.Common.Activities
{
	public static class LastVisibleTargetState
	{
		public const string TargetKey = "Target";
		public const string LastVisibleTargetKey = "LastVisibleTarget";
		public const string UseLastVisibleTargetKey = "UseLastVisibleTarget";
		public const string LastVisibleMinimumRangeKey = "LastVisibleMinimumRange";
		public const string LastVisibleMaximumRangeKey = "LastVisibleMaximumRange";
		public const string LastVisibleOwnerKey = "LastVisibleOwner";
		public const string LastVisibleTargetTypesKey = "LastVisibleTargetTypes";

		public static void Save(List<MiniYamlNode> nodes, SnapshotWriter w, in Target target, in Target lastVisibleTarget,
			bool useLastVisibleTarget, WDist lastVisibleMinimumRange, WDist lastVisibleMaximumRange,
			Player lastVisibleOwner, BitSet<TargetableType> lastVisibleTargetTypes)
		{
			nodes.Add(new MiniYamlNode(TargetKey, w.TargetRef(target)));
			nodes.Add(new MiniYamlNode(LastVisibleTargetKey, w.TargetRef(lastVisibleTarget)));
			nodes.Add(new MiniYamlNode(UseLastVisibleTargetKey, FieldSaver.FormatValue(useLastVisibleTarget)));
			nodes.Add(new MiniYamlNode(LastVisibleMinimumRangeKey, FieldSaver.FormatValue(lastVisibleMinimumRange)));
			nodes.Add(new MiniYamlNode(LastVisibleMaximumRangeKey, FieldSaver.FormatValue(lastVisibleMaximumRange)));
			nodes.Add(new MiniYamlNode(LastVisibleOwnerKey, w.PlayerRef(lastVisibleOwner)));
			nodes.Add(new MiniYamlNode(LastVisibleTargetTypesKey, FieldSaver.FormatValue(lastVisibleTargetTypes)));
		}

		public static bool UseLastVisible(Dictionary<string, MiniYaml> nodes)
		{
			return FieldLoader.GetValue<bool>(UseLastVisibleTargetKey, nodes[UseLastVisibleTargetKey].Value);
		}

		public static WDist MinimumRange(Dictionary<string, MiniYaml> nodes)
		{
			return FieldLoader.GetValue<WDist>(LastVisibleMinimumRangeKey, nodes[LastVisibleMinimumRangeKey].Value);
		}

		public static WDist MaximumRange(Dictionary<string, MiniYaml> nodes)
		{
			return FieldLoader.GetValue<WDist>(LastVisibleMaximumRangeKey, nodes[LastVisibleMaximumRangeKey].Value);
		}

		public static Player Owner(Dictionary<string, MiniYaml> nodes, SnapshotReader r)
		{
			return r.ResolvePlayer(nodes[LastVisibleOwnerKey].Value);
		}

		public static BitSet<TargetableType> TargetTypes(Dictionary<string, MiniYaml> nodes)
		{
			var value = nodes[LastVisibleTargetTypesKey].Value;
			return string.IsNullOrEmpty(value)
				? default
				: FieldLoader.GetValue<BitSet<TargetableType>>(LastVisibleTargetTypesKey, value);
		}
	}
}
