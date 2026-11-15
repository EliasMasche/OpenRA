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

using System;

namespace OpenRA.Hosting
{
	/// <summary>
	/// Time that moves only when the runner advances it, so a run does not depend on the wall clock.
	/// </summary>
	public sealed class SteppedClock : IRunTimeClock
	{
		public long Milliseconds { get; private set; }

		public void Advance(long milliseconds)
		{
			ArgumentOutOfRangeException.ThrowIfNegative(milliseconds);
			Milliseconds += milliseconds;
		}
	}
}
