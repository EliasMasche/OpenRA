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
using NUnit.Framework;
using OpenRA.GameSaves;
using OpenRA.Network;

namespace OpenRA.Test
{
	[TestFixture]
	[NonParallelizable]
	sealed class SnapshotSyncFrameTest
	{
		const string TestMap = "mods/ra/maps/agenda.oramap";

		sealed class RecordingConnection : IConnection
		{
			public readonly List<(int Frame, int SyncHash, ulong DefeatState)> Sent = [];
			readonly EchoConnection inner = new();

			int IConnection.LocalClientId => (inner as IConnection).LocalClientId;
			void IConnection.StartGame() => (inner as IConnection).StartGame();
			void IConnection.Send(int frame, IEnumerable<Order> orders) => (inner as IConnection).Send(frame, orders);
			void IConnection.SendImmediate(IEnumerable<Order> orders) => (inner as IConnection).SendImmediate(orders);
			void IConnection.Receive(OrderManager orderManager) => (inner as IConnection).Receive(orderManager);

			void IConnection.SendSync(int frame, int syncHash, ulong defeatState)
			{
				Sent.Add((frame, syncHash, defeatState));
				(inner as IConnection).SendSync(frame, syncHash, defeatState);
			}

			public void Dispose() => (inner as IDisposable).Dispose();
		}

		[TestCase(TestName = "A coordinated snapshot's header names the frame and hash the server actually recorded")]
		public void HeaderMatchesTheSyncPacketSentForItsFrame()
		{
			var map = HeadlessGame.LoadMap(TestMap);
			var session = HeadlessGame.CreateSession(map, seed: 4242, faction: "soviet", botTypes: "rush");
			var connection = new RecordingConnection();

			using (var game = HeadlessGame.CreateWorld(map, session, connection))
			{
				game.Tick(30);

				var targetFrame = game.OrderManager.NetFrameNumber + 5;

				using (var stream = new MemoryStream())
				{
					game.ScheduleSnapshot(targetFrame, stream);

					for (var i = 0; i < 40 && stream.Length == 0; i++)
						game.Tick(1);

					Assert.That(stream.Length, Is.GreaterThan(0), "The scheduled snapshot never fired.");

					stream.Position = 0;
					using (var reader = new SnapshotReader(stream))
					{
						var reported = connection.Sent.Find(s => s.Frame == reader.Header.ReportedFrame);
						Assert.That(reported, Is.Not.EqualTo(default((int, int, ulong))),
							$"The header names frame {reader.Header.ReportedFrame}, but no sync packet was ever sent for it — " +
							"the server has no key to verify this upload against.");

						Assert.That(reader.Header.ReportedSyncHash, Is.EqualTo(reported.SyncHash),
							"The header's reported hash disagrees with the sync packet sent for the frame it " +
							"names, so a server verifying an upload against its recorded sync hash would refuse it.");
					}
				}
			}
		}
	}
}
