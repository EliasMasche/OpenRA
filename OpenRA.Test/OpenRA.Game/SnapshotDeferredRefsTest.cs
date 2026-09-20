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
	sealed class SnapshotDeferredRefsTest
	{
		static SnapshotReader Reader()
		{
			var stream = new MemoryStream();
			using (var w = new SnapshotWriter(stream, new SnapshotHeader("test-engine", "ra", "1.0", "abcdef",
				42, 0x1234, 0, 0, 0, DateTime.UnixEpoch, SnapshotFlags.None)))
				w.WriteYamlSection("World", []);

			return new SnapshotReader(new MemoryStream(stream.ToArray()));
		}

		[TestCase(TestName = "Deferred references resolve only once RunDeferred is called")]
		public void DeferredReferencesResolveOnRunDeferred()
		{
			using (var r = Reader())
			{
				var resolved = 0;
				r.DeferActor("A:1", _ => resolved++);
				r.DeferActor("A:2", _ => resolved++);

				Assert.That(resolved, Is.Zero);

				r.RunDeferred();
				Assert.That(resolved, Is.EqualTo(2));
			}
		}

		[TestCase(TestName = "Deferred references resolve in the order they were queued")]
		public void DeferredReferencesResolveInOrder()
		{
			using (var r = Reader())
			{
				var order = new List<uint>();
				r.DeferActor("A:3", _ => order.Add(3));
				r.DeferActor("A:1", _ => order.Add(1));
				r.DeferActor("A:2", _ => order.Add(2));
				r.RunDeferred();

				Assert.That(order, Is.EqualTo(new uint[] { 3, 1, 2 }));
			}
		}

		[TestCase(TestName = "A dangling deferred reference resolves to null")]
		public void DanglingDeferredReferenceResolvesToNull()
		{
			using (var r = Reader())
			{
				var called = false;
				r.DeferActor("A:99", a =>
				{
					called = true;
					Assert.That(a, Is.Null);
				});

				r.RunDeferred();
				Assert.That(called, Is.True);
			}
		}

		[TestCase(TestName = "A null deferred reference never invokes its callback")]
		public void NullDeferredReferenceIsSkipped()
		{
			using (var r = Reader())
			{
				var called = false;
				r.DeferActor(SnapshotRefs.Null, _ => called = true);
				r.DeferActor(null, _ => called = true);
				r.DeferActor("", _ => called = true);
				r.RunDeferred();

				Assert.That(called, Is.False);
			}
		}

		[TestCase(TestName = "A malformed deferred reference throws when it is queued")]
		public void MalformedDeferredReferenceThrowsEarly()
		{
			using (var r = Reader())
			{
				void Act() => r.DeferActor("A:x", _ => { });
				Assert.That(Act, Throws.TypeOf<InvalidDataException>());
			}
		}

		[TestCase(TestName = "RunDeferred clears the queue")]
		public void RunDeferredClearsTheQueue()
		{
			using (var r = Reader())
			{
				var resolved = 0;
				r.DeferActor("A:1", _ => resolved++);
				r.RunDeferred();
				r.RunDeferred();

				Assert.That(resolved, Is.EqualTo(1));
			}
		}

		[TestCase(TestName = "Deferring requires a callback")]
		public void DeferRequiresCallback()
		{
			using (var r = Reader())
			{
				void Act() => r.DeferActor("A:1", null);
				Assert.That(Act, Throws.TypeOf<ArgumentNullException>());
			}
		}

		[TestCase(TestName = "A reader without a world resolves every reference to null")]
		public void ReaderWithoutWorldResolvesToNull()
		{
			using (var r = Reader())
			{
				Assert.That(r.World, Is.Null);
				Assert.That(r.ResolveActor("A:1"), Is.Null);
				Assert.That(r.ResolvePlayer("P:Multi0"), Is.Null);
				Assert.That(r.ResolveTarget("A:1:0").Type, Is.EqualTo(OpenRA.Traits.TargetType.Invalid));
			}
		}
	}
}
