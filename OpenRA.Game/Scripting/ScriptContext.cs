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
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using Eluant;
using OpenRA.GameRules;
using OpenRA.Graphics;
using OpenRA.Primitives;
using OpenRA.Scripting.Snapshot;
using OpenRA.Support;
using OpenRA.Traits;

namespace OpenRA.Scripting
{
	// Tag interfaces specifying the type of bindings to create
	public interface IScriptBindable { }

	// For objects that need the context to create their bindings
	public interface IScriptNotifyBind
	{
		void OnScriptBind(ScriptContext context);
	}

	// For traitinfos that provide actor / player commands
	[AttributeUsage(AttributeTargets.Class)]
	public sealed class ScriptPropertyGroupAttribute(string category) : Attribute
	{
		public readonly string Category = category;
	}

	// For property groups that are safe to initialize invoke on destroyed actors
	[AttributeUsage(AttributeTargets.Class)]
	public sealed class ExposedForDestroyedActors : Attribute { }

	[AttributeUsage(AttributeTargets.Property | AttributeTargets.Method)]
	public sealed class ScriptActorPropertyActivityAttribute : Attribute { }

	public interface IScriptMemberOwner
	{
		object ScriptOwner { get; }
	}

	public abstract class ScriptActorProperties(ScriptContext context, Actor self) : IScriptMemberOwner
	{
		protected readonly Actor Self = self;
		protected readonly ScriptContext Context = context;

		public object ScriptOwner => Self;
	}

	public abstract class ScriptPlayerProperties(ScriptContext context, Player player) : IScriptMemberOwner
	{
		protected readonly Player Player = player;
		protected readonly ScriptContext Context = context;

		public object ScriptOwner => Player;
	}

	public abstract class ScriptProjectileProperties(ScriptContext context, IProjectileScriptInfo projectile) : IScriptMemberOwner
	{
		protected readonly IProjectileScriptInfo Projectile = projectile;
		protected readonly ScriptContext Context = context;

		public object ScriptOwner => Projectile;
	}

	/// <summary>
	/// Provides global bindings in Lua code.
	/// </summary>
	/// <remarks>
	/// <para>
	/// Instance methods and properties declared in derived classes will be made available in Lua. Use
	/// <see cref="ScriptGlobalAttribute"/> on your derived class to specify the name exposed in Lua. It is recommended
	/// to apply <see cref="DescAttribute"/> against each method or property to provide a description of what it does.
	/// </para>
	/// <para>
	/// Any parameters to your method that are <see cref="LuaValue"/>s will be disposed automatically when your method
	/// completes. If you need to return any of these values, or need them to live longer than your method, you must
	/// use <see cref="LuaValue.CopyReference"/> to get your own copy of the value. Any copied values you return will
	/// be disposed automatically, but you assume responsibility for disposing any other copies.
	/// </para>
	/// </remarks>
	public abstract class ScriptGlobal : ScriptObjectWrapper
	{
		protected override string DuplicateKeyError(string memberName) { return $"Table '{Name}' defines multiple members '{memberName}'"; }
		protected override string MemberNotFoundError(string memberName) { return $"Table '{Name}' does not define a property '{memberName}'"; }

		public readonly string Name;

		protected ScriptGlobal(ScriptContext context)
			: base(context)
		{
			// GetType resolves the actual (subclass) type
			var type = GetType();
			var names = type.GetCustomAttributes<ScriptGlobalAttribute>(true);
			if (names.Length != 1)
				throw new InvalidOperationException($"[ScriptGlobal] attribute not found for global table '{type}'");

			Name = names[0].Name;
			Bind([this]);
		}

		protected IEnumerable<T> FilteredObjects<T>(IEnumerable<T> objects, LuaFunction filter)
		{
			if (filter != null)
			{
				objects = objects.Where(a =>
				{
					using (var luaObject = a.ToLuaValue(Context))
					using (var filterResult = filter.Call(luaObject))
					using (var result = filterResult[0])
						return result.ToBoolean();
				});
			}

			return objects;
		}
	}

	[AttributeUsage(AttributeTargets.Class)]
	public sealed class ScriptGlobalAttribute(string name) : Attribute
	{
		public readonly string Name = name;
	}

	public sealed class ScriptContext : IDisposable
	{
		// Restrict user scripts (excluding system libraries) to 50 MB of memory use
		const int MaxUserScriptMemory = 50 * 1024 * 1024;

