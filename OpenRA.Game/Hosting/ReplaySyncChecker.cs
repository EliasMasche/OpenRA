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

#nullable enable

using System;
using OpenRA.FileFormats;
using OpenRA.Network;

namespace OpenRA.Hosting
{
	public enum ReplaySyncOutcome
	{
		Passed,

		Desync,

		/// <summary>The simulation stopped before the recording's last frame.</summary>
		Incomplete,

		Unreadable,

		MapMissing,

		/// <summary>This host cannot run the recording, such as a map whose scripts need a display.</summary>
		Unsupported,

		/// <summary>Loading or replaying the recording threw, such as a recording without lobby info.</summary>
		Error,

		/// <summary>The caller ended the replay early, at the tick it wanted.</summary>
		Stopped,
	}

	public readonly record struct ReplaySyncResult(string Replay, ReplaySyncOutcome Outcome, int NetFrame, int RecordedFrames,
		string? Reason = null);

	/// <summary>
	/// Replays a recording without a display. It compares each sync hash of the recording with the hash of the world
	/// that this build simulates. So a change to the simulation gives a desync.
	/// </summary>
	public static class ReplaySyncChecker
	{
		// The number of consecutive frames without a world tick after which the recording counts as exhausted. A
		// replay never waits for input, because every order is already on disk, so a stall means the recording
		// ended.
		const int StallLimit = 1000;

		/// <summary>Sets the mod up to replay: its loaders and its installed maps, which a replay looks up.</summary>
		public static void UseMod(ModData modData)
		{
			// The replay's StartGame order builds the world through the static Game state.
			Game.ModData = modData;
			modData.InitializeLoaders(modData.DefaultFileSystem);
			modData.MapCache.LoadPreviewImages = false;
			modData.MapCache.LoadMaps(modData);
		}

		/// <summary>
		/// <paramref name="afterTick"/> runs after each world tick, before the next orders apply. When it returns
		/// false, the replay ends with <see cref="ReplaySyncOutcome.Stopped"/>. <paramref name="worldStarted"/> runs
		/// once, before the first tick. A caller can load a snapshot there, into a world that
		/// <paramref name="startupActors"/> starts without the actors of its map. A refused load ends the check as an
		/// error.
		/// </summary>
		public static ReplaySyncResult Check(string replayFile, Func<World, bool>? afterTick = null,
			StartupActors startupActors = StartupActors.FromMap, Action<World>? worldStarted = null)
		{
			var host = Game.Host;
			if (host.Profile.HasDisplay || host.Clock is not SteppedClock clock)
				throw new HostPortionException(host.Profile, "a replay sync check needs no display and a stepped clock.");

			var metadata = ReplayMetadata.Read(replayFile);
			if (metadata == null)
				return new ReplaySyncResult(replayFile, ReplaySyncOutcome.Unreadable, 0, 0);

			// The StartGame order falls back to the shellmap when the map is missing, which is not a
			// sync check.
			var map = Game.ModData.MapCache[metadata.GameInfo.MapUid];
			if (map.Status != MapStatus.Available)
				return new ReplaySyncResult(replayFile, ReplaySyncOutcome.MapMissing, 0, 0);

			// A Lua script calls the user interface, and a snapshot does not hold the state of a script.
			if (map.WorldActorInfo.HasTraitInfo<Scripting.IWorldScriptInfo>())
				return new ReplaySyncResult(replayFile, ReplaySyncOutcome.Unsupported, 0, 0, "the map runs a Lua script.");

			OrderManager? orderManager = null;
			var recordedFrames = 0;
			try
			{
				Game.JoinReplay(replayFile, startupActors);
				orderManager = Game.OrderManager;

				// FinalGameTick stays 0 when a game is quit instead of won, so the recording's last frame is
				// the end.
				recordedFrames = ((ReplayConnection)orderManager.Connection).TickCount;

				var stalled = 0;
				var stopped = false;
				var started = false;
				while (!stopped && !orderManager.IsOutOfSync && stalled < StallLimit)
				{
					Game.PerformDelayedActions();

					// Processes the lobby orders first, and with them the StartGame order
					// that builds the world.
					orderManager.TickImmediate();

					var world = orderManager.World;
					if (world != null && !started)
					{
						started = true;
						worldStarted?.Invoke(world);
					}

					if (world != null && orderManager.TryTick())
					{
						world.Tick();
						stalled = 0;
						stopped = afterTick != null && !afterTick(world);
					}
					else
						stalled++;

					clock.Advance(orderManager.SuggestedTimestep);
				}

				var outcome = orderManager.IsOutOfSync ? ReplaySyncOutcome.Desync
					: stopped ? ReplaySyncOutcome.Stopped
					: orderManager.NetFrameNumber >= recordedFrames ? ReplaySyncOutcome.Passed
					: ReplaySyncOutcome.Incomplete;

				return new ReplaySyncResult(replayFile, outcome, orderManager.NetFrameNumber, recordedFrames);
			}
			catch (HostPortionException e)
			{
				// The next replay's JoinReplay disposes this order manager, so the check goes on with the
				// next file.
				return Failed(ReplaySyncOutcome.Unsupported, e.Message);
			}
			catch (Exception e)
			{
				return Failed(ReplaySyncOutcome.Error, $"{e.GetType().Name}: {e.Message}");
			}

			ReplaySyncResult Failed(ReplaySyncOutcome outcome, string reason) =>
				new(replayFile, outcome, orderManager?.NetFrameNumber ?? 0, recordedFrames, reason);
		}
	}
}
