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
using System.Reflection;
using OpenRA.Activities;

namespace OpenRA.GameSaves
{
	/// <summary>Finds the saveable activities of a mod and creates them during a restore.</summary>
	/// <remarks>
	/// A type qualifies when it carries <see cref="SaveableActivityAttribute"/> and has a restore
	/// constructor. That constructor takes an <see cref="Actor"/>, a <see cref="SnapshotReader"/>, and a
	/// <see cref="MiniYaml"/>. The registry refuses a marked type without such a constructor, so the
	/// error appears at startup and not in the middle of a restore.
	/// </remarks>
	public sealed class ActivityRegistry
	{
		public static readonly Type[] RestoreCtorArgs = [typeof(Actor), typeof(SnapshotReader), typeof(MiniYaml)];

		const BindingFlags CtorFlags = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance;

		readonly Dictionary<string, ConstructorInfo> ctors = [];

		public ActivityRegistry(ObjectCreator objectCreator)
			: this(SaveableActivityTypes(objectCreator)) { }

		public ActivityRegistry(IEnumerable<Type> types)
		{
			ArgumentNullException.ThrowIfNull(types);

			foreach (var type in types)
			{
				var ctor = RestoreCtor(type);
				if (ctor == null)
					throw new InvalidOperationException(
						$"Activity {type.FullName} is marked [SaveableActivity] but has no restore constructor. " +
						"Run --check-activity-restore for the full list.");

				if (!ctors.TryAdd(NameOf(type), ctor))
					throw new InvalidOperationException($"Two saveable activities are both named '{NameOf(type)}'.");
			}
		}

		public static string NameOf(Type type)
		{
			ArgumentNullException.ThrowIfNull(type);

			return type.IsNested ? type.DeclaringType.Name + "+" + type.Name : type.Name;
		}

		public static string NameOf(Activity activity)
		{
			ArgumentNullException.ThrowIfNull(activity);

			return NameOf(activity.GetType());
		}

		public static ConstructorInfo RestoreCtor(Type type)
		{
			ArgumentNullException.ThrowIfNull(type);

			return type.GetConstructor(CtorFlags, null, RestoreCtorArgs, null);
		}

		public static IEnumerable<Type> SaveableActivityTypes(ObjectCreator objectCreator)
		{
			return ActivityTypes(objectCreator).Where(t => t.IsDefined(typeof(SaveableActivityAttribute), false));
		}

		public static IEnumerable<Type> ActivityTypes(ObjectCreator objectCreator)
		{
			ArgumentNullException.ThrowIfNull(objectCreator);

			return objectCreator.GetTypes()
				.Where(t => !t.IsAbstract && typeof(Activity).IsAssignableFrom(t))
				.OrderBy(t => t.FullName, StringComparer.Ordinal);
		}

		public bool IsSaveable(Activity activity)
		{
			return activity != null && IsSaveable(activity.GetType());
		}

		public bool IsSaveable(Type type)
		{
			return type != null && ctors.ContainsKey(NameOf(type));
		}

		public Activity Create(string name, Actor self, SnapshotReader r, MiniYaml yaml)
		{
			if (!ctors.TryGetValue(name, out var ctor))
				throw new InvalidDataException($"Snapshot names an unknown activity '{name}'.");

			try
			{
				return (Activity)ctor.Invoke([self, r, yaml]);
			}
			catch (TargetInvocationException e) when (e.InnerException != null)
			{
				throw new InvalidDataException($"Failed to restore activity '{name}' on {self}: {e.InnerException.Message}", e.InnerException);
			}
		}
	}
}
