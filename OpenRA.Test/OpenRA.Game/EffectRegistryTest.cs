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
using OpenRA.Effects;
using OpenRA.GameSaves;
using OpenRA.Graphics;

namespace OpenRA.Test
{
	[SaveableEffect]
	sealed class OuterEffect : ISaveableEffect
	{
		internal OuterEffect(World _1, SnapshotReader _2, MiniYaml _3) { }

		public void Tick(World world) { }
		public IEnumerable<IRenderable> Render(WorldRenderer wr) => [];
		public List<MiniYamlNode> SaveState(World world, SnapshotWriter w) => [];

		[SaveableEffect]
		internal sealed class InnerEffect : ISaveableEffect
		{
			internal InnerEffect(World _1, SnapshotReader _2, MiniYaml _3) { }

			public void Tick(World world) { }
			public IEnumerable<IRenderable> Render(WorldRenderer wr) => [];
			public List<MiniYamlNode> SaveState(World world, SnapshotWriter w) => [];
		}
	}

	[SaveableEffect]
	sealed class MalformedEffect : ISaveableEffect
	{
		public void Tick(World world) { }
		public IEnumerable<IRenderable> Render(WorldRenderer wr) => [];
		public List<MiniYamlNode> SaveState(World world, SnapshotWriter w) => [];
	}

	[TestFixture]
	sealed class EffectRegistryTest
	{
		[TestCase(TestName = "A top-level effect is named by its type alone")]
		public void NamesTopLevelEffect()
		{
			Assert.That(EffectRegistry.NameOf(typeof(OuterEffect)), Is.EqualTo("OuterEffect"));
		}

		[TestCase(TestName = "A nested effect is named by its declaring type as well")]
		public void NamesNestedEffect()
		{
			Assert.That(EffectRegistry.NameOf(typeof(OuterEffect.InnerEffect)), Is.EqualTo("OuterEffect+InnerEffect"));
		}

		[TestCase(TestName = "A registry finds a non-public nested effect")]
		public void FindsNonPublicRestoreConstructor()
		{
			var registry = new EffectRegistry([typeof(OuterEffect), typeof(OuterEffect.InnerEffect)]);

			Assert.That(registry.IsSaveable(typeof(OuterEffect.InnerEffect)), Is.True);
		}

		[TestCase(TestName = "An effect that is not registered is not saveable")]
		public void UnregisteredEffectIsNotSaveable()
		{
			var registry = new EffectRegistry([typeof(OuterEffect)]);

			Assert.That(registry.IsSaveable(typeof(OuterEffect.InnerEffect)), Is.False);
			Assert.That(registry.IsSaveable((IEffect)null), Is.False);
			Assert.That(registry.IsSaveable((Type)null), Is.False);
		}

		[TestCase(TestName = "Two effects sharing a saved name are rejected")]
		public void RejectsDuplicateNames()
		{
			Assert.Throws<InvalidOperationException>(
				() => new EffectRegistry([typeof(OuterEffect), typeof(OuterEffect)]));
		}

		[TestCase(TestName = "An effect marked saveable without a restore constructor is rejected")]
		public void RejectsMissingRestoreConstructor()
		{
			Assert.Throws<InvalidOperationException>(
				() => new EffectRegistry([typeof(MalformedEffect)]));
		}

		[TestCase(TestName = "Restoring an unknown effect name is a data error")]
		public void CreateThrowsForUnknownName()
		{
			var registry = new EffectRegistry([typeof(OuterEffect)]);

			Assert.Throws<InvalidDataException>(
				() => registry.Create("NoSuchEffect", null, null, new MiniYaml("")));
		}

		[TestCase(TestName = "The restore constructor is found even though it is private")]
		public void FindsPrivateRestoreConstructor()
		{
			Assert.That(EffectRegistry.RestoreCtor(typeof(OuterEffect)), Is.Not.Null);
			Assert.That(EffectRegistry.RestoreCtor(typeof(MalformedEffect)), Is.Null);
		}

		[TestCase(TestName = "The restore constructor signature is the documented one")]
		public void RestoreConstructorSignatureIsFixed()
		{
			Assert.That(
				EffectRegistry.RestoreCtorArgs,
				Is.EqualTo(new[] { typeof(World), typeof(SnapshotReader), typeof(MiniYaml) }));

			var parameters = EffectRegistry.RestoreCtor(typeof(OuterEffect)).GetParameters().Select(p => p.ParameterType);
			Assert.That(parameters, Is.EqualTo(EffectRegistry.RestoreCtorArgs));
		}
	}
}