		// Restrict the number of instructions that will be run per map function call
		const int MaxUserScriptInstructions = 1000000;

		public World World { get; }
		public WorldRenderer WorldRenderer { get; }

		readonly MemoryConstrainedLuaRuntime runtime;

		readonly List<string> engineGlobals = [];

		readonly List<LuaValue> handles = [];

		LuaFunction tick;

		readonly Type[] knownActorCommands;
		public readonly Cache<ActorInfo, Type[]> ActorCommands;
		public readonly Type[] PlayerCommands;
		public readonly Type[] ProjectileCommands;

		public string ErrorMessage;

		bool disposed;

		public ScriptContext(World world, WorldRenderer worldRenderer,
			IEnumerable<string> scripts)
		{
			runtime = new MemoryConstrainedLuaRuntime();

			Log.AddChannel("lua", "lua.log");

			World = world;
			WorldRenderer = worldRenderer;
			knownActorCommands = Game.ModData.ObjectCreator
				.GetTypesImplementing<ScriptActorProperties>()
				.ToArray();

			ActorCommands = new Cache<ActorInfo, Type[]>(FilterActorCommands);

			var knownPlayerCommands = Game.ModData.ObjectCreator
				.GetTypesImplementing<ScriptPlayerProperties>()
				.ToArray();
			PlayerCommands = FilterCommands(world.Map.Rules.Actors[SystemActors.Player], knownPlayerCommands);

			ProjectileCommands = Game.ModData.ObjectCreator
				.GetTypesImplementing<ScriptProjectileProperties>()
				.ToArray();

			// Safe functions for http://lua-users.org/wiki/SandBoxes
			// assert, error have been removed as well as albeit safe
			var allowedGlobals = new string[]
			{
				"ipairs", "next", "pairs",
				"pcall", "select", "tonumber", "tostring", "type", "unpack", "xpcall",
				"math", "string", "table"
			};

			foreach (var fieldName in runtime.Globals.Keys)
				if (!allowedGlobals.Contains(fieldName.ToString()))
					runtime.Globals[fieldName] = null;

			var forbiddenMath = new string[]
			{
				"random",
				"randomseed"
			};

			var mathGlobal = (LuaTable)runtime.Globals["math"];
			foreach (var mathFunction in mathGlobal.Keys)
				if (forbiddenMath.Contains(mathFunction.ToString()))
					mathGlobal[mathFunction] = null;

			// Register globals
			InstallEngineGlobal("EngineDir", Platform.EngineDir);

			using (var fn = runtime.CreateFunctionFromDelegate((Action<string>)FatalError))
				InstallEngineGlobal("FatalError", fn);

			InstallEngineGlobal("MaxUserScriptInstructions", MaxUserScriptInstructions);

			using (var fn = runtime.CreateFunctionFromDelegate(LogDebugMessage))
				InstallEngineGlobal("print", fn);

			// Register global tables
			var bindings = Game.ModData.ObjectCreator.GetTypesImplementing<ScriptGlobal>();
			foreach (var b in bindings)
			{
				var ctor = b.GetConstructors(BindingFlags.Public | BindingFlags.Instance).FirstOrDefault(c =>
				{
					var p = c.GetParameters();
					return p.Length == 1 && p[0].ParameterType == typeof(ScriptContext);
				});

				if (ctor == null)
					throw new InvalidOperationException($"{b.Name} must define a constructor that takes a {nameof(ScriptContext)} context parameter");

				var binding = (ScriptGlobal)ctor.Invoke([this]);
				using (var obj = binding.ToLuaValue(this))
					runtime.Globals.Add(binding.Name, obj);

				engineGlobals.Add(binding.Name);
			}

			// System functions do not count towards the memory limit
			runtime.MaxMemoryUse = runtime.MemoryUse + MaxUserScriptMemory;

			try
			{
				RunScripts(scripts);
			}
			catch (Exception e)
			{
				FatalError(e);
				return;
			}

			tick = runtime.Globals["Tick"] as LuaFunction;
		}

		public void RunScripts(IEnumerable<string> scripts)
		{
			foreach (var script in scripts)
				runtime.DoBuffer(World.Map.Open(script).ReadAllText(), script).Dispose();
		}

		void LogDebugMessage(string message)
		{
			Console.WriteLine($"Lua debug: {message}");
			Log.Write("lua", message);
		}

		void InstallEngineGlobal(string name, LuaValue value)
		{
			runtime.Globals[name] = value;
			engineGlobals.Add(name);
		}

