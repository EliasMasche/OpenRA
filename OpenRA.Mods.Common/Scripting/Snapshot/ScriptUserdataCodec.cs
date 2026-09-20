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
using System.Text;
using Eluant;
using OpenRA.GameSaves;
using OpenRA.Primitives;
using OpenRA.Scripting;
using OpenRA.Scripting.Snapshot;

namespace OpenRA.Mods.Common.Scripting.Snapshot
{
	public sealed class ScriptUserdataCodec : ILuaStateSnapshotCodec
	{
		const string TypeKey = "Type";
		const string RefKey = "Ref";
		const string BitsKey = "Bits";
		const string XKey = "X";
		const string YKey = "Y";
		const string ZKey = "Z";
		const string LengthKey = "Length";
		const string AngleKey = "Angle";
		const string ArgbKey = "Argb";

		const string ActorTypeKey = "ActorType";
		const string OwnerKey = "Owner";
		const string MemberKey = "Member";

		const string ActorType = "Actor";
		const string TombstoneType = "DeadActor";
		const string PlayerType = "Player";
		const string CPosType = "CPos";
		const string CVecType = "CVec";
		const string WPosType = "WPos";
		const string WVecType = "WVec";
		const string WDistType = "WDist";
		const string WAngleType = "WAngle";
		const string ColorType = "Color";
		const string BoundMethodType = "BoundMethod";

		readonly ScriptContext context;
		readonly SnapshotWriter writer;
		readonly SnapshotReader reader;

		ScriptUserdataCodec(ScriptContext context, SnapshotWriter writer, SnapshotReader reader)
		{
			this.context = context;
			this.writer = writer;
			this.reader = reader;
		}

		public static ScriptUserdataCodec ForSave(ScriptContext context, SnapshotWriter writer)
		{
			return new ScriptUserdataCodec(context, writer, null);
		}

		public static ScriptUserdataCodec ForLoad(ScriptContext context, SnapshotReader reader)
		{
			return new ScriptUserdataCodec(context, null, reader);
		}

		public bool TrySaveClrObject(object clrObject, out byte[] payload)
		{
			if (writer == null)
				throw new InvalidOperationException("This codec reads a heap; it does not write one.");

			payload = null;

			switch (clrObject)
			{
				case Actor actor:
					if (actor.Disposed || (!actor.IsInWorld && !actor.TraitsImplementing<ISync>().Any()))
					{
						payload = Encode(
							new MiniYamlNode(TypeKey, TombstoneType),
							new MiniYamlNode(RefKey, writer.ActorRef(actor)),
							new MiniYamlNode(ActorTypeKey, actor.Info.Name),
							new MiniYamlNode(OwnerKey, writer.PlayerRef(actor.Owner)));
						return true;
					}

					payload = Encode(
						new MiniYamlNode(TypeKey, ActorType),
						new MiniYamlNode(RefKey, writer.ActorRef(actor)));
					return true;

				case ActorTombstone tombstone:
					payload = Encode(
						new MiniYamlNode(TypeKey, TombstoneType),
						new MiniYamlNode(RefKey, SnapshotRefs.FormatActorID(tombstone.ActorID)),
						new MiniYamlNode(ActorTypeKey, tombstone.ActorType),
						new MiniYamlNode(OwnerKey, writer.PlayerRef(tombstone.Owner)));
					return true;

				case Player player:
					payload = Encode(
						new MiniYamlNode(TypeKey, PlayerType),
						new MiniYamlNode(RefKey, writer.PlayerRef(player)));
					return true;

				case CPos pos:
					payload = Encode(
						new MiniYamlNode(TypeKey, CPosType),
						new MiniYamlNode(BitsKey, FieldSaver.FormatValue(pos.Bits)));
					return true;

				case CVec vec:
					payload = Encode(
						new MiniYamlNode(TypeKey, CVecType),
						new MiniYamlNode(XKey, FieldSaver.FormatValue(vec.X)),
						new MiniYamlNode(YKey, FieldSaver.FormatValue(vec.Y)));
					return true;

				case WPos wpos:
					payload = Encode(
						new MiniYamlNode(TypeKey, WPosType),
						new MiniYamlNode(XKey, FieldSaver.FormatValue(wpos.X)),
						new MiniYamlNode(YKey, FieldSaver.FormatValue(wpos.Y)),
						new MiniYamlNode(ZKey, FieldSaver.FormatValue(wpos.Z)));
					return true;

				case WVec wvec:
					payload = Encode(
						new MiniYamlNode(TypeKey, WVecType),
						new MiniYamlNode(XKey, FieldSaver.FormatValue(wvec.X)),
						new MiniYamlNode(YKey, FieldSaver.FormatValue(wvec.Y)),
						new MiniYamlNode(ZKey, FieldSaver.FormatValue(wvec.Z)));
					return true;

				case WDist dist:
					payload = Encode(
						new MiniYamlNode(TypeKey, WDistType),
						new MiniYamlNode(LengthKey, FieldSaver.FormatValue(dist.Length)));
					return true;

				case WAngle angle:
					payload = Encode(
						new MiniYamlNode(TypeKey, WAngleType),
						new MiniYamlNode(AngleKey, FieldSaver.FormatValue(angle.Angle)));
					return true;

				case Color color:
					payload = Encode(
						new MiniYamlNode(TypeKey, ColorType),
						new MiniYamlNode(ArgbKey, FieldSaver.FormatValue(color.ToArgb())));
					return true;

				default:
					return false;
			}
		}

