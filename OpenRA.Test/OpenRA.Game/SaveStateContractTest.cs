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
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using OpenRA.Effects;
using OpenRA.GameSaves;
using OpenRA.Scripting;

namespace OpenRA.Test
{
	[TestFixture]
	sealed class SaveStateContractTest
	{
		static IEnumerable<Type> SaveStateTraits()
		{
			var assembly = Assembly.Load("OpenRA.Mods.Common");
			return assembly.GetTypes()
				.Where(t => !t.IsAbstract && !t.IsInterface && typeof(ISaveState).IsAssignableFrom(t))
				.OrderBy(t => t.FullName, StringComparer.Ordinal);
		}

		[TestCase(TestName = "Every ISaveState trait is discovered")]
		public void TraitsAreDiscovered()
		{
			Assert.That(SaveStateTraits().Any(), Is.True);
		}

		[TestCase(TestName = "Every ISaveState trait can name its info type")]
		public void TraitsDeclareInfo()
		{
			var missing = SaveStateTraits()
				.Where(t => t.GetInterfaceMap(typeof(ISaveState))
					.TargetMethods
					.All(m => m.Name != "get_SaveStateInfo" && !m.Name.EndsWith(".get_SaveStateInfo", StringComparison.Ordinal)))
				.Select(t => t.FullName)
				.ToList();

			Assert.That(missing, Is.Empty, "These traits implement ISaveState without a SaveStateInfo: " + missing.JoinWith(", "));
		}

		[TestCase(TestName = "Every trait that rebuilds wiring also saves state")]
		public void RestoredWiringIsDeclared()
		{
			var assembly = Assembly.Load("OpenRA.Mods.Common");
			var missing = assembly.GetTypes()
				.Where(t => !t.IsAbstract && !t.IsInterface && typeof(INotifyStateRestored).IsAssignableFrom(t)
					&& !typeof(ISaveState).IsAssignableFrom(t) && !typeof(IWorldSaveState).IsAssignableFrom(t))
				.Select(t => t.FullName)
				.OrderBy(n => n, StringComparer.Ordinal)
				.ToList();

			Assert.That(missing, Is.Empty,
				"These traits implement INotifyStateRestored without ISaveState or IWorldSaveState: " + missing.JoinWith(", "));
		}

		[TestCase(TestName = "Every ISync effect can be saved")]
		public void EveryISyncEffectIsSaveable()
		{
			var unsaveable = EffectAssemblies()
				.SelectMany(a => a.GetTypes())
				.Where(t => !t.IsAbstract && !t.IsInterface && typeof(IEffect).IsAssignableFrom(t)
					&& typeof(ISync).IsAssignableFrom(t)
					&& !t.IsDefined(typeof(SaveableEffectAttribute), false))
				.Select(t => t.FullName)
				.OrderBy(n => n, StringComparer.Ordinal)
				.ToList();

			Assert.That(unsaveable, Is.Empty,
				"These synced effects are not saveable, so dropping them would shift the sync hash: " +
				unsaveable.JoinWith(", "));
		}

		[TestCase(TestName = "Every synced member can be restored")]
		public void SyncedMembersAreRestorable()
		{
			var synced = AuditedAssemblies()
				.SelectMany(a => a.GetTypes())
				.Where(t => !t.IsAbstract && !t.IsInterface && typeof(ISync).IsAssignableFrom(t))
				.ToList();

			Assert.That(synced, Is.Not.Empty);

			var missing = synced
				.Where(t => !SaveStateAudit.Abstentions.ContainsKey(t.Name))
				.SelectMany(t => SaveStateAudit.UnsavedSyncedMembers(t).Select(m => $"{t.FullName}: {m}"))
				.OrderBy(n => n, StringComparer.Ordinal)
				.ToList();

			Assert.That(missing, Is.Empty,
				"These synced members are hashed but never saved, so a restore leaves them at a default " +
				"and the snapshot's own hash check fails: " + missing.JoinWith(", "));
		}

		[TestCase(TestName = "Every script property group names the object it belongs to")]
		public void PropertyGroupsNameTheirOwner()
		{
			var bases = new[]
			{
				typeof(ScriptActorProperties),
				typeof(ScriptPlayerProperties),
				typeof(ScriptProjectileProperties)
			};

			var groups = AuditedAssemblies()
				.SelectMany(a => a.GetTypes())
				.Where(t => !t.IsAbstract && bases.Any(b => b.IsAssignableFrom(t)))
				.ToList();

			Assert.That(groups, Is.Not.Empty);

			var missing = groups
				.Where(t => !typeof(IScriptMemberOwner).IsAssignableFrom(t))
				.Select(t => t.FullName)
				.OrderBy(n => n, StringComparer.Ordinal)
				.ToList();

			Assert.That(missing, Is.Empty,
				"These script property groups cannot name the object they belong to, so a bound method on " +
				"one of them refuses the save: " + missing.JoinWith(", "));
		}

		[TestCase(TestName = "The bound method the codec allows is the one the binding hands out")]
		public void BoundMethodNameMatchesTheBinding()
		{
			var invoke = typeof(ScriptMemberWrapper)
				.GetMethod("Invoke", BindingFlags.NonPublic | BindingFlags.Instance);

			Assert.That(invoke, Is.Not.Null,
				"ScriptMemberWrapper.Invoke is gone, so the codec's allowlist names a method that no " +
				"longer exists and every bound method refuses the save.");

			Assert.That(invoke.GetParameters().Length, Is.EqualTo(1),
				"Invoke no longer takes the single LuaVararg the binding calls it with.");
		}

		static IEnumerable<Assembly> AuditedAssemblies()
		{
			yield return typeof(World).Assembly;

			foreach (var assembly in EffectAssemblies())
				yield return assembly;
		}

		// The audits below cover the assemblies this build ships. Loading one that is absent throws
		// FileNotFoundException and reports as a broken contract, so a tier that carries fewer mods
		// must narrow this list rather than let it fail.
		static IEnumerable<Assembly> EffectAssemblies()
		{
			yield return Assembly.Load("OpenRA.Mods.Common");
			yield return Assembly.Load("OpenRA.Mods.Cnc");
		}
	}
}
