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
using Eluant;

namespace OpenRA.Scripting.Snapshot
{
	public interface ILuaStateSnapshotCodec
	{
		bool TrySaveClrObject(object clrObject, out byte[] payload);

		LuaValue RestoreClrObject(byte[] payload);

		bool TrySaveMethodTarget(object target, out byte[] payload);

		object RestoreMethodTarget(byte[] payload);

		bool AllowBoundMethod(Type declaringType, string methodName);
	}

	public sealed class LuaStateSnapshotRefusedException : Exception
	{
		public string LuaPath { get; }

		public LuaStateSnapshotRefusedException(string message, string luaPath)
			: base(message)
		{
			LuaPath = luaPath;
		}

		public LuaStateSnapshotRefusedException(string message, Exception inner, string luaPath)
			: base(message, inner)
		{
			LuaPath = luaPath;
		}
	}

	public static class LuaStateSnapshot
	{
		static LuaStateSnapshot()
		{
#if LUA_SNAPSHOT
			try
			{
				if (LuaSnapshot.CheckLayout(out var reason))
					Supported = true;
				else
					UnsupportedReason = reason;
			}
			catch (Exception e)
			{
				UnsupportedReason = "the Lua binding could not be probed: " + e.Message;
			}
#else
			UnsupportedReason = "this engine was built against a Lua binding without Eluant.LuaSnapshot";
#endif
		}

		public static bool Supported { get; }

		public static string UnsupportedReason { get; }

		static void RequireSupported()
		{
			if (Supported)
				return;

			throw new LuaStateSnapshotRefusedException(
				"The Lua script state cannot be written down: " + UnsupportedReason + ".", null);
		}

		public static void Save(LuaRuntime runtime, Stream output, ILuaStateSnapshotCodec codec,
			IEnumerable<string> engineGlobals, IList<LuaValue> roots)
		{
			RequireSupported();

#if LUA_SNAPSHOT
			var options = BuildOptions(codec, engineGlobals);
			options.AdditionalRoots = roots;

			try
			{
				LuaSnapshot.Save(runtime, output, options);
			}
			catch (LuaSnapshotException e)
			{
				throw new LuaStateSnapshotRefusedException(e.Message, e, e.LuaPath);
			}
#endif
		}

		public static LuaValue[] Load(LuaRuntime runtime, Stream input, ILuaStateSnapshotCodec codec,
			IEnumerable<string> engineGlobals)
		{
			RequireSupported();

#if LUA_SNAPSHOT
			var options = BuildOptions(codec, engineGlobals);

			try
			{
				return LuaSnapshot.Load(runtime, input, options);
			}
			catch (LuaSnapshotException e)
			{
				throw new LuaStateSnapshotRefusedException(e.Message, e, e.LuaPath);
			}
#else
			return [];
#endif
		}

#if LUA_SNAPSHOT
		static LuaSnapshotOptions BuildOptions(ILuaStateSnapshotCodec codec, IEnumerable<string> engineGlobals)
		{
			var options = new LuaSnapshotOptions { Codec = new CodecAdapter(codec) };

			foreach (var name in engineGlobals)
				options.EngineGlobals.Add(name);

			return options;
		}

		sealed class CodecAdapter : ILuaSnapshotCodec, ILuaBoundMethodCodec
		{
			readonly ILuaStateSnapshotCodec codec;

			public CodecAdapter(ILuaStateSnapshotCodec codec) { this.codec = codec; }

			public bool TrySaveClrObject(LuaClrObjectValue value, out byte[] payload)
			{
				try
				{
					return codec.TrySaveClrObject(value.ClrObject, out payload);
				}
				catch (LuaSnapshotException)
				{
					throw;
				}
				catch (Exception e)
				{
					throw new LuaSnapshotException("the engine codec failed to write a CLR object: " + e.Message, e);
				}
			}

			public LuaValue RestoreClrObject(byte[] payload)
			{
				try
				{
					return codec.RestoreClrObject(payload);
				}
				catch (LuaSnapshotException)
				{
					throw;
				}
				catch (Exception e)
				{
					throw new LuaSnapshotException("the engine codec failed to restore a CLR object: " + e.Message, e);
				}
			}

			public bool TrySaveMethodTarget(object target, out byte[] payload)
			{
				try
				{
					return codec.TrySaveMethodTarget(target, out payload);
				}
				catch (LuaSnapshotException)
				{
					throw;
				}
				catch (Exception e)
				{
					throw new LuaSnapshotException("the engine codec failed to write a bound method target: " + e.Message, e);
				}
			}

			public object RestoreMethodTarget(byte[] payload)
			{
				try
				{
					return codec.RestoreMethodTarget(payload);
				}
				catch (LuaSnapshotException)
				{
					throw;
				}
				catch (Exception e)
				{
					throw new LuaSnapshotException("the engine codec failed to restore a bound method target: " + e.Message, e);
				}
			}

			public bool AllowBoundMethod(Type declaringType, string methodName)
			{
				try
				{
					return codec.AllowBoundMethod(declaringType, methodName);
				}
				catch (LuaSnapshotException)
				{
					throw;
				}
				catch (Exception e)
				{
					throw new LuaSnapshotException("the engine codec failed to check a bound method: " + e.Message, e);
				}
			}
		}
#endif
	}
}
