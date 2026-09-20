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
using System.Globalization;
using System.IO;
using System.Linq;
using OpenRA.GameSaves;
using OpenRA.Mods.Common.Scripting;
using OpenRA.Mods.Common.Widgets.Logic;
using OpenRA.Scripting.Snapshot;

namespace OpenRA.Mods.Common.UtilityCommands
{
	public class VerifySnapshotCommand : IUtilityCommand
	{
		string IUtilityCommand.Name => "--verify-snapshot";

		const string SoakFlag = "--soak=";
		const string OrdersFlag = "--orders=";
		const string SaveFlag = "--save=";

		bool IUtilityCommand.ValidateArguments(string[] args)
		{
			return Positional(args).Length is >= 1 and <= 2;
		}

		[Desc("MAP", "[TICKS]", "[--soak=TICKS]", "[--orders=NAME,NAME,...]", "[--save=FILE]",
			"Save and restore a map, and check that the restored world matches.",
			"With --soak, tick the restored world on and compare every tick against the original.",
			"With --orders, issue those orders to the local player before the save, which is the only",
			"way a check reaches state that only an order can set, such as a developer mode cheat flag.",
			"With --save, also write the snapshot to FILE, so --dump-snapshot can read its sections.")]
		void IUtilityCommand.Run(Utility utility, string[] args)
		{
			var rest = Positional(args);

			var mapPath = rest[0];
			var ticks = rest.Length > 1 ? Exts.ParseInt32Invariant(rest[1]) : 100;

			var soak = 0;
			var soakArg = args.FirstOrDefault(a => a.StartsWith(SoakFlag, StringComparison.Ordinal));
			if (soakArg != null)
			{
				var value = soakArg[SoakFlag.Length..];
				if (!int.TryParse(value, NumberStyles.None, NumberFormatInfo.InvariantInfo, out soak) || soak < 1)
				{
					Console.WriteLine($"Invalid soak length '{value}': expected a positive whole number of ticks.");
					Environment.Exit(1);
				}
			}

			var orders = Array.Empty<string>();
			var ordersArg = args.FirstOrDefault(a => a.StartsWith(OrdersFlag, StringComparison.Ordinal));
			if (ordersArg != null)
			{
				orders = ordersArg[OrdersFlag.Length..]
					.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

				if (orders.Length == 0)
				{
					Console.WriteLine($"Invalid orders '{ordersArg[OrdersFlag.Length..]}': expected one or more names.");
					Environment.Exit(1);
				}
			}

			var savePath = args.FirstOrDefault(a => a.StartsWith(SaveFlag, StringComparison.Ordinal))?[SaveFlag.Length..];

			HeadlessGame.UseExistingMod(utility.ModData);

			try
			{
				if (!RoundTrip(mapPath, ticks, soak, orders, savePath))
					Environment.Exit(1);
			}
			finally
			{
				HeadlessGame.Shutdown();
			}
		}

		static bool ScriptFailed(HeadlessWorld world, string which)
		{
			var script = world.World.WorldActor.TraitOrDefault<LuaScript>();
			if (script == null || !script.FatalErrorOccurred)
				return false;

			Console.WriteLine($"FAIL: the mission script raised in the {which} world; see the Lua error above.");
			return true;
		}

		static (int Activities, int Effects) DroppedState(byte[] snapshot)
		{
			using (var ms = new MemoryStream(snapshot))
			using (var reader = new SnapshotReader(ms))
				return (reader.Header.DroppedActivities, reader.Header.DroppedEffects);
		}

		static string[] Positional(string[] args)
		{
			return args.Skip(1).Where(a => !a.StartsWith("--", StringComparison.Ordinal)).ToArray();
		}

		const int SoakDiffWindow = 8;