		public bool FatalErrorOccurred { get; private set; }
		public void FatalError(Exception e)
		{
			ReportFatalError(e.Message, e.StackTrace);
		}

		void FatalError(string message)
		{
			ReportFatalError(message, new StackTrace().ToString());
		}

		void ReportFatalError(string message, string stackTrace)
		{
			ErrorMessage = message;

			Console.WriteLine($"Fatal Lua Error: {message}");
			Console.WriteLine(stackTrace);

			Log.Write("lua", $"Fatal Lua Error: {message}");
			Log.Write("lua", stackTrace);

			if (!FatalErrorOccurred)
				World.AddFrameEndTask(_ => World.EndGame());

			FatalErrorOccurred = true;
		}

		public void RegisterMapActor(string name, Actor a)
		{
			RegisterMapActor(name, a, replace: false);
		}

		public void ReinstallMapActorGlobals(IEnumerable<KeyValuePair<string, Actor>> actors)
		{
			ArgumentNullException.ThrowIfNull(actors);

			foreach (var kv in actors)
				RegisterMapActor(kv.Key, kv.Value, replace: true);
		}

		void RegisterMapActor(string name, Actor a, bool replace)
		{
			ArgumentNullException.ThrowIfNull(a);

			if (runtime.Globals.ContainsKey(name) && !replace)
				throw new LuaException($"The global name '{name}' is reserved, and may not be used by a map actor");

			using (var obj = a.ToLuaValue(this))
				runtime.Globals[name] = obj;
		}

		public void WorldLoaded()
		{
			if (FatalErrorOccurred || runtime.Globals["WorldLoaded"] is not LuaFunction worldLoaded)
				return;

			try
			{
				worldLoaded.Call().Dispose();
			}
			catch (LuaException e)
			{
				FatalError(e);
			}
			finally
			{
				worldLoaded?.Dispose();
			}
		}

		public void Tick()
		{
			if (FatalErrorOccurred || disposed || tick == null)
				return;

			try
			{
				using (new PerfSample("tick_lua"))
					tick.Call().Dispose();
			}
			catch (LuaException e)
			{
				FatalError(e);
			}
		}

		public void Dispose()
		{
			if (disposed)
				return;

			disposed = true;
			runtime?.Dispose();
		}

		static IEnumerable<Type> ExtractRequiredTypes(Type t)
		{
			// Returns the inner types of all the Requires<T> interfaces on this type
			var outer = t.GetInterfaces()
				.Where(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(Requires<>));

			return outer.SelectMany(i => i.GetGenericArguments());
		}

		static readonly object[] NoArguments = [];
		Type[] FilterActorCommands(ActorInfo ai)
		{
			return FilterCommands(ai, knownActorCommands);
		}

		Type[] FilterCommands(ActorInfo ai, Type[] knownCommands)
		{
			var method = typeof(ActorInfo).GetMethod(nameof(ActorInfo.HasTraitInfo));
			return knownCommands.Where(c => ExtractRequiredTypes(c)
				.All(t => (bool)method.MakeGenericMethod(t).Invoke(ai, NoArguments)))
				.ToArray();
		}

		public LuaTable CreateTable() { return runtime.CreateTable(); }

		public int RegisterHandle(LuaFunction function)
		{
			handles.Add(function);
			return handles.Count - 1;
		}

		public LuaFunction ResolveHandle(int index)
		{
			if (index < 0 || index >= handles.Count)
				throw new InvalidDataException($"The save refers to Lua handle {index}, which this restore did not produce.");

			if (handles[index] is not LuaFunction function)
				throw new InvalidDataException($"Lua handle {index} is not a function.");

			return function;
		}

		public void SaveLuaState(Stream stream, ILuaStateSnapshotCodec codec)
		{
			LuaStateSnapshot.Save(runtime, stream, codec, engineGlobals, handles);
			handles.Clear();
		}

		public void LoadLuaState(Stream stream, ILuaStateSnapshotCodec codec)
		{
			runtime.MaxMemoryUse = long.MaxValue;

			try
			{
				handles.Clear();
				handles.AddRange(LuaStateSnapshot.Load(runtime, stream, codec, engineGlobals));
			}
			finally
			{
				runtime.MaxMemoryUse = runtime.MemoryUse + MaxUserScriptMemory;
			}

			tick = runtime.Globals["Tick"] as LuaFunction;
		}
	}
}
