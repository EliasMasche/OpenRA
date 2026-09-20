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
using OpenRA.Graphics;

namespace OpenRA.GameSaves
{
	public static class AnimationCodec
	{
		public const string SequenceKey = "Sequence";
		public const string FrameKey = "Frame";
		public const string BackwardsKey = "Backwards";
		public const string TickAlwaysKey = "TickAlways";
		public const string TimeUntilNextFrameKey = "TimeUntilNextFrame";
		public const string FinishedKey = "Finished";

		public static List<MiniYamlNode> Save(Animation anim)
		{
			var state = anim?.SaveSequenceState();
			if (state == null)
				return null;

			var s = state.Value;
			return
			[
				new(SequenceKey, s.Sequence),
				new(FrameKey, FieldSaver.FormatValue(s.Frame)),
				new(BackwardsKey, FieldSaver.FormatValue(s.Backwards)),
				new(TickAlwaysKey, FieldSaver.FormatValue(s.TickAlways)),
				new(TimeUntilNextFrameKey, FieldSaver.FormatValue(s.TimeUntilNextFrame)),
				new(FinishedKey, FieldSaver.FormatValue(s.Finished))
			];
		}

		public static Animation.SequenceState Load(MiniYaml yaml)
		{
			ArgumentNullException.ThrowIfNull(yaml);

			var nodes = yaml.ToDictionary();

			return new Animation.SequenceState(
				nodes[SequenceKey].Value,
				FieldLoader.GetValue<int>(FrameKey, nodes[FrameKey].Value),
				FieldLoader.GetValue<bool>(BackwardsKey, nodes[BackwardsKey].Value),
				FieldLoader.GetValue<bool>(TickAlwaysKey, nodes[TickAlwaysKey].Value),
				FieldLoader.GetValue<int>(TimeUntilNextFrameKey, nodes[TimeUntilNextFrameKey].Value),
				FieldLoader.GetValue<bool>(FinishedKey, nodes[FinishedKey].Value));
		}
	}
}
