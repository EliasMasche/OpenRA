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
using System.Linq;
using NUnit.Framework;
using OpenRA.Activities;
using OpenRA.GameSaves;

namespace OpenRA.Test
{
	#region Stub activities


	[SaveableActivity]
	sealed class CountingActivity : Activity
	{
		public int Value;

		public CountingActivity(int value) { Value = value; }

		internal CountingActivity(Actor _1, SnapshotReader _2, MiniYaml yaml)
		{
			Value = FieldLoader.GetValue<int>("Value", yaml.NodeWithKeyOrDefault("Value").Value.Value);
		}

		public override List<MiniYamlNode> SaveState(Actor self, SnapshotWriter w)
		{
			return [new("Value", FieldSaver.FormatValue(Value))];
		}
	}

	[SaveableActivity]
	sealed class ReferringActivity : Activity, IActivityReferences
	{
		public Activity Other;

		public ReferringActivity(Activity other) { Other = other; }

		internal ReferringActivity(Actor _1, SnapshotReader _2, MiniYaml _3) { }

		public override List<MiniYamlNode> SaveState(Actor self, SnapshotWriter w)
		{
			return [];
		}

		IEnumerable<(string Key, Activity Activity)> IActivityReferences.SaveReferences()
		{
			yield return ("Other", Other);
		}

		void IActivityReferences.LoadReference(string key, Activity activity)
		{
			if (key == "Other")
				Other = activity;
		}
	}

	sealed class UnsaveableActivity : Activity { }

	[SaveableActivity]
	sealed class MalformedActivity : Activity
	{
		public override List<MiniYamlNode> SaveState(Actor self, SnapshotWriter w)
		{
			return [];
		}
	}

	#endregion

	[TestFixture]
	sealed class ActivitySerializerTest
	{
		static ActivitySerializer Serializer()
		{
			return new ActivitySerializer(new ActivityRegistry(
				[typeof(CountingActivity), typeof(ReferringActivity)]));
		}

		static (List<MiniYamlNode> Saved, Activity Restored) RoundTrip(Activity root, ActivitySerializer s = null)
		{
			s ??= Serializer();

			var actor = (Actor)null;
			var saved = SaveTree(s, actor, root);
			if (saved == null)
				return (null, null);

			var yaml = new MiniYaml("", MiniYaml.FromString(saved.WriteToString(), "test").ToList());

			Activity restored = null;
			s.Restore(actor, yaml, Reader(), a => restored = a);
			return (saved, restored);
		}

		static List<MiniYamlNode> SaveTree(ActivitySerializer s, Actor actor, Activity root)
		{
			return s.Save(actor, root, Writer());
		}

		static SnapshotWriter Writer()
		{
			return new SnapshotWriter(new MemoryStream(), Header());
		}

		static SnapshotReader Reader()
		{
			var stream = new MemoryStream();
			using (var w = new SnapshotWriter(stream, Header()))
				w.WriteYamlSection("Empty", []);

			stream.Position = 0;
			return new SnapshotReader(stream);
		}

		static SnapshotHeader Header()
		{
			return new SnapshotHeader("test", "test", "1", "uid", 0, 0, 0, 0, 0, DateTime.UtcNow, SnapshotFlags.None);
		}

		[TestCase(TestName = "An activity's own state survives a round trip")]
		public void RoundTripsOwnState()
		{
			var (_, restored) = RoundTrip(new CountingActivity(42));

			Assert.That(restored, Is.TypeOf<CountingActivity>());
			Assert.That(((CountingActivity)restored).Value, Is.EqualTo(42));
		}

		[TestCase(TestName = "The child and next links are rebuilt in the shape they were saved in")]
		public void RoundTripsTreeShape()
		{
			var root = new CountingActivity(1);
			root.QueueChild(new CountingActivity(2));
			root.Queue(new CountingActivity(3));

			var (_, restored) = RoundTrip(root);

			Assert.That(Value(restored), Is.EqualTo(1));
			Assert.That(Value(restored.NextActivity), Is.EqualTo(3));

			int[] expected = [2, 1, 3];
			Assert.That(Values(restored), Is.EqualTo(expected));
		}

		[TestCase(TestName = "A tree containing an unsaveable activity is dropped whole")]
		public void DropsTreeWithUnsaveableNode()
		{
			var root = new CountingActivity(1);
			root.QueueChild(new UnsaveableActivity());

			var s = Serializer();
			Assert.That(SaveTree(s, null, root), Is.Null);
			Assert.That(s.DroppedTrees, Is.EqualTo(1));
		}

		[TestCase(TestName = "A reference to another activity resolves to that same instance")]
		public void ResolvesReferenceToSameInstance()
		{
			var target = new CountingActivity(7);
			var root = new ReferringActivity(target);
			root.QueueChild(target);

			var (_, restored) = RoundTrip(root);

			var child = restored.ActivitiesImplementing<CountingActivity>().Single();
			Assert.That(((ReferringActivity)restored).Other, Is.SameAs(child));
			Assert.That(child.Value, Is.EqualTo(7));
		}

		[TestCase(TestName = "An activity that never ran comes back Queued")]
		public void RoundTripsQueuedState()
		{
			var (_, restored) = RoundTrip(new CountingActivity(1));

			Assert.That(restored.State, Is.EqualTo(ActivityState.Queued));
		}

		[TestCase(TestName = "A registry rejects a saveable activity with no restore constructor")]
		public void RejectsMissingRestoreConstructor()
		{
			Assert.Throws<InvalidOperationException>(
				() => new ActivityRegistry([typeof(MalformedActivity)]));
		}

		[TestCase(TestName = "A link to a missing activity is corruption, not an absent reference")]
		public void RejectsDanglingLink()
		{
			var yaml = new MiniYaml("", MiniYaml.FromString(
				"1: CountingActivity\n\tValue: 1\n\tNext: 9\nRoot: 1", "test").ToList());

			Assert.Throws<InvalidDataException>(
				() => Serializer().Restore(null, yaml, Reader(), _ => { }));
		}

		[TestCase(TestName = "An activity saved as Active but never first-run is rejected")]
		public void RejectsInconsistentFirstRunState()
		{
			var yaml = new MiniYaml("", MiniYaml.FromString(
				"1: CountingActivity\n\tValue: 1\n\tState: Active\n\tFirstRunCompleted: false\nRoot: 1", "test").ToList());

			Assert.Throws<InvalidDataException>(
				() => Serializer().Restore(null, yaml, Reader(), _ => { }));
		}

		static int Value(Activity a)
		{
			return ((CountingActivity)a).Value;
		}

		static int[] Values(Activity root)
		{
			return root.ActivitiesImplementing<CountingActivity>().Select(a => a.Value).ToArray();
		}
	}
}
