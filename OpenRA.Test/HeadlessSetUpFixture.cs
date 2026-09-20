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
using System.IO;
using System.Runtime.CompilerServices;
using NUnit.Framework;

namespace OpenRA.Test
{
	[SetUpFixture]
	sealed class HeadlessSetUpFixture
	{
		string supportDir;

		[OneTimeSetUp]
		public void LoadMod()
		{
			supportDir = Path.Combine(Path.GetTempPath(), "openra-test-" + Guid.NewGuid().ToString("N"));
			Directory.CreateDirectory(supportDir);

			RuntimeHelpers.RunClassConstructor(typeof(Mods.Common.Traits.MobileInfo).TypeHandle);

			HeadlessGame.Initialize("ra", engineDir: "..", supportDir: supportDir);
		}

		[OneTimeTearDown]
		public void UnloadMod()
		{
			HeadlessGame.Shutdown();

			if (supportDir == null || !Directory.Exists(supportDir))
				return;

			try
			{
				Directory.Delete(supportDir, true);
			}
			catch (IOException)
			{
			}
			catch (UnauthorizedAccessException)
			{
			}
		}
	}
}