		static bool RoundTrip(string mapPath, int ticks, int soak, string[] orders, string savePath = null)
		{
			byte[] snapshot;
			int expectedHash;
			SnapshotDiff savedDiff;

			var map = HeadlessGame.LoadMap(mapPath);
			Console.WriteLine($"Map: {map.Title} ({map.Uid})");

			var soakHashes = new int[soak];
			var soakDiffs = new Queue<(int Tick, SnapshotDiff Diff)>();
			bool sourceScriptFailed;

			using (var source = HeadlessGame.CreateWorld(map, HeadlessGame.CreateSession(map)))
			{
				foreach (var order in orders)
					source.World.IssueOrder(new Order(order, source.World.LocalPlayer.PlayerActor, false));

				source.Tick(ticks);

				using (var ms = new MemoryStream())
				{
					try
					{
						source.Save(ms);
					}
					catch (LuaStateSnapshotRefusedException e)
					{
						Console.WriteLine($"REFUSED: {e.Message}");
						if (e.LuaPath != null)
							Console.WriteLine($"  at Lua path {e.LuaPath}");

						return true;
					}

					snapshot = ms.ToArray();
				}

				if (!string.IsNullOrEmpty(savePath))
				{
					File.WriteAllBytes(savePath, snapshot);
					Console.WriteLine($"Wrote the snapshot to {savePath}.");
				}

				expectedHash = source.World.SyncHash();
				savedDiff = source.CaptureDiff();

				Console.WriteLine(
					$"Saved at tick {source.World.WorldTick.ToStringInvariant()}: " +
					$"{source.World.Actors.Count().ToStringInvariant()} actors, " +
					$"{snapshot.Length.ToStringInvariant()} bytes, sync hash {expectedHash.ToStringInvariant()}");

				var dropped = DroppedState(snapshot);
				Console.WriteLine(
					$"Dropped by the save: {dropped.Activities.ToStringInvariant()} activit" +
					$"{(dropped.Activities == 1 ? "y" : "ies")}, " +
					$"{dropped.Effects.ToStringInvariant()} effect" +
					$"{(dropped.Effects == 1 ? "" : "s")}");

				for (var i = 0; i < soak; i++)
				{
					source.Tick();
					soakHashes[i] = source.World.SyncHash();

					var tick = i + 1;
					soakDiffs.Enqueue((tick, source.CaptureDiff()));
					if (soakDiffs.Count > SoakDiffWindow)
						soakDiffs.Dequeue();
				}

				sourceScriptFailed = ScriptFailed(source, "source");
			}

			if (sourceScriptFailed)
				return false;

			var restoreMap = HeadlessGame.LoadMap(mapPath);
			using (var restored = HeadlessGame.CreateWorld(restoreMap, HeadlessGame.CreateSession(restoreMap), restoring: true))
			{
				try
				{
					using (var ms = new MemoryStream(snapshot))
					{
						restored.Restore(ms, lenient: true, savedDiff: savedDiff);
					}
				}
				catch (Exception e)
				{
					Console.WriteLine($"FAIL: the restore threw {e.GetType().Name}: {e.Message}");
					return false;
				}

				var actualHash = restored.World.SyncHash();
				if (actualHash != expectedHash)
				{
					Console.WriteLine(
						$"FAIL: sync hash is {actualHash.ToStringInvariant()}, expected {expectedHash.ToStringInvariant()}.");

					var detail = SnapshotDiff.Compare(savedDiff, restored.CaptureDiff());
					if (!string.IsNullOrEmpty(detail))
						Console.Write(detail);

					return false;
				}

				Console.WriteLine("OK: restored world matches.");

				if (soak > 0 && !Soak(restored, soakHashes, soakDiffs))
					return false;

				if (ScriptFailed(restored, "restored"))
					return false;

				return DescribeAsBrowsersWould(snapshot);
			}
		}

		static bool Soak(HeadlessWorld restored, int[] expected, Queue<(int Tick, SnapshotDiff Diff)> diffs)
		{
			for (var i = 0; i < expected.Length; i++)
			{
				restored.Tick();

				var actual = restored.World.SyncHash();
				if (actual == expected[i])
					continue;

				Console.WriteLine(
					$"FAIL: soak diverged {(i + 1).ToStringInvariant()} tick(s) after the save: " +
					$"sync hash is {actual.ToStringInvariant()}, expected {expected[i].ToStringInvariant()}.");

				var recorded = diffs.FirstOrDefault(d => d.Tick == i + 1).Diff;
				if (recorded == null)
				{
					Console.WriteLine(
						$"  The diff for that tick is outside the {SoakDiffWindow.ToStringInvariant()}-tick window " +
						"that is kept. Re-run with a shorter soak to see it.");

					return false;
				}

				var detail = SnapshotDiff.Compare(recorded, restored.CaptureDiff());
				if (!string.IsNullOrEmpty(detail))
					Console.Write(detail);

				return false;
			}

			Console.WriteLine($"OK: restored world still matches after {expected.Length.ToStringInvariant()} ticks.");
			return true;
		}

		static bool DescribeAsBrowsersWould(byte[] snapshot)
		{
			var path = Path.Combine(Path.GetTempPath(), $"openra-verify-{Guid.NewGuid():N}{SavePaths.Extension}");
			try
			{
				File.WriteAllBytes(path, snapshot);

				var info = SaveFileInfo.Read(path);
				if (info == null)
				{
					Console.WriteLine("FAIL: the snapshot cannot be read back as a save file.");
					return false;
				}

				var players = string.Join(", ", info.SlotClients.Values
					.Select(c => c.Bot != null ? $"{c.Faction} (bot)" : c.Faction));

				Console.WriteLine(
					$"Save file: {info.Format}, duration " +
					$"{GameSaveUtils.FormatGameDuration(info.Duration)}, " +
					$"{info.SlotClients.Count.ToStringInvariant()} player(s): {players}");

				return true;
			}
			finally
			{
				if (File.Exists(path))
					File.Delete(path);
			}
		}
	}
}
