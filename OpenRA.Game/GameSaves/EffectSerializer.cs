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
using System.Linq;
using OpenRA.Effects;

namespace OpenRA.GameSaves
{
	/// <summary>Writes the effects of a world in order, and rebuilds them during a restore.</summary>
	/// <remarks>
	/// Position is identity for an effect, so the save skips an effect that is not saveable and counts the
	/// drop. The restore adds each effect and then, after the deferred references resolve, removes an
	/// effect that still cannot reach what it needs. The command <c>--check-effect-restore</c> reports a
	/// dropped synced effect, because it shifts the hash of every effect after it.
	/// </remarks>
	public sealed class EffectSerializer
	{
		readonly EffectRegistry registry;

		public int DroppedEffects { get; private set; }

		public EffectSerializer(EffectRegistry registry)
		{
			ArgumentNullException.ThrowIfNull(registry);

			this.registry = registry;
		}

		public List<MiniYamlNode> Save(World world, SnapshotWriter w)
		{
			ArgumentNullException.ThrowIfNull(world);
			ArgumentNullException.ThrowIfNull(w);

			var nodes = new List<MiniYamlNode>();
			foreach (var effect in world.Effects)
			{
				if (effect is not ISaveableEffect saveable || !registry.IsSaveable(effect))
				{
					DroppedEffects++;
					continue;
				}

				var state = saveable.SaveState(world, w);
				if (state == null)
				{
					DroppedEffects++;
					continue;
				}

				nodes.Add(new MiniYamlNode(nodes.Count.ToStringInvariant(), EffectRegistry.NameOf(effect), state));
			}

			return nodes;
		}

		public void Restore(World world, IEnumerable<MiniYamlNode> nodes, SnapshotReader r)
		{
			ArgumentNullException.ThrowIfNull(world);
			ArgumentNullException.ThrowIfNull(nodes);
			ArgumentNullException.ThrowIfNull(r);

			var restored = new List<IEffect>();
			foreach (var node in nodes.OrderBy(n => ParseIndex(n.Key)))
			{
				var effect = registry.Create(node.Value.Value, world, r, node.Value);
				world.Add(effect);
				restored.Add(effect);
			}

			// The check waits for the deferred references, because an effect cannot
			// know yet whether it can reach what it needs.
			r.DeferCompleted(() =>
			{
				foreach (var effect in restored)
				{
					if (effect is IRequiresRestoredReferences e && !e.ReferencesRestored)
					{
						world.Remove(effect);
						DroppedEffects++;
					}
				}
			});
		}

		static int ParseIndex(string key)
		{
			if (!int.TryParse(key, System.Globalization.NumberStyles.None, System.Globalization.NumberFormatInfo.InvariantInfo, out var index))
				throw new InvalidDataException($"Malformed saved effect index '{key}'.");

			return index;
		}
	}
}
