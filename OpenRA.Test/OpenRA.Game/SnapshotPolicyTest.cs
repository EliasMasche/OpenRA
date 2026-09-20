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
using OpenRA.GameSaves;

namespace OpenRA.Test
{
	[TestFixture]
	sealed class SnapshotPolicyTest
	{
		[TestCase(true, TestName = "A dedicated server offers saves when its operator enables them")]
		[TestCase(false, TestName = "A dedicated server refuses saves when its operator disables them")]
		public void DedicatedFollowsTheOperatorSetting(bool enabled)
		{
			Assert.That(SnapshotPolicy.ServerOffersGameSaves(isDedicated: true, enabledForDedicated: enabled), Is.EqualTo(enabled));
		}

		[TestCase(true, TestName = "A player-hosted server offers saves although the setting is on")]
		[TestCase(false, TestName = "A player-hosted server offers saves although the setting is off")]
		public void OnlyDedicatedReadsTheSetting(bool enabled)
		{
			Assert.That(SnapshotPolicy.ServerOffersGameSaves(isDedicated: false, enabledForDedicated: enabled), Is.True);
		}
	}
}
