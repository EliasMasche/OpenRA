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

using NUnit.Framework;
using OpenRA.Hosting;

namespace OpenRA.Test
{
	/// <summary>
	/// Game.Host is process-wide, so the fixtures in this assembly run under the Tooling profile, which is what
	/// OpenRA.Utility runs. Game.RunTime reads the clock of the host.
	/// </summary>
	[SetUpFixture]
	sealed class TestHostSetup
	{
		[OneTimeSetUp]
		public void CreateHost()
		{
			Game.InitializeHost(HostProfile.Tooling);
		}
	}
}
