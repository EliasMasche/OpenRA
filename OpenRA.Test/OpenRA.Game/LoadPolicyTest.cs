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
	sealed class LoadPolicyTest
	{
		const string Exodus = "8a5357e175a451edae46be562a9527276ff7c2d1";
		const string Other = "36678d607706730452136d1f04286dabeb6b824c";

		[TestCase(TestName = "A save for the session's own map may be restored")]
		public void SameMapIsAllowed()
		{
			Assert.That(LoadPolicy.CanRestoreInto(Exodus, Exodus, saveMapAvailable: true),
				Is.EqualTo(LoadRefusal.None));
		}

		[TestCase(TestName = "A save for a different map may not be restored into a session")]
		public void DifferentMapIsRefused()
		{
			Assert.That(LoadPolicy.CanRestoreInto(Other, Exodus, saveMapAvailable: true),
				Is.EqualTo(LoadRefusal.DifferentMap));
		}

		[TestCase(TestName = "A save whose map is missing is refused for that reason, not for a mismatch")]
		public void UnavailableMapIsRefusedFirst()
		{
			Assert.That(LoadPolicy.CanRestoreInto(Other, Exodus, saveMapAvailable: false),
				Is.EqualTo(LoadRefusal.MapUnavailable));

			Assert.That(LoadPolicy.CanRestoreInto(Exodus, Exodus, saveMapAvailable: false),
				Is.EqualTo(LoadRefusal.MapUnavailable));
		}

		[TestCase(TestName = "With no session to protect, any available save may be restored")]
		public void NoSessionAllowsAnyMap()
		{
			Assert.That(LoadPolicy.CanRestoreInto(Exodus, null, saveMapAvailable: true),
				Is.EqualTo(LoadRefusal.None));

			Assert.That(LoadPolicy.CanRestoreInto(Exodus, "", saveMapAvailable: true),
				Is.EqualTo(LoadRefusal.None));
		}

		[TestCase(TestName = "Map identity is compared exactly, not by prefix or case")]
		public void IdentityIsExact()
		{
			Assert.That(LoadPolicy.CanRestoreInto(Exodus, Exodus[..^1], saveMapAvailable: true),
				Is.EqualTo(LoadRefusal.DifferentMap));

			Assert.That(LoadPolicy.CanRestoreInto(Exodus, Exodus.ToUpperInvariant(), saveMapAvailable: true),
				Is.EqualTo(LoadRefusal.DifferentMap));
		}
	}

	[TestFixture]
	sealed class LoadSeatingTest
	{
		[TestCase(TestName = "A load while a game is running keeps every client where it is")]
		public void RunningGamePreservesSeating()
		{
			Assert.That(LoadSeating.Plan(gameRunning: true, humanCount: 1),
				Is.EqualTo(SeatingPlan.PreserveCurrent));

			Assert.That(LoadSeating.Plan(gameRunning: true, humanCount: 2),
				Is.EqualTo(SeatingPlan.PreserveCurrent));

			Assert.That(LoadSeating.Plan(gameRunning: true, humanCount: 8),
				Is.EqualTo(SeatingPlan.PreserveCurrent));
		}

		[TestCase(TestName = "A load with no game running rebuilds its seating from the save")]
		public void LobbyLoadSeatsFromSave()
		{
			Assert.That(LoadSeating.Plan(gameRunning: false, humanCount: 0),
				Is.EqualTo(SeatingPlan.FromSave));

			Assert.That(LoadSeating.Plan(gameRunning: false, humanCount: 1),
				Is.EqualTo(SeatingPlan.FromSave));

			Assert.That(LoadSeating.Plan(gameRunning: false, humanCount: 4),
				Is.EqualTo(SeatingPlan.FromSave));
		}

		[TestCase(TestName = "A negative human count is rejected rather than treated as zero")]
		public void NegativeHumanCountIsRejected()
		{
			Assert.Throws<System.ArgumentOutOfRangeException>(
				() => LoadSeating.Plan(gameRunning: false, humanCount: -1));
		}
	}
}