		public LuaValue RestoreClrObject(byte[] payload)
		{
			if (reader == null)
				throw new InvalidOperationException("This codec writes a heap; it does not read one.");

			var fields = Decode(payload);
			if (!fields.TryGetValue(TypeKey, out var type))
			{
				Log.Write("debug", $"Refused to restore a script object whose payload names no type: {Describe(payload)}");
				return null;
			}

			switch (type)
			{
				case ActorType:
				{
					var reference = Field(fields, RefKey);
					var actor = reader.ResolveActor(reference);
					if (actor == null)
						Log.Write("debug", $"Refused to restore a script reference: actor '{reference}' is not in " +
							"the restored world, and this save wrote it as a live reference rather than a " +
							$"dead-actor stand-in. Retaking the save resolves it. {Describe(payload)}");

					return actor?.ToLuaValue(context);
				}

				case TombstoneType:
				{
					if (!SnapshotRefs.TryParseActorID(Field(fields, RefKey), out var actorID))
					{
						Log.Write("debug", $"Refused to restore a dead-actor stand-in: '{Field(fields, RefKey)}' " +
							$"is not an actor id. {Describe(payload)}");
						return null;
					}

					var actorType = Field(fields, ActorTypeKey);
					if (actorType == null)
					{
						Log.Write("debug", $"Refused to restore a dead-actor stand-in with no actor type. {Describe(payload)}");
						return null;
					}

					var owner = reader.ResolvePlayer(Field(fields, OwnerKey));
					return new ActorTombstone(context, actorID, actorType, owner).ToLuaValue(context);
				}

				case PlayerType:
				{
					var reference = Field(fields, RefKey);
					var player = reader.ResolvePlayer(reference);
					if (player == null)
						Log.Write("debug", $"Refused to restore a script reference: player '{reference}' is not in " +
							$"the restored world. {Describe(payload)}");

					return player?.ToLuaValue(context);
				}

				case CPosType:
					return new CPos(Read<int>(fields, BitsKey)).ToLuaValue(context);

				case CVecType:
					return new CVec(Read<int>(fields, XKey), Read<int>(fields, YKey)).ToLuaValue(context);

				case WPosType:
					return new WPos(Read<int>(fields, XKey), Read<int>(fields, YKey), Read<int>(fields, ZKey)).ToLuaValue(context);

				case WVecType:
					return new WVec(Read<int>(fields, XKey), Read<int>(fields, YKey), Read<int>(fields, ZKey)).ToLuaValue(context);

				case WDistType:
					return new WDist(Read<int>(fields, LengthKey)).ToLuaValue(context);

				case WAngleType:
					return new WAngle(Read<int>(fields, AngleKey)).ToLuaValue(context);

				case ColorType:
					return Color.FromArgb(Read<uint>(fields, ArgbKey)).ToLuaValue(context);

				default:
					Log.Write("debug", $"Refused to restore a script object of unknown type '{type}'. {Describe(payload)}");
					return null;
			}
		}

