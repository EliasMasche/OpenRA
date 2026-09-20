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
using System.IO;
using NUnit.Framework;
using OpenRA.GameSaves;

namespace OpenRA.Test
{
	[TestFixture]
	sealed class SnapshotTraitKeyTest
	{
		static IEnumerable<TestCaseData> RoundTripTestCases()
		{
			return
			[
				new TestCaseData(0u, "Selection", null).SetName("Unnamed"),
				new TestCaseData(1u, "ControlGroups", "").SetName("Empty instance name"),
				new TestCaseData(42u, "Armament", "primary").SetName("Named instance"),
				new TestCaseData(uint.MaxValue, "WithSpriteBody", "body").SetName("Maximum actor id"),
			];
		}

		[TestCaseSource(nameof(RoundTripTestCases))]
		public void RoundTrip(uint actorID, string traitName, string instanceName)
		{
			var key = new SnapshotTraitKey(actorID, traitName, instanceName);
			var parsed = SnapshotTraitKey.Parse(key.ToString());

			Assert.That(parsed, Is.EqualTo(key));
			Assert.That(parsed.ActorID, Is.EqualTo(actorID));
			Assert.That(parsed.TraitName, Is.EqualTo(traitName));
			Assert.That(parsed.ToString(), Is.EqualTo(key.ToString()));
		}

		[TestCase(TestName = "An empty instance name is equivalent to an absent one")]
		public void EmptyInstanceNameIsAbsent()
		{
			var unnamed = new SnapshotTraitKey(3, "Selection");
			var empty = new SnapshotTraitKey(3, "Selection", "");

			Assert.That(empty, Is.EqualTo(unnamed));
			Assert.That(empty.InstanceName, Is.Null);
			Assert.That(empty.ToString(), Is.EqualTo("3/Selection"));
		}

		[TestCase(TestName = "Keys format as ActorID/Trait[@Instance]")]
		public void FormatsAsDocumented()
		{
			Assert.That(new SnapshotTraitKey(7, "Armament").ToString(), Is.EqualTo("7/Armament"));
			Assert.That(new SnapshotTraitKey(7, "Armament", "primary").ToString(), Is.EqualTo("7/Armament@primary"));
		}

		[TestCase(TestName = "Keys differing only by instance name are distinct")]
		public void InstanceNameDistinguishesKeys()
		{
			var primary = new SnapshotTraitKey(7, "Armament", "primary");
			var secondary = new SnapshotTraitKey(7, "Armament", "secondary");

			Assert.That(primary, Is.Not.EqualTo(secondary));
			Assert.That(primary == secondary, Is.False);
			Assert.That(primary != secondary, Is.True);
		}

		static IEnumerable<TestCaseData> MalformedTestCases()
		{
			return
			[
				new TestCaseData(null).SetName("Null"),
				new TestCaseData("").SetName("Empty"),
				new TestCaseData("Selection").SetName("No actor id"),
				new TestCaseData("/Selection").SetName("Empty actor id"),
				new TestCaseData("1/").SetName("Empty trait name"),
				new TestCaseData("x/Selection").SetName("Non-numeric actor id"),
				new TestCaseData("-1/Selection").SetName("Negative actor id"),
				new TestCaseData(" 1/Selection").SetName("Leading whitespace"),
				new TestCaseData("1/a/b").SetName("Second actor separator"),
				new TestCaseData("1/@primary").SetName("Empty trait name with instance"),
				new TestCaseData("1/Armament@").SetName("Empty instance name"),
				new TestCaseData("1/Armament@a@b").SetName("Second instance separator"),
			];
		}

		[TestCaseSource(nameof(MalformedTestCases))]
		public void RejectsMalformedKeys(string key)
		{
			Assert.That(SnapshotTraitKey.TryParse(key, out _), Is.False);

			void Act() => SnapshotTraitKey.Parse(key);
			Assert.That(Act, Throws.TypeOf<InvalidDataException>());
		}

		[TestCase(TestName = "Constructing a key rejects separators in its parts")]
		public void RejectsSeparatorsInParts()
		{
			static void SlashInTrait() => new SnapshotTraitKey(1, "a/b");
			static void AtInTrait() => new SnapshotTraitKey(1, "a@b");
			static void AtInInstance() => new SnapshotTraitKey(1, "Armament", "a@b");
			static void EmptyTrait() => new SnapshotTraitKey(1, "");

			Assert.That(SlashInTrait, Throws.TypeOf<ArgumentException>());
			Assert.That(AtInTrait, Throws.TypeOf<ArgumentException>());
			Assert.That(AtInInstance, Throws.TypeOf<ArgumentException>());
			Assert.That(EmptyTrait, Throws.TypeOf<ArgumentException>());
		}

		[TestCase(TestName = "Keys are usable as dictionary keys")]
		public void UsableAsDictionaryKey()
		{
			var data = new Dictionary<SnapshotTraitKey, string>
			{
				[new SnapshotTraitKey(1, "Armament", "primary")] = "first",
				[new SnapshotTraitKey(1, "Armament", "secondary")] = "second",
			};

			Assert.That(data[SnapshotTraitKey.Parse("1/Armament@primary")], Is.EqualTo("first"));
			Assert.That(data[SnapshotTraitKey.Parse("1/Armament@secondary")], Is.EqualTo("second"));
			Assert.That(data.ContainsKey(SnapshotTraitKey.Parse("1/Armament")), Is.False);
		}
	}
}
