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
using System.Linq;
using NUnit.Framework;
using OpenRA.Activities;
using OpenRA.GameSaves;

namespace OpenRA.Test
{
	[SaveableActivity]
	sealed class Outer : Activity
	{
		internal Outer(Actor _1, SnapshotReader _2, MiniYaml _3) { }

		[SaveableActivity]
		internal sealed class Inner : Activity
		{
			internal Inner(Actor _1, SnapshotReader _2, MiniYaml _3) { }
		}
	}

	[TestFixture]
	sealed class ActivityRegistryTest
	{
		[TestCase(TestName = "A top-level activity is named by its type alone")]
		public void NamesTopLevelActivity()
		{
			Assert.That(ActivityRegistry.NameOf(typeof(Outer)), Is.EqualTo("Outer"));
		}

		[TestCase(TestName = "A nested activity is named by its declaring type as well")]
		public void NamesNestedActivity()
		{
			Assert.That(ActivityRegistry.NameOf(typeof(Outer.Inner)), Is.EqualTo("Outer+Inner"));
		}

		[TestCase(TestName = "A registry finds a private nested activity")]
		public void FindsNestedActivity()
		{
			var registry = new ActivityRegistry([typeof(Outer), typeof(Outer.Inner)]);

			Assert.That(registry.IsSaveable(typeof(Outer.Inner)), Is.True);
		}

		[TestCase(TestName = "An activity that is not registered is not saveable")]
		public void UnregisteredActivityIsNotSaveable()
		{
			var registry = new ActivityRegistry([typeof(Outer)]);

			Assert.That(registry.IsSaveable(typeof(Outer.Inner)), Is.False);
			Assert.That(registry.IsSaveable((Activity)null), Is.False);
			Assert.That(registry.IsSaveable((Type)null), Is.False);
		}

		[TestCase(TestName = "Two activities sharing a saved name are rejected")]
		public void RejectsDuplicateNames()
		{
			Assert.Throws<InvalidOperationException>(
				() => new ActivityRegistry([typeof(Outer), typeof(Outer)]));
		}

		[TestCase(TestName = "The restore constructor is found even though it is private")]
		public void FindsPrivateRestoreConstructor()
		{
			Assert.That(ActivityRegistry.RestoreCtor(typeof(Outer)), Is.Not.Null);
			Assert.That(ActivityRegistry.RestoreCtor(typeof(Activity)), Is.Null);
		}

		[TestCase(TestName = "The restore constructor signature is the documented one")]
		public void RestoreConstructorSignatureIsFixed()
		{
			Assert.That(
				ActivityRegistry.RestoreCtorArgs,
				Is.EqualTo(new[] { typeof(Actor), typeof(SnapshotReader), typeof(MiniYaml) }));

			var parameters = ActivityRegistry.RestoreCtor(typeof(Outer)).GetParameters().Select(p => p.ParameterType);
			Assert.That(parameters, Is.EqualTo(ActivityRegistry.RestoreCtorArgs));
		}
	}
}
