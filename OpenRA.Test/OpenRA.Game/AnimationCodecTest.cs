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

using System.Linq;
using NUnit.Framework;
using OpenRA.GameSaves;
using OpenRA.Graphics;

namespace OpenRA.Test
{
	[TestFixture]
	sealed class AnimationCodecTest
	{
		[TestCase(TestName = "Animation state round trips through yaml")]
		public void StateRoundTrips()
		{
			var state = new Animation.SequenceState("idle", 3, true, true, 17, false);

			Assert.That(RoundTrip(state), Is.EqualTo(state));
		}

		[TestCase(TestName = "A finished animation is restored as finished")]
		public void FinishedRoundTrips()
		{
			var state = new Animation.SequenceState("idle", 5, false, false, 0, true);

			Assert.That(RoundTrip(state).Finished, Is.True,
				"A completed animation came back as one still to complete, so its completion would run twice.");
		}

		[TestCase(TestName = "An animation with no sequence saves as null")]
		public void NoSequenceSavesNull()
		{
			Assert.That(AnimationCodec.Save(null), Is.Null);
		}

		static Animation.SequenceState RoundTrip(in Animation.SequenceState state)
		{
			var nodes = new[]
			{
				new MiniYamlNode(AnimationCodec.SequenceKey, state.Sequence),
				new MiniYamlNode(AnimationCodec.FrameKey, FieldSaver.FormatValue(state.Frame)),
				new MiniYamlNode(AnimationCodec.BackwardsKey, FieldSaver.FormatValue(state.Backwards)),
				new MiniYamlNode(AnimationCodec.TickAlwaysKey, FieldSaver.FormatValue(state.TickAlways)),
				new MiniYamlNode(AnimationCodec.TimeUntilNextFrameKey, FieldSaver.FormatValue(state.TimeUntilNextFrame)),
				new MiniYamlNode(AnimationCodec.FinishedKey, FieldSaver.FormatValue(state.Finished))
			};

			var text = new[] { new MiniYamlNode("Animation", new MiniYaml("", nodes)) }.WriteToString();
			var yaml = MiniYaml.FromString(text, "test").First().Value;

			return AnimationCodec.Load(yaml);
		}
	}
}
