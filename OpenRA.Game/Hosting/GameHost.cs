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

#nullable enable

namespace OpenRA.Hosting
{
	public sealed class GameHost
	{
		public HostProfile Profile { get; }
		public IRunTimeClock Clock { get; }
		public IPresentation Presentation { get; }

		GameHost(HostProfile profile, IRunTimeClock clock, IPresentation presentation)
		{
			Profile = profile;
			Clock = clock;
			Presentation = presentation;
		}

		public static GameHost Create(HostProfile profile)
		{
			profile.Validate();

			IRunTimeClock clock = profile.Clock switch
			{
				ClockPortion.Realtime => new RealtimeClock(),
				ClockPortion.Stepped => new SteppedClock(),
				_ => throw new HostPortionException(profile, $"there is no {profile.Clock} clock."),
			};

			IPresentation presentation = profile.Presentation switch
			{
				PresentationPortion.Window => new Game.WindowPresentation(),
				PresentationPortion.None => new NoDisplayPresentation(),
				_ => throw new HostPortionException(profile, $"there is no {profile.Presentation} presentation."),
			};

			return new GameHost(profile, clock, presentation);
		}
	}
}
