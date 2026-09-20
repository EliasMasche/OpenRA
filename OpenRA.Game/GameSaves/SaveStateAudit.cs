#region Copyright & License Information
/*
 * Copyright (c) The OpenRA Developers and Contributors
 * This file is part of OpenRA, which is free software. It is made
 * available to you under the terms of the GNU General Public License
 * as published by the Free Software Foundation, either version 3 of
 * the License, or (at your option) any later version. For more information,
 * see COPYING.
 */
#endregion

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace OpenRA.GameSaves
{
	public static class SaveStateAudit
	{
		public static readonly IReadOnlyDictionary<string, string> Abstentions = new Dictionary<string, string>
		{
			{ "DebugPauseState", "a debug toggle that no saved game has to reproduce" },
			{ "Husk", "placement inits restore TopLeft, CenterPosition and Facing, because this trait is the actor's IOccupySpace and IFacing" }
		};

		static readonly Type[] Mechanisms =
		[
			typeof(ISaveState),
			typeof(IWorldSaveState),
			typeof(ISaveableEffect),

			typeof(INotifyStateRestored)
		];

		public static IEnumerable<string> UnsavedSyncedMembers(Type type)
		{
			ArgumentNullException.ThrowIfNull(type);

			if (type.IsGenericTypeDefinition)
				return [];

			return UnsavedSyncedMembersCore(type);
		}

		static IEnumerable<string> UnsavedSyncedMembersCore(Type type)
		{
			foreach (var (name, declaring) in SyncedMembers(type))
				if (!WritesOwnState(declaring))
					yield return name;
		}

		static IEnumerable<(string Name, Type Declaring)> SyncedMembers(Type type)
		{
			const BindingFlags Binding = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

			foreach (var field in type.GetFields(Binding))
				if (!field.IsInitOnly && field.IsDefined(typeof(VerifySyncAttribute), true))
					yield return (field.Name, field.DeclaringType);

			foreach (var property in type.GetProperties(Binding))
				if (IsSettable(property) && property.IsDefined(typeof(VerifySyncAttribute), true))
					yield return (property.Name, property.DeclaringType);
		}

		static bool IsSettable(PropertyInfo property)
		{
			var setter = (DeclaredProperty(property) ?? property).SetMethod ?? property.SetMethod;
			return setter != null && !IsInitOnly(setter);
		}

		static PropertyInfo DeclaredProperty(PropertyInfo property)
		{
			const BindingFlags Declared = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly;
			return property.DeclaringType?.GetProperties(Declared).FirstOrDefault(p => p.Name == property.Name);
		}

		static bool IsInitOnly(MethodInfo setter)
		{
			return setter.ReturnParameter.GetRequiredCustomModifiers()
				.Any(m => m == typeof(System.Runtime.CompilerServices.IsExternalInit));
		}

		static bool WritesOwnState(Type type)
		{
			foreach (var mechanism in Mechanisms)
			{
				var implemented = type.GetInterface(mechanism.FullName);
				if (implemented == null)
					continue;

				foreach (var target in type.GetInterfaceMap(implemented).TargetMethods)
					if (target.DeclaringType == type)
						return true;
			}

			const BindingFlags Declared = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly;
			foreach (var method in type.GetMethods(Declared))
				if (method.Name is "SaveState" or "LoadState")
					return true;

			return false;
		}
	}
}