		static string Describe(byte[] payload)
		{
			try
			{
				return MiniYaml.FromString(Encoding.UTF8.GetString(payload), "ScriptObject")
					.WriteToString().Replace(Environment.NewLine, " ").Trim();
			}
			catch (Exception e)
			{
				return $"<undecodable payload of {payload.Length} bytes: {e.Message}>";
			}
		}

		public bool TrySaveMethodTarget(object target, out byte[] payload)
		{
			if (writer == null)
				throw new InvalidOperationException("This codec reads a heap; it does not write one.");

			payload = null;

			if (target == null)
			{
				payload = Encode(new MiniYamlNode(TypeKey, BoundMethodType));
				return true;
			}

			if (target is not ScriptMemberWrapper wrapper || wrapper.Target is not IScriptMemberOwner owner)
				return false;

			if (!TrySaveClrObject(owner.ScriptOwner, out var ownerPayload))
				return false;

			payload = Encode(
				new MiniYamlNode(TypeKey, BoundMethodType),
				new MiniYamlNode(MemberKey, wrapper.Member.Name),
				new MiniYamlNode(OwnerKey, Convert.ToBase64String(ownerPayload)));
			return true;
		}

		public object RestoreMethodTarget(byte[] payload)
		{
			if (reader == null)
				throw new InvalidOperationException("This codec writes a heap; it does not read one.");

			var fields = Decode(payload);
			if (Field(fields, TypeKey) != BoundMethodType)
				return null;

			var member = Field(fields, MemberKey);
			var ownerPayload = Field(fields, OwnerKey);

			if (member == null || ownerPayload == null)
				return null;

			byte[] ownerBytes;
			try
			{
				ownerBytes = Convert.FromBase64String(ownerPayload);
			}
			catch (FormatException)
			{
				return null;
			}

			var ownerValue = RestoreClrObject(ownerBytes);
			if (ownerValue == null)
				return null;

			using (ownerValue)
			{
				if (ownerValue.TryGetClrValue(out Actor actor))
					return actor.TryGetScriptMember(member, out var wrapper) ? wrapper : null;

				if (ownerValue.TryGetClrValue(out Player player))
					return player.TryGetScriptMember(member, out var wrapper) ? wrapper : null;
			}

			return null;
		}

		public bool AllowBoundMethod(Type declaringType, string methodName)
		{
			return declaringType == typeof(ScriptMemberWrapper) && methodName == "Invoke";
		}

		static byte[] Encode(params MiniYamlNode[] nodes)
		{
			return Encoding.UTF8.GetBytes(nodes.WriteToString());
		}

		static Dictionary<string, string> Decode(byte[] payload)
		{
			var fields = new Dictionary<string, string>();

			foreach (var node in MiniYaml.FromString(Encoding.UTF8.GetString(payload), "lua-snapshot"))
				fields.TryAdd(node.Key, node.Value.Value);

			return fields;
		}

		static string Field(Dictionary<string, string> fields, string key)
		{
			return fields.TryGetValue(key, out var value) ? value : null;
		}

		static T Read<T>(Dictionary<string, string> fields, string key)
		{
			return FieldLoader.GetValue<T>(key, Field(fields, key));
		}
	}
}
