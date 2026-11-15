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

using System.Diagnostics;

namespace OpenRA.Hosting
{
	public interface IRunTimeClock
	{
		long Milliseconds { get; }
	}

	public sealed class RealtimeClock : IRunTimeClock
	{
		// More accurate than Environment.TickCount.
		readonly Stopwatch stopwatch = Stopwatch.StartNew();

		public long Milliseconds => stopwatch.ElapsedMilliseconds;
	}
}
