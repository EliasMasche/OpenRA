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
using NUnit.Framework;
using OpenRA.GameSaves;

namespace OpenRA.Test
{
	[TestFixture]
	sealed class SavePathsTest
	{
		[TestCase("quicksave.orasav", "quicksave.orasav", TestName = "An ordinary name is unchanged")]
		[TestCase("a/b.orasav", "ab.orasav", TestName = "Separators are stripped")]
		[TestCase("a\\b.orasav", "ab.orasav", TestName = "Backslashes are stripped")]
		[TestCase("save .orasav", "save .orasav", TestName = "A name containing dots is kept")]
		public void SanitizesNames(string input, string expected)
		{
			Assert.That(SavePaths.SanitizeFileName(input), Is.EqualTo(expected));
		}

		[TestCase("..", TestName = "The parent directory is refused")]
		[TestCase(".", TestName = "The current directory is refused")]
		[TestCase("...", TestName = "A name of only dots is refused")]
		[TestCase("", TestName = "An empty name is refused")]
		[TestCase("/", TestName = "A name that is empty after stripping is refused")]
		public void RefusesNamesThatAreNotFiles(string input)
		{
			Assert.Throws<ArgumentException>(() => SavePaths.SanitizeFileName(input));

			Assert.That(SavePaths.TrySanitizeFileName(input, out _), Is.False);
		}

		[TestCase(TestName = "TrySanitizeFileName reports success for a usable name")]
		public void TrySanitizeAcceptsUsableNames()
		{
			Assert.That(SavePaths.TrySanitizeFileName("quicksave.orasav", out var sanitized), Is.True);
			Assert.That(sanitized, Is.EqualTo("quicksave.orasav"));
		}
	}
}
