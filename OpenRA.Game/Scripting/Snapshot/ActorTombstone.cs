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

using Eluant;
using Eluant.ObjectBinding;

namespace OpenRA.Scripting.Snapshot
{
	public sealed class ActorTombstone : ScriptObjectWrapper, IScriptBindable, ILuaEqualityBinding, ILuaToStringBinding
	{
		public readonly uint ActorID;

		public readonly string ActorType;

		public readonly Player Owner;

		protected override string DuplicateKeyError(string memberName)
		{
			return $"Actor '{ActorType}' defines the command '{memberName}' on multiple traits";
		}

		protected override string MemberNotFoundError(string memberName)
		{
			return $"Actor '{ActorType} (dead)' does not define a property '{memberName}'";
		}

		public ActorTombstone(ScriptContext context, uint actorID, string actorType, Player owner)
			: base(context)
		{
			ActorID = actorID;
			ActorType = actorType;
			Owner = owner;

			Bind([new TombstoneProperties(actorType, owner)]);
		}

		public LuaValue Equals(LuaRuntime runtime, LuaValue left, LuaValue right)
		{
			if (!left.TryGetClrValue(out ActorTombstone a) || !right.TryGetClrValue(out ActorTombstone b))
				return false;

			return a.ActorID == b.ActorID;
		}

		public LuaValue ToString(LuaRuntime runtime)
		{
			return $"Actor ({ActorType} {ActorID})";
		}

		sealed class TombstoneProperties
		{
			readonly string type;
			readonly Player owner;

			public TombstoneProperties(string type, Player owner)
			{
				this.type = type;
				this.owner = owner;
			}

			public bool IsInWorld => false;

			public bool IsDead => true;

			public bool IsIdle => true;

			public Player Owner => owner;

			public Player EffectiveOwner => owner;

			public string Type => type;

			public bool HasProperty(string name)
			{
				return name == nameof(IsInWorld) || name == nameof(IsDead) || name == nameof(IsIdle)
					|| name == nameof(Owner) || name == nameof(EffectiveOwner) || name == nameof(Type)
					|| name == nameof(HasProperty);
			}
		}
	}
}
