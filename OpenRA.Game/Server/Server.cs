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
using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using OpenRA.FileFormats;
using OpenRA.GameSaves;
using OpenRA.Network;
using OpenRA.Primitives;
using OpenRA.Support;
using OpenRA.Traits;

namespace OpenRA.Server
{
	public enum ServerState
	{
		WaitingPlayers = 1,
		GameStarted = 2,
		ShuttingDown = 3
	}

	public enum ServerType
	{
		Local = 0,
		Skirmish = 1,
		Multiplayer = 2,
		Dedicated = 3
	}

	[IncludeStaticFluentReferences(typeof(PlayerMessageTracker), typeof(VoteKickTracker))]
	public sealed class Server
	{
		[FluentReference]
		const string CustomRules = "notification-custom-rules";

		[FluentReference]
		const string SnapshotTransferFailed = "notification-snapshot-transfer-failed";

		[FluentReference("frame")]
		const string SnapshotDoesNotMatch = "notification-snapshot-does-not-match";

		[FluentReference]
		const string SnapshotNotWritten = "notification-snapshot-not-written";

		[FluentReference]
		const string SnapshotOnlyHost = "notification-snapshot-only-host";

		[FluentReference("player", "save")]
		const string SnapshotLoading = "notification-snapshot-loading";

		[FluentReference]
		const string SnapshotPickSlots = "notification-snapshot-pick-slots";

		[FluentReference]
		const string SnapshotMapUnavailable = "notification-snapshot-map-unavailable";

		[FluentReference]
		const string SnapshotDifferentMap = "notification-cannot-load-different-map";

		[FluentReference]
		const string SnapshotNotFound = "notification-snapshot-not-found";

		[FluentReference("player", "reason")]
		const string SnapshotLoadFailed = "notification-snapshot-load-failed";

		[FluentReference("player")]
		const string SnapshotStartFailed = "notification-snapshot-start-failed";

		[FluentReference]
		const string SnapshotLoadInGame = "notification-snapshot-load-in-game";

		[FluentReference("save")]
		const string SnapshotReceived = "notification-snapshot-received";

		[FluentReference("save")]
		const string SnapshotLoadWaiting = "notification-snapshot-load-waiting";

		[FluentReference("save", "reason")]
		const string SnapshotLoadAbandoned = "notification-snapshot-load-abandoned";

		[FluentReference]
		const string SnapshotNoLobby = "notification-snapshot-no-lobby";

		[FluentReference]
		const string TwoHumansRequired = "notification-two-humans-required";

		[FluentReference]
		const string ErrorGameStarted = "notification-error-game-started";

		[FluentReference]
		const string RequiresPassword = "notification-requires-password";

		[FluentReference]
		const string IncorrectPassword = "notification-incorrect-password";

		[FluentReference]
		const string IncompatibleMod = "notification-incompatible-mod";

		[FluentReference]
		const string IncompatibleVersion = "notification-incompatible-version";

		[FluentReference]
		const string IncompatibleProtocol = "notification-incompatible-protocol";

		[FluentReference]
		const string Banned = "notification-you-were-banned";

		[FluentReference]
		const string TempBanned = "notification-you-were-temp-banned";

		[FluentReference]
		const string Full = "notification-game-full";

		[FluentReference("player")]
		const string Joined = "notification-joined";

		[FluentReference]
		const string RequiresAuthentication = "notification-requires-authentication";

		[FluentReference]
		const string NoPermission = "notification-no-permission-to-join";

		[FluentReference("command")]
		const string UnknownServerCommand = "notification-unknown-server-command";

		[FluentReference("player")]
		const string LobbyDisconnected = "notification-lobby-disconnected";

		[FluentReference("player")]
		const string PlayerDisconnected = "notification-player-disconnected";

		[FluentReference("player", "team")]
		const string PlayerTeamDisconnected = "notification-team-player-disconnected";

		[FluentReference("player")]
		const string ObserverDisconnected = "notification-observer-disconnected";

		[FluentReference("player")]
		const string NewAdmin = "notification-new-admin";

		[FluentReference]
		const string YouWereKicked = "notification-you-were-kicked";

		[FluentReference]
		const string GameStarted = "notification-game-started";

		public readonly MersenneTwister Random = new();
		public readonly ServerType Type;
		public bool IsMultiplayer => Type == ServerType.Dedicated || Type == ServerType.Multiplayer;

		public bool IsSinglePlayer => !IsMultiplayer && LobbyInfo.NonBotClients.Count() <= 1;

		public readonly List<Connection> Conns = [];

		public readonly Lock LobbyInfoLock = new();
		public Session LobbyInfo;
		public ServerSettings Settings;
		public ModData ModData;
		public List<string> TempBans = [];
		public string GeneratedMapData;

		// Managed by LobbyCommands
		public MapPreview Map;
		public readonly MapStatusCache MapStatusCache;

		const int SnapshotFrameMargin = 10;

		const int SnapshotLoadHoldTimeoutMs = 30000;

		public GameSave GameSave;

		string snapshotFilename;

		bool snapshotIsLocalLoad;

		bool snapshotClientsHoldFile;

		public bool LobbyRestoredFromSnapshot { get; private set; }

		readonly Dictionary<Connection, BlobReassembler> snapshotUploads = [];

		readonly Dictionary<string, HashSet<Connection>> snapshotPendingAcks = [];

		string snapshotLoadBlocked;

		long snapshotLoadBlockedSince;

		int lastSessionFrame;

		(Session.Global GlobalSettings, Dictionary<string, Session.Slot> Slots) lobbyBeforeLoad;

		public FrozenSet<string> MapPool;

		// Default to the next frame for ServerType.Local - MP servers take the value from the selected GameSpeed.
		public int OrderLatency = 1;

		readonly int randomSeed;
		readonly List<TcpListener> listeners = [];
		readonly TypeDictionary serverTraits = [];
		readonly PlayerDatabase playerDatabase;

		OrderBuffer orderBuffer;

		volatile ServerState internalState = ServerState.WaitingPlayers;

		readonly BlockingCollection<IServerEvent> events = [];

		ReplayRecorder recorder;
		GameInformation gameInfo;
		readonly List<GameInformation.Player> worldPlayers = [];
		readonly Stopwatch pingUpdated = Stopwatch.StartNew();

		public readonly VoteKickTracker VoteKickTracker;
		readonly PlayerMessageTracker playerMessageTracker;

		public ServerState State
		{
			get => internalState;
			set => internalState = value;
		}

		public static void SyncClientToPlayerReference(Session.Client c, PlayerReference pr)
		{
			if (pr == null)
				return;

			if (pr.LockFaction)
				c.Faction = pr.Faction;
			if (pr.LockSpawn)
				c.SpawnPoint = pr.Spawn;
			if (pr.LockTeam)
				c.Team = pr.Team;
			if (pr.LockHandicap)
				c.Handicap = pr.Handicap;

			c.Color = pr.LockColor ? pr.Color : c.PreferredColor;
		}

		public void Shutdown()
		{
			State = ServerState.ShuttingDown;
		}

		public void EndGame()
		{
			foreach (var t in serverTraits.WithInterface<IEndGame>())
				t.GameEnded(this);

			recorder?.Dispose();
			recorder = null;
		}

		// Craft a fake handshake request/response because that's the
		// only way to expose the Version and OrdersProtocol.
		public void RecordFakeHandshake()
		{
			var request = new HandshakeRequest
			{
				Mod = ModData.Manifest.Id,
				Version = ModData.Manifest.Metadata.Version,
			};

			recorder.ReceiveFrame(0, 0, new Order("HandshakeRequest", null, false)
			{
				Type = OrderType.Handshake,
				IsImmediate = true,
				TargetString = request.Serialize(),
			}.Serialize());

			var response = new HandshakeResponse()
			{
				Mod = ModData.Manifest.Id,
				Version = ModData.Manifest.Metadata.Version,
				OrdersProtocol = ProtocolVersion.Orders,
				Client = new Session.Client(),
			};

			recorder.ReceiveFrame(0, 0, new Order("HandshakeResponse", null, false)
			{
				Type = OrderType.Handshake,
				IsImmediate = true,
				TargetString = response.Serialize(),
			}.Serialize());
		}

		void MapStatusChanged(string uid, Session.MapStatus status)
		{
			lock (LobbyInfoLock)
			{
				if (LobbyInfo.GlobalSettings.Map == uid)
					LobbyInfo.GlobalSettings.MapStatus = status;

				SyncLobbyInfo();
			}
		}

		public Server(List<IPEndPoint> endpoints, ServerSettings settings, ModData modData, ServerType type)
		{
			Log.AddChannel("server", "server.log", true);

			SocketException lastException = null;
			foreach (var endpoint in endpoints)
			{
				var listener = new TcpListener(endpoint);
				try
				{
					try
					{
						listener.Server.SetSocketOption(SocketOptionLevel.IPv6, SocketOptionName.IPv6Only, 1);
					}
					catch (Exception ex) when (ex is SocketException || ex is ArgumentException)
					{
						Log.Write("server", $"Failed to set socket option on {endpoint}: {ex.Message}");
					}

					listener.Start();
					listeners.Add(listener);

					new Thread(() =>
					{
						while (true)
						{
							if (State != ServerState.WaitingPlayers)
							{
								listener.Stop();
								return;
							}

							// Use a 1s timeout so we can stop listening once the game starts
							if (listener.Server.Poll(1000000, SelectMode.SelectRead))
							{
								try
								{
									events.Add(new ConnectionConnectEvent(listener.AcceptSocket()));
								}
								catch (Exception)
								{
									// Ignore the exception that may be generated if the connection
									// drops while we are trying to connect
								}
							}
						}
					})
					{ Name = $"Connection listener ({listener.LocalEndpoint})", IsBackground = true }.Start();
				}
				catch (SocketException ex)
				{
					lastException = ex;
					Log.Write("server", $"Failed to listen on {endpoint}: {ex.Message}");
				}
			}

			if (listeners.Count == 0)
				throw lastException;

			Type = type;
			Settings = settings;

			Settings.Name = Game.Settings.SanitizedServerName(Settings.Name);

			ModData = modData;

			playerDatabase = modData.GetOrCreate<PlayerDatabase>();

			randomSeed = (int)DateTime.Now.ToBinary();

			if (IsMultiplayer && settings.EnableGeoIP)
				GeoIP.Initialize();

			if (IsMultiplayer)
				Nat.TryForwardPort(Settings.ListenPort, Settings.ListenPort);

			foreach (var trait in modData.Manifest.ServerTraits)
				serverTraits.Add(modData.ObjectCreator.CreateObject<ServerTrait>(trait));

			serverTraits.TrimExcess();

			MapStatusCache = new MapStatusCache(modData, MapStatusChanged, type == ServerType.Dedicated && settings.EnableLintChecks);

			playerMessageTracker = new PlayerMessageTracker(this, DispatchOrdersToClient, SendFluentMessageTo);
			VoteKickTracker = new VoteKickTracker(this);

			LobbyInfo = new Session
			{
				GlobalSettings =
				{
					RandomSeed = randomSeed,
					ServerName = settings.Name,
					EnableSingleplayer = settings.EnableSingleplayer || Type != ServerType.Dedicated,
					EnableMapGeneration = settings.EnableMapGeneration,
					EnableSyncReports = settings.EnableSyncReports,
					GameUid = Guid.NewGuid().ToString(),
					Dedicated = Type == ServerType.Dedicated
				}
			};

			if (Settings.RecordReplays && Type == ServerType.Dedicated)
			{
				recorder = new ReplayRecorder(() => Game.TimestampedFilename(extra: "-Server"));

				// We only need one handshake to initialize the replay.
				// Add it now, then ignore the redundant handshakes from each client
				RecordFakeHandshake();
			}

			new Thread(_ =>
			{
				// Note: at least one of these is required to set the initial LobbyInfo.Map and MapStatus
				foreach (var t in serverTraits.WithInterface<INotifyServerStart>())
					t.ServerStarted(this);

				Log.Write("server", $"Initial mod: {ModData.Manifest.Id}");
				Log.Write("server", $"Initial map: {LobbyInfo.GlobalSettings.Map}");

				while (true)
				{
					if (State != ServerState.ShuttingDown)
					{
						if (events.TryTake(out var e, 1000))
							e.Invoke(this);

						// PERF: Dedicated servers need to drain the action queue to remove references blocking the GC from cleaning up disposed objects.
						if (Type == ServerType.Dedicated)
							Game.PerformDelayedActions();

						foreach (var t in serverTraits.WithInterface<ITick>())
							t.Tick(this);

						if (State == ServerState.GameStarted)
						{
							foreach (var (playerIndex, scale) in orderBuffer.GetTickScales())
							{
								var frame = CreateTickScaleFrame(scale);
								var con = Conns.SingleOrDefault(c => c.PlayerIndex == playerIndex);

								if (con != null && con.Validated)
									DispatchFrameToClient(con, playerIndex, frame);
							}
						}

						ExpireSnapshotLoadHold();
					}

					if (State == ServerState.ShuttingDown)
					{
						EndGame();
						if (IsMultiplayer)
							Nat.TryRemovePortForward();
						break;
					}
				}

				foreach (var t in serverTraits.WithInterface<INotifyServerShutdown>())
					t.ServerShutdown(this);

				// Make sure to immediately close connections after the server is shutdown, we don't want to keep clients waiting
				foreach (var c in Conns)
					c.Dispose();

				Conns.Clear();
			})
			{ IsBackground = true, Name = "ServerThread" }.Start();
		}

		int nextPlayerIndex;
		public int ChooseFreePlayerIndex()
		{
			return nextPlayerIndex++;
		}

		internal void OnConnectionPacket(Connection conn, int frame, byte[] data)
		{
			events.Add(new ConnectionPacketEvent(conn, frame, data));
		}

		internal void OnConnectionPing(Connection conn, int[] pingHistory, byte queueLength)
		{
			events.Add(new ConnectionPingEvent(conn, pingHistory, queueLength));
		}

		internal void OnConnectionDisconnect(Connection conn)
		{
			events.Add(new ConnectionDisconnectEvent(conn));
		}

		void AcceptConnection(Socket socket)
		{
			if (State != ServerState.WaitingPlayers)
				return;

			// Validate player identity by asking them to sign a random blob of data
			// which we can then verify against the player public key database
			var token = Convert.ToBase64String(OpenRA.Exts.MakeArray(256, _ => (byte)Random.Next()));

			var newConn = new Connection(this, socket, token);
			try
			{
				// Send handshake and client index.
				var ms = new MemoryStream(8);
				ms.Write(ProtocolVersion.Handshake);
				ms.Write(newConn.PlayerIndex);
				newConn.TrySendData(ms.ToArray());

				// Dispatch a handshake order
				var request = new HandshakeRequest
				{
					Mod = ModData.Manifest.Id,
					Version = ModData.Manifest.Metadata.Version,
					AuthToken = token
				};

				DispatchOrdersToClient(newConn, 0, 0, new Order("HandshakeRequest", null, false)
				{
					Type = OrderType.Handshake,
					IsImmediate = true,
					TargetString = request.Serialize()
				}.Serialize());
			}
			catch (Exception e)
			{
				Log.Write("server", $"Handshake for client {newConn.EndPoint} failed: {e}");
			}

			Conns.Add(newConn);
		}

		void ValidateClient(Connection newConn, string data, string name)
		{
			try
			{
				if (State == ServerState.GameStarted)
				{
					Log.Write("server", $"Rejected connection from {newConn.EndPoint}; game is already started.");

					SendOrderTo(newConn, "ServerError", ErrorGameStarted);
					DropClient(newConn);
					return;
				}

				var handshake = HandshakeResponse.Deserialize(data, name);

				if (!string.IsNullOrEmpty(Settings.Password) && handshake.Password != Settings.Password)
				{
					var message = string.IsNullOrEmpty(handshake.Password) ? RequiresPassword : IncorrectPassword;
					SendOrderTo(newConn, "AuthenticationError", message);
					DropClient(newConn);
					return;
				}

				var ipAddress = ((IPEndPoint)newConn.EndPoint).Address;
				var client = new Session.Client
				{
					Name = OpenRA.Settings.SanitizedPlayerName(handshake.Client.Name),
					IPAddress = ipAddress.ToString(),
					AnonymizedIPAddress = IsMultiplayer && Settings.ShareAnonymizedIPs ? Session.AnonymizeIP(ipAddress) : null,
					Location = GeoIP.LookupCountry(ipAddress),
					Index = newConn.PlayerIndex,
					PreferredColor = handshake.Client.PreferredColor,
					Color = handshake.Client.Color,
					Faction = "Random",
					SpawnPoint = 0,
					Team = 0,
					Handicap = 0,
					State = Session.ClientState.Invalid,
				};

				if (ModData.Manifest.Id != handshake.Mod)
				{
					Log.Write("server", $"Rejected connection from {newConn.EndPoint}; mods do not match.");

					SendOrderTo(newConn, "ServerError", IncompatibleMod);
					DropClient(newConn);
					return;
				}

				if (ModData.Manifest.Metadata.Version != handshake.Version)
				{
					Log.Write("server", $"Rejected connection from {newConn.EndPoint}; Not running the same version.");

					SendOrderTo(newConn, "ServerError", IncompatibleVersion);
					DropClient(newConn);
					return;
				}

				if (handshake.OrdersProtocol != ProtocolVersion.Orders)
				{
					Log.Write("server", $"Rejected connection from {newConn.EndPoint}; incompatible Orders protocol version {handshake.OrdersProtocol}.");

					SendOrderTo(newConn, "ServerError", IncompatibleProtocol);
					DropClient(newConn);
					return;
				}

				// Check if IP is banned
				var bans = Settings.Ban.Union(TempBans);
				if (bans.Contains(client.IPAddress))
				{
					Log.Write("server", $"Rejected connection from {newConn.EndPoint}; Banned.");
					var message = Settings.Ban.Contains(client.IPAddress) ? Banned : TempBanned;
					SendOrderTo(newConn, "ServerError", message);
					DropClient(newConn);
					return;
				}

				void CompleteConnection()
				{
					lock (LobbyInfoLock)
					{
						client.Slot = LobbyInfo.FirstEmptySlot();
						client.IsAdmin = !LobbyInfo.Clients.Any(c => c.IsAdmin);

						if (client.IsObserver && !LobbyInfo.GlobalSettings.AllowSpectators)
						{
							SendOrderTo(newConn, "ServerError", Full);
							DropClient(newConn);
							return;
						}

						if (client.Slot != null)
							SyncClientToPlayerReference(client, Map.Players.Players[client.Slot]);
						else
							client.Color = Color.White;

						// Promote connection to a valid client
						LobbyInfo.Clients.Add(client);
						newConn.Validated = true;

						// Disable chat UI to stop the client sending messages that we know we will reject
						if (!client.IsAdmin && Settings.FloodLimitJoinCooldown > 0)
							playerMessageTracker.DisableChatUI(newConn, Settings.FloodLimitJoinCooldown);

						Log.Write("server", $"Client {newConn.PlayerIndex}: Accepted connection from {newConn.EndPoint}.");

						if (client.Fingerprint != null)
							Log.Write("server", $"Client {newConn.PlayerIndex}: Player fingerprint is {client.Fingerprint}.");

						foreach (var t in serverTraits.WithInterface<IClientJoined>())
							t.ClientJoined(this, newConn);

						var p = ModData.MapCache[LobbyInfo.GlobalSettings.Map];
						if (p.Class == MapClassification.Generated && !string.IsNullOrEmpty(GeneratedMapData))
							SendOrderTo(newConn, "GenerateMap", GeneratedMapData);

						SyncLobbyInfo();

						Log.Write("server", $"{client.Name} ({newConn.EndPoint}) has joined the game.");

						var otherConns = Conns.Where(c => c != newConn).ToArray();
						SendFluentMessage(otherConns.AsSpan(), Joined, "player", client.Name);

						if (Type == ServerType.Dedicated)
						{
							var motdFile = Path.Combine(Platform.SupportDir, "motd.txt");
							if (!File.Exists(motdFile))
								File.WriteAllText(motdFile, "Welcome, have fun and good luck!");

							var motd = File.ReadAllText(motdFile);
							if (!string.IsNullOrEmpty(motd))
								SendOrderTo(newConn, "Message", motd);

							if (!LobbyInfo.GlobalSettings.EnableSingleplayer)
								SendFluentMessageTo(newConn, TwoHumansRequired);
						}

						if (Type != ServerType.Local && (LobbyInfo.GlobalSettings.MapStatus & Session.MapStatus.UnsafeCustomRules) != 0)
							SendFluentMessageTo(newConn, CustomRules);
					}
				}

				if (!IsMultiplayer)
				{
					// Local servers can only be joined by the local client, so we can trust their identity without validation
					client.Fingerprint = handshake.Fingerprint;
					CompleteConnection();
				}
				else if (!string.IsNullOrEmpty(handshake.Fingerprint) && !string.IsNullOrEmpty(handshake.AuthSignature))
				{
					Task.Run(async () =>
					{
						PlayerProfile profile = null;

						try
						{
							var httpClient = HttpClientFactory.Create();
							var url = playerDatabase.Profile + handshake.Fingerprint;
							var httpResponseMessage = await httpClient.GetAsync(url);
							var result = await httpResponseMessage.Content.ReadAsStreamAsync();

							var yaml = MiniYaml.FromStream(result, url).First();
							if (yaml.Key == "Player")
							{
								profile = FieldLoader.Load<PlayerProfile>(yaml.Value);

								var publicKey = Encoding.ASCII.GetString(Convert.FromBase64String(profile.PublicKey));
								var parameters = CryptoUtil.DecodePEMPublicKey(publicKey);
								if (!profile.KeyRevoked && CryptoUtil.VerifySignature(parameters, newConn.AuthToken, handshake.AuthSignature))
								{
									client.Fingerprint = handshake.Fingerprint;
									Log.Write("server", $"{newConn.EndPoint} authenticated as {profile.ProfileName} (UID {profile.ProfileID})");
								}
								else if (profile.KeyRevoked)
								{
									profile = null;
									Log.Write("server", $"{newConn.EndPoint} failed to authenticate as {handshake.Fingerprint} (key revoked)");
								}
								else
								{
									profile = null;
									Log.Write("server", $"{newConn.EndPoint} failed to authenticate as {handshake.Fingerprint} (signature verification failed)");
								}
							}
							else
								Log.Write("server", $"{newConn.EndPoint} failed to authenticate as {handshake.Fingerprint} (invalid server response: `{yaml.Key}` is not `Player`)");
						}
						catch (Exception ex)
						{
							Log.Write("server", $"{newConn.EndPoint} failed to authenticate as {handshake.Fingerprint} (exception occurred)");
							Log.Write("server", ex.ToString());
						}

						events.Add(new CallbackEvent(() =>
						{
							var notAuthenticated = Type == ServerType.Dedicated && profile == null && (Settings.RequireAuthentication || Settings.ProfileIDWhitelist.Count > 0);
							var blacklisted = Type == ServerType.Dedicated && profile != null && Settings.ProfileIDBlacklist.Contains(profile.ProfileID);
							var notWhitelisted = Type == ServerType.Dedicated && Settings.ProfileIDWhitelist.Count > 0 &&
								(profile == null || !Settings.ProfileIDWhitelist.Contains(profile.ProfileID));

							if (notAuthenticated)
							{
								Log.Write("server", $"Rejected connection from {newConn.EndPoint}; Not authenticated.");
								SendOrderTo(newConn, "ServerError", RequiresAuthentication);
								DropClient(newConn);
							}
							else if (blacklisted || notWhitelisted)
							{
								if (blacklisted)
									Log.Write("server", $"Rejected connection from {newConn.EndPoint}; In server blacklist.");
								else
									Log.Write("server", $"Rejected connection from {newConn.EndPoint}; Not in server whitelist.");

								SendOrderTo(newConn, "ServerError", NoPermission);
								DropClient(newConn);
							}
							else
								CompleteConnection();
						}));
					});
				}
				else
				{
					if (Type == ServerType.Dedicated && (Settings.RequireAuthentication || Settings.ProfileIDWhitelist.Count > 0))
					{
						Log.Write("server", $"Rejected connection from {newConn.EndPoint}; Not authenticated.");
						SendOrderTo(newConn, "ServerError", RequiresAuthentication);
						DropClient(newConn);
					}
					else
						CompleteConnection();
				}
			}
			catch (Exception ex)
			{
				Log.Write("server", $"Dropping connection {newConn.EndPoint} because an error occurred:");
				Log.Write("server", ex.ToString());
				DropClient(newConn);
			}
		}

		static byte[] CreateFrame(int client, int frame, byte[] data)
		{
			var ms = new MemoryStream(data.Length + 12);
			ms.Write(data.Length + 4);
			ms.Write(client);
			ms.Write(frame);
			ms.Write(data);
			return ms.GetBuffer();
		}

		static byte[] CreateAckFrame(int frame, byte count)
		{
			var ms = new MemoryStream(14);
			ms.Write(6);
			ms.Write(0);
			ms.Write(frame);
			ms.WriteByte((byte)OrderType.Ack);
			ms.WriteByte(count);
			return ms.GetBuffer();
		}

		static byte[] CreateTickScaleFrame(float scale)
		{
			var ms = new MemoryStream(17);
			ms.Write(9);
			ms.Write(0);
			ms.Write(0);
			ms.WriteByte((byte)OrderType.TickScale);
			ms.Write(scale);
			return ms.GetBuffer();
		}

		void DispatchOrdersToClient(Connection c, int client, int frame, byte[] data)
		{
			DispatchFrameToClient(c, client, CreateFrame(client, frame, data));
		}

		void DispatchFrameToClient(Connection c, int client, byte[] frameData)
		{
			if (!c.TrySendData(frameData))
			{
				DropClient(c);
				Log.Write("server", $"Dropping client {client.ToString(CultureInfo.InvariantCulture)} because dispatching orders failed!");
			}
		}

		bool AnyUndefinedWinStates()
		{
			var lastTeam = -1;
			var remainingPlayers = gameInfo.Players.Where(p => p.Outcome == WinState.Undefined);
			foreach (var player in remainingPlayers)
			{
				if (lastTeam >= 0 && (player.Team != lastTeam || player.Team == 0))
					return true;

				lastTeam = player.Team;
			}

			return false;
		}

		void SetPlayerDefeat(int playerIndex)
		{
			var defeatedPlayer = worldPlayers[playerIndex];
			if (defeatedPlayer == null || defeatedPlayer.Outcome != WinState.Undefined)
				return;

			defeatedPlayer.Outcome = WinState.Lost;
			defeatedPlayer.OutcomeTimestampUtc = DateTime.UtcNow;

			// Set remaining players as winners if only one side remains
			if (!AnyUndefinedWinStates())
			{
				var now = DateTime.UtcNow;
				var remainingPlayers = gameInfo.Players.Where(p => p.Outcome == WinState.Undefined);
				foreach (var winner in remainingPlayers)
				{
					winner.Outcome = WinState.Won;
					winner.OutcomeTimestampUtc = now;
				}
			}
		}

		void DistributeSnapshot(string filename)
		{
			DistributeSnapshot(filename, null);
		}

		void DistributeSnapshot(string filename, Connection skip)
		{
			byte[] payload;
			try
			{
				payload = File.ReadAllBytes(Path.Combine(SavePaths.BaseSaveDirectory(ModData.Manifest), filename));
			}
			catch (Exception e)
			{
				Log.Write("server", $"Failed to read snapshot {filename} for distribution: {e.Message}");
				return;
			}

			var recipients = Conns.Where(c => c.Validated && c != skip).ToList();
			if (recipients.Count == 0)
				return;

			snapshotPendingAcks[filename] = [.. recipients];

			var parts = BlobTransfer.Split(payload);
			var recipientSpan = (ReadOnlySpan<Connection>)[.. recipients];
			for (var i = 0; i < parts.Chunks.Count; i++)
				DispatchServerOrdersToClients(recipientSpan, Order.FromTargetString("SnapshotChunk", parts.Chunks[i], true, (uint)i).Serialize());

			var end = new List<MiniYamlNode>
			{
				new("Count", parts.Chunks.Count.ToStringInvariant()),
				new("Length", parts.EncodedLength.ToStringInvariant()),
				new("Hash", parts.Hash),
				new("Filename", filename)
			};

			DispatchServerOrdersToClients(recipientSpan, Order.FromTargetString("SnapshotChunkEnd", end.WriteToString(), true).Serialize());

			Log.Write("server", $"Sending {(snapshotLoadBlocked == filename ? "the load" : "the save")} " +
				$"{filename} to {recipients.Count} client(s).");
		}

		bool AcceptsSnapshotUpload(Connection conn)
		{
			if (!LobbyInfo.GlobalSettings.EnableGameSaves)
				return false;

			if (State != ServerState.GameStarted && State != ServerState.WaitingPlayers)
				return false;

			var client = GetClient(conn);
			return client != null && client.IsAdmin;
		}

		bool AcceptsLobbySnapshot(Connection conn, byte[] payload)
		{
			SnapshotLobby lobby;
			try
			{
				using (var stream = new MemoryStream(payload))
				using (var reader = new SnapshotReader(stream))
					lobby = SnapshotLobby.Read(reader);
			}
			catch (Exception e)
			{
				Log.Write("server", $"Refused unreadable snapshot from client {conn.PlayerIndex}: {e.Message}");
				SendFluentMessageTo(conn, SnapshotTransferFailed);
				return false;
			}

			if (lobby == null)
			{
				Log.Write("server", $"Refused a snapshot with no lobby section from client {conn.PlayerIndex}.");
				SendFluentMessageTo(conn, SnapshotTransferFailed);
				return false;
			}

			var uid = lobby.GlobalSettings.Map;
			if (uid == null || ModData.MapCache[uid].Status != MapStatus.Available)
			{
				Log.Write("server", $"Refused a snapshot naming map {uid}, which this server does not have.");
				SendFluentMessageTo(conn, SnapshotMapUnavailable);
				return false;
			}

			return true;
		}

		void CompleteSnapshotUpload(Connection conn, BlobReassembler upload, Order o)
		{
			var nodes = new MiniYaml("", MiniYaml.FromString(o.TargetString, o.OrderString));
			var count = OpenRA.Exts.ParseInt32Invariant(nodes.NodeWithKey("Count").Value.Value);
			var length = OpenRA.Exts.ParseInt32Invariant(nodes.NodeWithKey("Length").Value.Value);
			var hash = nodes.NodeWithKey("Hash").Value.Value;
			var filename = SavePaths.SanitizeFileName(nodes.NodeWithKey("Filename").Value.Value);

			if (!upload.TryComplete(count, length, hash, out var payload, out var error))
			{
				Log.Write("server", $"Refused snapshot from client {conn.PlayerIndex}: {error}");
				SendFluentMessageTo(conn, SnapshotTransferFailed);
				return;
			}

			if (State == ServerState.GameStarted)
			{
				int savedFrame;
				int savedHash;
				ulong savedDefeatState;
				try
				{
					using (var stream = new MemoryStream(payload))
					using (var reader = new SnapshotReader(stream))
					{
						savedFrame = reader.Header.ReportedFrame;
						savedHash = reader.Header.ReportedSyncHash;
						savedDefeatState = reader.Header.ReportedDefeatState;
					}
				}
				catch (Exception e)
				{
					Log.Write("server", $"Refused unreadable snapshot from client {conn.PlayerIndex}: {e.Message}");
					SendFluentMessageTo(conn, SnapshotTransferFailed);
					return;
				}

				if (pendingSnapshotFrame is int requested && savedFrame != requested)
					Log.Write("server", $"Save was taken at frame {savedFrame}, but frame {requested} was last requested.");

				if (!syncForFrame.TryGetValue(savedFrame, out var sync))
				{
					Log.Write("server", $"Refused the save for frame {savedFrame}: this session has no sync hash " +
						"recorded for that frame, so the save cannot be checked against the world being played. " +
						"A save is verifiable only until a load restarts the session it was taken in.");
					SendFluentMessageTo(conn, SnapshotDoesNotMatch, ["frame", savedFrame]);
					return;
				}

				var expected = BitConverter.ToInt32(sync, 1);
				if (expected != savedHash)
				{
					Log.Write("server", $"Refused snapshot for frame {savedFrame}: sync hash {savedHash} does not match the recorded {expected}.");
					SendFluentMessageTo(conn, SnapshotDoesNotMatch, ["frame", savedFrame]);
					return;
				}

				var expectedDefeatState = BitConverter.ToUInt64(sync, 1 + 4);
				if (expectedDefeatState != savedDefeatState)
				{
					Log.Write("server", $"Refused snapshot for frame {savedFrame}: defeat state {savedDefeatState} does not match the recorded {expectedDefeatState}.");
					SendFluentMessageTo(conn, SnapshotDoesNotMatch, ["frame", savedFrame]);
					return;
				}

				pendingSnapshotFrame = null;
			}
			else if (!AcceptsLobbySnapshot(conn, payload))
				return;

			try
			{
				var baseSavePath = SavePaths.BaseSaveDirectory(ModData.Manifest);
				if (!Directory.Exists(baseSavePath))
					Directory.CreateDirectory(baseSavePath);

				SavePaths.ReplaceFile(Path.Combine(baseSavePath, filename), payload);
				Log.Write("server", $"Staged {filename} ({payload.Length} bytes) from client {conn.PlayerIndex}.");
			}
			catch (Exception e)
			{
				SavePaths.DiscardFailedWrite(Path.Combine(SavePaths.BaseSaveDirectory(ModData.Manifest), filename));
				Log.Write("server", $"Failed to write snapshot {filename}: {e.Message}");
				SendFluentMessageTo(conn, SnapshotNotWritten);
				return;
			}

			DistributeSnapshot(filename, conn);

			DispatchServerOrdersToClients(Order.FromTargetString("GameSaved", filename, true, o.ExtraData));
		}

		string UniqueSnapshotFilename(string filename)
		{
			try
			{
				var directory = SavePaths.BaseSaveDirectory(ModData.Manifest);
				return Path.GetFileName(SavePaths.UniqueFilePath(directory, filename));
			}
			catch (Exception e)
			{
				Log.Write("server", $"Could not find a free name for {filename}, using it as given: {e.Message}");
				return filename;
			}
		}

		bool MoveStagedSnapshot(string staged, string served, out string refusal)
		{
			refusal = null;

			try
			{
				var directory = SavePaths.BaseSaveDirectory(ModData.Manifest);
				File.Move(Path.Combine(directory, staged), Path.Combine(directory, served), true);
				return true;
			}
			catch (Exception e)
			{
				refusal = e.Message;
				return false;
			}
		}

		void OutOfSync(int frame)
		{
			Log.Write("server", $"Out of sync detected at frame {frame}, cancel replay recording");

			// Make sure the written file is not valid
			// TODO: Storing a server-side replay on desync would be extremely useful
			if (recorder != null)
			{
				recorder.Metadata = null;

				recorder.Dispose();
			}

			// Stop the recording
			recorder = null;
		}

		readonly Dictionary<int, byte[]> syncForFrame = [];
		int lastDefeatStateFrame;
		ulong lastDefeatState;

		int? pendingSnapshotFrame;

		void HandleSyncOrder(int frame, byte[] packet)
		{
			if (syncForFrame.TryGetValue(frame, out var existingSync))
			{
				if (packet.Length != existingSync.Length)
				{
					OutOfSync(frame);
					return;
				}

				for (var i = 0; i < packet.Length; i++)
				{
					if (packet[i] != existingSync[i])
					{
						OutOfSync(frame);
						return;
					}
				}
			}
			else
			{
				// Update player losses based on the new defeat state.
				// Do this once for the first player, the check above
				// guarantees a desync if any other player disagrees.
				var playerDefeatState = BitConverter.ToUInt64(packet, 1 + 4);
				if (frame > lastDefeatStateFrame && lastDefeatState != playerDefeatState)
				{
					var newDefeats = playerDefeatState & ~lastDefeatState;
					for (var i = 0; i < worldPlayers.Count; i++)
						if ((newDefeats & (1UL << i)) != 0)
							SetPlayerDefeat(i);

					lastDefeatState = playerDefeatState;
					lastDefeatStateFrame = frame;
				}

				syncForFrame.Add(frame, packet);
			}
		}

		public void DispatchOrdersToClients(Connection conn, int frame, byte[] data)
		{
			var from = conn.PlayerIndex;
			var frameData = CreateFrame(from, frame, data);
			foreach (var c in Conns.ToList())
				if (c != conn && c.Validated)
					DispatchFrameToClient(c, from, frameData);

			RecordOrder(frame, data, from);
		}

		void RecordOrder(int frame, byte[] data, int from)
		{
			recorder?.ReceiveFrame(from, frame, data);

			if (data.Length > 0 && data[0] == (byte)OrderType.SyncHash)
			{
				if (data.Length == Order.SyncHashOrderLength)
					HandleSyncOrder(frame, data);
				else
					Log.Write("server", $"Dropped sync order with length {data.Length} from client {from}. Expected length {Order.SyncHashOrderLength}.");
			}
		}

		public void DispatchServerOrdersToClients(Order order)
		{
			DispatchServerOrdersToClients(order.Serialize());
		}

		public void DispatchServerOrdersToClients(byte[] data, int frame = 0)
		{
			const int From = 0;
			var frameData = CreateFrame(From, frame, data);
			foreach (var c in Conns.ToList())
				if (c.Validated)
					DispatchFrameToClient(c, From, frameData);

			RecordOrder(frame, data, From);
		}

		public void DispatchServerOrdersToClients(ReadOnlySpan<Connection> conns, byte[] data, int frame = 0)
		{
			const int From = 0;
			var frameData = CreateFrame(From, frame, data);
			foreach (var c in conns)
				if (c.Validated)
					DispatchFrameToClient(c, From, frameData);

			RecordOrder(frame, data, From);
		}

		public void ReceiveOrders(Connection conn, int frame, byte[] data)
		{
			// Make sure we don't accidentally forward on orders from clients who we have just dropped
			if (!Conns.Contains(conn))
				return;

			if (frame == 0)
				InterpretServerOrders(conn, data);
			else
			{
				// Non-immediate orders must be projected into the future so that all players can
				// apply them on the same world tick. We can do this directly when forwarding the
				// packet on to other clients, but sending the same data back to the client that
				// sent it just to update the frame number would be wasteful. We instead send them
				// a separate Ack packet that tells them to apply the order from a locally stored queue.
				// TODO: Replace static latency with a dynamic order buffering system
				if (data.Length == 0 || data[0] != (byte)OrderType.SyncHash)
				{
					frame += OrderLatency;
					DispatchFrameToClient(conn, conn.PlayerIndex, CreateAckFrame(frame, 1));

					orderBuffer?.AddOrderTimestamp(conn.PlayerIndex);

					if (frame > lastSessionFrame)
						lastSessionFrame = frame;

					// Track the last frame for each client so the disconnect handling can write
					// an EndOfOrders marker with the correct frame number.
					// TODO: This should be handled by the order buffering system too
					conn.LastOrdersFrame = frame;
				}

				DispatchOrdersToClients(conn, frame, data);
			}

			GameSave?.DispatchOrders(conn, frame, data);
		}

		void InterpretServerOrders(Connection conn, byte[] data)
		{
			var ms = new MemoryStream(data);
			var br = new BinaryReader(ms);

			try
			{
				while (ms.Position < ms.Length)
				{
					var o = Order.Deserialize(null, br);
					if (o != null)
						InterpretServerOrder(conn, o);
				}
			}
			catch (EndOfStreamException) { }
			catch (NotImplementedException) { }
		}

		public void SendOrderTo(Connection conn, string order, string data)
		{
			DispatchOrdersToClient(conn, 0, 0, Order.FromTargetString(order, data, true).Serialize());
		}

		public void SendFluentMessage(string key, params object[] args)
		{
			var conns = Conns.ToArray();
			SendFluentMessage(conns, key, args);
		}

		public void SendFluentMessage(ReadOnlySpan<Connection> conns, string key, params object[] args)
		{
			var text = FluentMessage.Serialize(key, args);
			DispatchServerOrdersToClients(conns, Order.FromTargetString("FluentMessage", text, true).Serialize());

			if (Type == ServerType.Dedicated)
				WriteLineWithTimeStamp(FluentProvider.GetMessage(key, args));
		}

		public void SendFluentMessageTo(Connection conn, string key, object[] args = null)
		{
			var text = FluentMessage.Serialize(key, args);
			DispatchOrdersToClient(conn, 0, 0, Order.FromTargetString("FluentMessage", text, true).Serialize());
		}

		void WriteLineWithTimeStamp(string line)
		{
			Console.WriteLine($"[{DateTime.Now.ToString(Settings.TimestampFormat, CultureInfo.CurrentCulture)}] {line}");
		}

		void InterpretServerOrder(Connection conn, Order o)
		{
			lock (LobbyInfoLock)
			{
				// Only accept handshake responses from unvalidated clients
				// Anything else may be an attempt to exploit the server
				if (!conn.Validated)
				{
					if (o.OrderString == "HandshakeResponse")
						ValidateClient(conn, o.TargetString, o.OrderString);
					else
					{
						Log.Write("server", $"Rejected connection from {conn.EndPoint}; Order `{o.OrderString}` is not a `HandshakeResponse`.");
						DropClient(conn);
					}

					return;
				}

				switch (o.OrderString)
				{
					case "Command":
					{
						if (!InterpretCommand(o.TargetString, conn))
						{
							Log.Write("server", $"Unknown server command: {o.TargetString}");
							SendFluentMessageTo(conn, UnknownServerCommand, ["command", o.TargetString]);
						}

						break;
					}

					case "Chat":
					{
						if (!IsMultiplayer || !playerMessageTracker.IsPlayerAtFloodLimit(conn))
							DispatchOrdersToClients(conn, 0, o.Serialize());

						break;
					}

					case "GameSaveTraitData":
					{
						if (GameSave != null)
						{
							var data = MiniYaml.FromString(o.TargetString, o.OrderString).First();
							GameSave.AddTraitData(OpenRA.Exts.ParseInt32Invariant(data.Key), data.Value);
						}

						break;
					}

					case "RequestSnapshot":
					{
						if (State != ServerState.GameStarted || !LobbyInfo.GlobalSettings.EnableGameSaves)
							break;

						var requester = GetClient(conn);
						if (requester == null)
							break;

						if (!requester.IsAdmin)
						{
							SendFluentMessageTo(conn, SnapshotOnlyHost);
							break;
						}

						string filename;
						string autosave;
						try
						{
							var request = new MiniYaml("", MiniYaml.FromString(o.TargetString, o.OrderString));
							filename = request.NodeWithKey("Filename").Value.Value;
							autosave = request.NodeWithKey("Autosave").Value.Value;
						}
						catch (Exception e)
						{
							Log.Write("server", $"Malformed RequestSnapshot from client {conn.PlayerIndex}: {e.Message}");
							break;
						}

						var targetFrame = Conns.Count == 0 ? OrderLatency : Conns.Max(c => c.LastOrdersFrame) + OrderLatency + SnapshotFrameMargin;
						pendingSnapshotFrame = targetFrame;

						var announce = new List<MiniYamlNode>
						{
							new("Frame", targetFrame.ToStringInvariant()),
							new("Filename", filename),
							new("Autosave", autosave),
							new("Host", requester.Index.ToStringInvariant())
						};

						DispatchServerOrdersToClients(Order.FromTargetString("SaveSnapshot", announce.WriteToString(), true));
						break;
					}

					case "SnapshotChunk":
					{
						if (!AcceptsSnapshotUpload(conn))
							break;

						if (o.ExtraData == 0 || !snapshotUploads.TryGetValue(conn, out var upload))
							snapshotUploads[conn] = upload = new BlobReassembler();

						if (!upload.TryAdd((int)o.ExtraData, o.TargetString, out var chunkError))
						{
							Log.Write("server", $"Refused snapshot chunk from client {conn.PlayerIndex}: {chunkError}");
							SendFluentMessageTo(conn, SnapshotTransferFailed);
							snapshotUploads.Remove(conn);
						}

						break;
					}

					case "SnapshotChunkEnd":
					{
						if (!AcceptsSnapshotUpload(conn))
							break;

						if (snapshotUploads.Remove(conn, out var completed))
							CompleteSnapshotUpload(conn, completed, o);

						else
							Log.Write("server", $"Ignored a SnapshotChunkEnd from client {conn.PlayerIndex}: " +
								"no upload from that client is in progress.");

						break;
					}

					case "SnapshotLoadFailed":
					{
						if (!SavePaths.TrySanitizeFileName(o.TargetString, out var failed))
							break;

						if (!snapshotPendingAcks.TryGetValue(failed, out var stillWaiting))
							break;

						if (!stillWaiting.Remove(conn))
							break;

						var failing = GetClient(conn);
						Log.Write("server", $"Client {failing?.Index} ({failing?.Name}) could not receive snapshot {failed}.");

						if (snapshotLoadBlocked == failed)
							AbandonSnapshotLoad(failed, $"client {failing?.Index} ({failing?.Name}) could not receive it.");

						break;
					}

					case "SnapshotSaveFailed":
					{
						Log.Write("server", $"Client {conn.PlayerIndex} could not save: {o.TargetString}");
						break;
					}

					case "SnapshotReceived":
					{
						if (!SavePaths.TrySanitizeFileName(o.TargetString, out var received))
							break;

						if (!snapshotPendingAcks.TryGetValue(received, out var awaiting))
							break;

						var acking = GetClient(conn);
						if (acking == null)
							break;

						if (!awaiting.Remove(conn))
							break;

						var forLoad = snapshotLoadBlocked == received;
						var purpose = forLoad ? "load" : "save";

						Log.Write("server", $"Client {acking.Index} ({acking.Name}) holds the {purpose} " +
							$"{received}; {awaiting.Count} still outstanding.");

						if (awaiting.Count == 0)
						{
							snapshotPendingAcks.Remove(received);
							SendFluentMessage(SnapshotReceived, "save", received);

							if (snapshotLoadBlocked == received)
							{
								Log.Write("server", $"Every client holds {received}; restarting the session from it.");
								StartGame();
							}
							else
								Log.Write("server", $"Every client holds the save {received}; the match continues.");
						}

						break;
					}

					case "SnapshotStartFailed":
					{
						var failed = GetClient(conn);
						if (failed == null)
							break;

						Log.Write("server", $"Client {failed.Index} ({failed.Name}) could not start the game: {o.TargetString}");
						SendFluentMessage(SnapshotStartFailed, "player", failed.Name);
						break;
					}

					case "CreateGameSave":
					{
						if (GameSave != null)
						{
							if (!SavePaths.TrySanitizeFileName(o.TargetString, out var filename))
								break;

							var baseSavePath = SavePaths.BaseSaveDirectory(ModData.Manifest);

							if (!Directory.Exists(baseSavePath))
								Directory.CreateDirectory(baseSavePath);

							GameSave.Save(Path.Combine(baseSavePath, filename));
							DispatchServerOrdersToClients(Order.FromTargetString("GameSaved", filename, true, o.ExtraData));
						}

						break;
					}

					case "LoadGameSave":
					{
						snapshotIsLocalLoad = false;
						snapshotClientsHoldFile = false;

						if (State == ServerState.ShuttingDown || !LobbyInfo.GlobalSettings.EnableGameSaves)
						{
							Log.Write("server", $"Refused a load from client {conn.PlayerIndex}: state is {State}, " +
								$"saves are {(LobbyInfo.GlobalSettings.EnableGameSaves ? "enabled" : "disabled")}.");
							SendFluentMessageTo(conn, SnapshotLoadInGame);
							break;
						}

						var gameRunning = State == ServerState.GameStarted;

						var requester = GetClient(conn);
						if (requester?.IsAdmin != true)
						{
							SendFluentMessageTo(conn, SnapshotOnlyHost);
							break;
						}

						if (!SavePaths.TrySanitizeFileName(o.TargetString, out var filename))
							break;

						var savePath = Path.Combine(SavePaths.BaseSaveDirectory(ModData.Manifest), filename);

						if (!File.Exists(savePath))
						{
							Log.Write("server", $"Refused to load {filename}, which this server does not hold.");

							SendFluentMessageTo(conn, SnapshotNotFound);
							SendFluentMessage(SnapshotLoadFailed, "player", requester.Name,
								"reason", FluentProvider.GetMessage(SnapshotNotFound));
							break;
						}

						var format = SaveFileFormatDetector.Detect(savePath);

						if (gameRunning && format != SaveFileFormat.Snapshot)
						{
							Log.Write("server", $"Refused to load {filename} from client {conn.PlayerIndex}: " +
								"only a snapshot can restart a session that is already running.");
							SendFluentMessageTo(conn, SnapshotLoadInGame);
							break;
						}

						string saveMapUid;
						try
						{
							if (format == SaveFileFormat.Snapshot)
							{
								using var stream = File.OpenRead(savePath);
								using var reader = new SnapshotReader(stream);
								saveMapUid = reader.Header.MapUid;
							}
							else
								saveMapUid = new GameSave(savePath).GlobalSettings.Map;
						}
						catch (Exception e)
						{
							Log.Write("server", $"Refused to read the map of {filename} from client {conn.PlayerIndex}: {e.Message}");
							SendFluentMessageTo(conn, SnapshotTransferFailed);
							break;
						}

						var refusal = LoadPolicy.CanRestoreInto(
							saveMapUid, Map?.Uid, ModData.MapCache[saveMapUid].Status == MapStatus.Available);

						if (refusal != LoadRefusal.None)
						{
							Log.Write("server", $"Refused to load {filename} from client {conn.PlayerIndex}: {refusal} " +
								$"(save map {saveMapUid}, session map {Map?.Uid}).");
							SendFluentMessageTo(conn, refusal == LoadRefusal.DifferentMap
								? SnapshotDifferentMap
								: SnapshotMapUnavailable);
							break;
						}

						SendFluentMessage(SnapshotLoading, "player", requester.Name, "save", filename);

						Dictionary<string, SlotClient> slotClients;
						if (format == SaveFileFormat.Snapshot)
						{
							SnapshotLobby lobby;
							using (var stream = File.OpenRead(savePath))
							using (var reader = new SnapshotReader(stream))
								lobby = SnapshotLobby.Read(reader);

							if (lobby == null)
							{
								Log.Write("server", $"Refused to load {filename} from client {conn.PlayerIndex}: " +
									"the snapshot carries no lobby section.");
								SendFluentMessageTo(conn, SnapshotNoLobby);
								SendFluentMessage(SnapshotLoadFailed, "player", requester.Name,
									"reason", FluentProvider.GetMessage(SnapshotNoLobby));
								break;
							}

							GameSave = null;

							var uploaded = filename;

							snapshotIsLocalLoad = o.ExtraData == 1 && IsSinglePlayer;

							snapshotClientsHoldFile = snapshotIsLocalLoad
								|| LobbyInfo.NonBotClients.Count() <= 1;

							if (snapshotClientsHoldFile)
								snapshotFilename = uploaded;
							else
								snapshotFilename = UniqueSnapshotFilename(filename);

							if (!snapshotClientsHoldFile && snapshotFilename != uploaded)
							{
								if (!MoveStagedSnapshot(uploaded, snapshotFilename, out var moveFailure))
								{
									Log.Write("server", $"Refused to load {uploaded} from client {conn.PlayerIndex}: " +
										$"it could not be served as {snapshotFilename} ({moveFailure}).");
									SendFluentMessageTo(conn, SnapshotNotWritten);
									snapshotFilename = null;
									snapshotIsLocalLoad = false;
									snapshotClientsHoldFile = false;
									break;
								}

								Log.Write("server", $"The load's file is served as {snapshotFilename}, " +
									$"because {uploaded} is already on this server.");
							}
							else if (snapshotClientsHoldFile)
								Log.Write("server", $"The load's file is served as {snapshotFilename}, which every " +
									"client in this session already holds; no transfer is needed.");
							else
								Log.Write("server", $"The load's file is served as {snapshotFilename}.");

							LobbyRestoredFromSnapshot = true;

							if (gameRunning)
							{
								lobbyBeforeLoad = (LobbyInfo.GlobalSettings, LobbyInfo.Slots);
								Log.Write("server", "Kept the running session's lobby in case the load is abandoned.");
							}

							LobbyInfo.GlobalSettings = lobby.GlobalSettings;
							LobbyInfo.Slots = lobby.Slots;
							slotClients = lobby.SlotClients;
						}
						else
						{
							GameSave = new GameSave(savePath);
							snapshotFilename = null;
							LobbyInfo.GlobalSettings = GameSave.GlobalSettings;
							LobbyInfo.Slots = GameSave.Slots;
							slotClients = GameSave.SlotClients;
						}

						// Reassign clients to slots
						//  - Bot ordering is preserved
						//  - Humans are assigned on a first-come-first-serve basis
						//  - Leftover humans become spectators

						var seating = LoadSeating.Plan(gameRunning, LobbyInfo.Clients.Count(c => c.Bot == null));

						if (seating == SeatingPlan.FromSave)
						{
							// Start by removing all bots and assigning all players as spectators
							foreach (var c in LobbyInfo.Clients)
							{
								if (c.Bot != null)
									LobbyInfo.Clients.Remove(c);
								else
									c.Slot = null;
							}

							// Rebuild/remap the saved client state
							// TODO: Multiplayer saves should leave all humans as spectators so they can manually pick slots
							var adminClientIndex = LobbyInfo.Clients.First(c => c.IsAdmin).Index;

							var seatHumans = LobbyInfo.Clients.Count(c => c.Bot == null) == 1;

							foreach (var kv in slotClients)
							{
								if (kv.Value.Bot != null)
								{
									var bot = new Session.Client()
									{
										Index = ChooseFreePlayerIndex(),
										State = Session.ClientState.NotReady,
										BotControllerClientIndex = adminClientIndex
									};

									kv.Value.ApplyTo(bot);
									LobbyInfo.Clients.Add(bot);
								}
								else if (seatHumans)
								{
									// This will throw if the server doesn't have enough human clients to fill all player slots
									// See TODO above - this isn't a problem in practice because MP saves won't use this
									var client = LobbyInfo.Clients.FirstOrDefault(c => c.Slot == null && c.Bot == null);
									if (client != null)
										kv.Value.ApplyTo(client);
								}
							}

							if (!seatHumans)
								SendFluentMessage(SnapshotPickSlots);
						}

						SyncLobbyInfo();
						SyncLobbyClients();

						if (gameRunning && snapshotFilename != null)
						{
							Log.Write("server", $"Load handler is serving {snapshotFilename}: local load " +
								$"{snapshotIsLocalLoad}, clients hold it {snapshotClientsHoldFile}, " +
								$"players {LobbyInfo.NonBotClients.Count()}.");
							if (!snapshotClientsHoldFile)
								DistributeSnapshot(snapshotFilename);

							if (snapshotPendingAcks.TryGetValue(snapshotFilename, out var awaitingLoad))
							{
								snapshotLoadBlocked = snapshotFilename;
								snapshotLoadBlockedSince = Game.RunTime;
								SendFluentMessage(SnapshotLoadWaiting, "save", snapshotFilename);
								Log.Write("server", $"Holding the load of {snapshotFilename} until every client " +
									$"has confirmed it received the file; {awaitingLoad.Count} outstanding.");
							}
							else
							{
								StartGame();
							}
						}

						break;
					}

					case "GenerateMap":
					{
						if (!GetClient(conn).IsAdmin || State >= ServerState.GameStarted)
							break;

						if (!LobbyInfo.GlobalSettings.EnableMapGeneration)
							break;

						try
						{
							var yaml = new MiniYaml(o.OrderString, MiniYaml.FromString(o.TargetString, o.OrderString));
							var args = FieldLoader.Load<MapGenerationArgs>(yaml);
							var preview = ModData.MapCache[args.Uid];
							if (preview.Status != MapStatus.Available && preview.Class != MapClassification.Generated)
								preview.UpdateFromGenerationArgs(args);

							GeneratedMapData = o.TargetString;
							DispatchServerOrdersToClients(Order.FromTargetString("GenerateMap", o.TargetString, true));
						}
						catch (Exception e)
						{
							Console.WriteLine(e);
							throw;
						}

						break;
					}
				}
			}
		}

		public void ReceivePing(Connection conn, int[] pingHistory)
		{
			// Levels set relative to the default order lag of 3 net ticks (360ms)
			// TODO: Adjust this once dynamic lag is implemented
			var latency = pingHistory.Sum() / pingHistory.Length;

			var quality = latency < 240 ? Session.ConnectionQuality.Good :
				latency < 360 ? Session.ConnectionQuality.Moderate :
				Session.ConnectionQuality.Poor;

			lock (LobbyInfoLock)
			{
				foreach (var c in LobbyInfo.Clients)
					if (c.Index == conn.PlayerIndex || (c.Bot != null && c.BotControllerClientIndex == conn.PlayerIndex))
						c.ConnectionQuality = quality;

				// Update ping without forcing a full update
				// Note that syncing pings doesn't trigger INotifySyncLobbyInfo
				if (pingUpdated.ElapsedMilliseconds > 5000)
				{
					var nodes = new List<MiniYamlNode>();
					foreach (var c in LobbyInfo.Clients)
						nodes.Add(new MiniYamlNode($"ConnectionQuality@{c.Index}", FieldSaver.FormatValue(c.ConnectionQuality)));

					DispatchServerOrdersToClients(Order.FromTargetString("SyncConnectionQuality", nodes.WriteToString(), true));
					pingUpdated.Restart();
				}
			}
		}

		public Session.Client GetClient(Connection conn)
		{
			if (conn == null)
				return null;

			return LobbyInfo.ClientWithIndex(conn.PlayerIndex);
		}

		public bool HasClientWonOrLost(Session.Client client) =>
			worldPlayers.FirstOrDefault(p => p?.ClientIndex == client.Index)?.Outcome != WinState.Undefined;

		public void DropClient(Connection toDrop)
		{
			lock (LobbyInfoLock)
			{
				orderBuffer?.RemovePlayer(toDrop.PlayerIndex);
				Conns.Remove(toDrop);
				snapshotUploads.Remove(toDrop);

				var dropClient = LobbyInfo.Clients.FirstOrDefault(c => c.Index == toDrop.PlayerIndex);
				if (dropClient == null)
				{
					toDrop.Dispose();
					return;
				}

				if (State == ServerState.GameStarted)
				{
					if (dropClient.IsObserver)
						SendFluentMessage(ObserverDisconnected, "player", dropClient.Name);
					else if (dropClient.Team > 0)
						SendFluentMessage(PlayerTeamDisconnected, "player", dropClient.Name, "team", dropClient.Team);
					else
						SendFluentMessage(PlayerDisconnected, "player", dropClient.Name);
				}
				else
					SendFluentMessage(LobbyDisconnected, "player", dropClient.Name);

				LobbyInfo.Clients.RemoveAll(c => c.Index == toDrop.PlayerIndex);

				// Client was the server admin
				// TODO: Reassign admin for game in progress via an order
				if (Type == ServerType.Dedicated && dropClient.IsAdmin && State == ServerState.WaitingPlayers)
				{
					// Remove any bots controlled by the admin
					LobbyInfo.Clients.RemoveAll(c => c.Bot != null && c.BotControllerClientIndex == toDrop.PlayerIndex);

					var nextAdmin = LobbyInfo.Clients.Where(c1 => c1.Bot == null)
						.MinByOrDefault(c => c.Index);

					if (nextAdmin != null)
					{
						nextAdmin.IsAdmin = true;
						SendFluentMessage(NewAdmin, "player", nextAdmin.Name);
					}
				}

				var disconnectPacket = new MemoryStream(5);
				disconnectPacket.WriteByte((byte)OrderType.Disconnect);
				disconnectPacket.Write(toDrop.PlayerIndex);
				DispatchServerOrdersToClients(disconnectPacket.ToArray(), toDrop.LastOrdersFrame + 1);

				if (gameInfo != null)
					foreach (var player in gameInfo.Players.Where(p => p.ClientIndex == toDrop.PlayerIndex))
						player.DisconnectFrame = toDrop.LastOrdersFrame + 1;

				// All clients have left: clean up
				if (!Conns.Any(c => c.Validated))
					foreach (var t in serverTraits.WithInterface<INotifyServerEmpty>())
						t.ServerEmpty(this);

				if (Conns.Any(c => c.Validated) || Type == ServerType.Dedicated)
					SyncLobbyClients();

				if (Type != ServerType.Dedicated && dropClient.IsAdmin)
					Shutdown();
			}

			toDrop.Dispose();
		}

		public void SyncLobbyInfo()
		{
			lock (LobbyInfoLock)
			{
				if (State == ServerState.WaitingPlayers) // Do not do this while the game is running, it breaks things!
					DispatchServerOrdersToClients(Order.FromTargetString("SyncInfo", LobbyInfo.Serialize(), true));

				foreach (var t in serverTraits.WithInterface<INotifySyncLobbyInfo>())
					t.LobbyInfoSynced(this);
			}
		}

		public void SyncLobbyClients()
		{
			if (State != ServerState.WaitingPlayers)
				return;

			lock (LobbyInfoLock)
			{
				// TODO: Only need to sync the specific client that has changed to avoid conflicts!
				var clientData = LobbyInfo.Clients.ConvertAll(client => client.Serialize());

				DispatchServerOrdersToClients(Order.FromTargetString("SyncLobbyClients", clientData.WriteToString(), true));

				foreach (var t in serverTraits.WithInterface<INotifySyncLobbyInfo>())
					t.LobbyInfoSynced(this);

				// The full LobbyInfo includes ping info, so we can delay the next partial ping update
				// TODO: Replace the special-case ping updates with more general LobbyInfo delta updates
				pingUpdated.Restart();
			}
		}

		public void SyncLobbySlots()
		{
			if (State != ServerState.WaitingPlayers)
				return;

			lock (LobbyInfoLock)
			{
				// TODO: Don't sync all the slots if just one changed!
				var slotData = LobbyInfo.Slots.Select(slot => slot.Value.Serialize()).ToList();

				DispatchServerOrdersToClients(Order.FromTargetString("SyncLobbySlots", slotData.WriteToString(), true));

				foreach (var t in serverTraits.WithInterface<INotifySyncLobbyInfo>())
					t.LobbyInfoSynced(this);
			}
		}

		public void SyncLobbyGlobalSettings()
		{
			if (State != ServerState.WaitingPlayers)
				return;

			lock (LobbyInfoLock)
			{
				var sessionData = new List<MiniYamlNode> { LobbyInfo.GlobalSettings.Serialize() };

				DispatchServerOrdersToClients(Order.FromTargetString("SyncLobbyGlobalSettings", sessionData.WriteToString(), true));

				foreach (var t in serverTraits.WithInterface<INotifySyncLobbyInfo>())
					t.LobbyInfoSynced(this);
			}
		}

		void AbandonSnapshotLoad(string held, string reason)
		{
			snapshotPendingAcks.Remove(held);
			snapshotLoadBlocked = null;
			snapshotIsLocalLoad = false;
			snapshotClientsHoldFile = false;

			if (lobbyBeforeLoad != default)
			{
				LobbyInfo.GlobalSettings = lobbyBeforeLoad.GlobalSettings;
				LobbyInfo.Slots = lobbyBeforeLoad.Slots;
				lobbyBeforeLoad = default;
				SyncLobbyInfo();
				SyncLobbySlots();
				Log.Write("server", "Put the running session's lobby back after abandoning the load.");
			}

			Log.Write("server", $"Abandoned the load of {held}: {reason} The match continues.");
			SendFluentMessage(SnapshotLoadAbandoned, "save", held, "reason", reason);
		}

		void ExpireSnapshotLoadHold()
		{
			var held = snapshotLoadBlocked;
			if (held == null)
				return;

			if (Game.RunTime - snapshotLoadBlockedSince < SnapshotLoadHoldTimeoutMs)
				return;

			var outstanding = snapshotPendingAcks.TryGetValue(held, out var awaiting) ? awaiting.Count : 0;

			AbandonSnapshotLoad(held, $"{outstanding} client(s) never confirmed they received it within " +
				$"{SnapshotLoadHoldTimeoutMs} ms.");
		}

		public void StartGame()
		{
			lock (LobbyInfoLock)
			{
				WriteLineWithTimeStamp(FluentProvider.GetMessage(GameStarted));

				// Drop any players who are not ready
				foreach (var c in Conns.Where(c => !c.Validated || GetClient(c).IsInvalid).ToArray())
				{
					SendOrderTo(c, "ServerError", YouWereKicked);
					DropClient(c);
				}

				// Enable game saves for singleplayer missions only
				// TODO: Enable for multiplayer (non-dedicated servers only) once the lobby UI has been created
				LobbyInfo.GlobalSettings.EnableGameSaves = SnapshotPolicy.ServerOffersGameSaves(
					Type == ServerType.Dedicated, Settings.EnableGameSaves);

				worldPlayers.Clear();
				// Player list for win/loss tracking
				// HACK: NonCombatant and non-Playable players are set to null to simplify replay tracking
				// The null padding is needed to keep the player indexes in sync with world.Players on the clients
				// This will need to change if future code wants to use worldPlayers for other purposes
				var playerRandom = new MersenneTwister(LobbyInfo.GlobalSettings.RandomSeed);
				foreach (var cmpi in Map.WorldActorInfo.TraitInfos<ICreatePlayersInfo>())
					cmpi.CreateServerPlayers(Map, LobbyInfo, worldPlayers, playerRandom);

				gameInfo = new GameInformation()
				{
					Mod = Game.ModData.Manifest.Id,
					Version = Game.ModData.Manifest.Metadata.Version,
					MapUid = Map.Uid,
					MapTitle = Map.Title,
					StartTimeUtc = DateTime.UtcNow,
				};

				if (Map.Class == MapClassification.Generated)
					gameInfo.MapGenerationArgs = Map.GenerationArgs;

				// Replay metadata should only include the playable players
				foreach (var p in worldPlayers)
					if (p != null)
						gameInfo.Players.Add(p);

				if (recorder != null)
					recorder.Metadata = new ReplayMetadata(gameInfo);

				SyncLobbyInfo();

				var gameSpeeds = Game.ModData.GetOrCreate<GameSpeeds>();
				var gameSpeedName = LobbyInfo.GlobalSettings.OptionOrDefault("gamespeed", gameSpeeds.DefaultSpeed);

				var gameSpeed = gameSpeeds.Speeds[gameSpeedName];

				orderBuffer = new OrderBuffer();
				orderBuffer.Start(gameSpeed, Conns.Where(c => c.Validated).Select(c => c.PlayerIndex));

				syncForFrame.Clear();
				lastDefeatState = 0;
				lastDefeatStateFrame = 0;
				pendingSnapshotFrame = null;

				State = ServerState.GameStarted;

				if (IsMultiplayer)
					OrderLatency = gameSpeed.OrderLatency;

				LobbyInfo.GlobalSettings.GameTimestep = gameSpeed.Timestep;

				if (GameSave == null && LobbyInfo.GlobalSettings.EnableGameSaves && SnapshotPolicy.NeedsOrderStream(Map.WorldActorInfo))
					GameSave = new GameSave();

				var startingFrame = lastSessionFrame > 0 ? lastSessionFrame + 1 : 1;

				var startGameData = "";
				if (snapshotFilename != null)
				{
					if (snapshotLoadBlocked != snapshotFilename)
						DistributeSnapshot(snapshotFilename);

					startGameData = new List<MiniYamlNode>()
					{
						new("SnapshotPath", snapshotFilename)
					}.WriteToString();
				}
				else if (GameSave != null)
				{
					GameSave.StartGame(LobbyInfo, Map);
					if (GameSave.LastOrdersFrame >= 0)
					{
						startGameData = new List<MiniYamlNode>()
						{
							new("SaveLastOrdersFrame", GameSave.LastOrdersFrame.ToStringInvariant()),
							new("SaveSyncFrame", GameSave.LastSyncFrame.ToStringInvariant())
						}.WriteToString();
					}
				}

				var startGameNodes = new List<MiniYamlNode>
				{
					new("StartingFrame", startingFrame.ToStringInvariant())
				};

				if (snapshotFilename != null)
				{
					Log.Write("server", $"StartGame is serving {snapshotFilename}: local load " +
						$"{snapshotIsLocalLoad}, clients hold it {snapshotClientsHoldFile}, " +
						$"held {snapshotLoadBlocked == snapshotFilename}, players {LobbyInfo.NonBotClients.Count()}.");
					if (!snapshotClientsHoldFile && snapshotLoadBlocked != snapshotFilename)
						DistributeSnapshot(snapshotFilename);

					startGameNodes.Add(new MiniYamlNode("SnapshotPath", snapshotFilename));
				}

				if (!string.IsNullOrEmpty(startGameData))
					startGameNodes.AddRange(MiniYaml.FromString(startGameData, "StartGame"));

				DispatchServerOrdersToClients(Order.FromTargetString("StartGame", startGameNodes.WriteToString(), true));

				snapshotFilename = null;
				snapshotIsLocalLoad = false;
				snapshotClientsHoldFile = false;
				LobbyRestoredFromSnapshot = false;

				snapshotLoadBlocked = null;
				snapshotLoadBlockedSince = 0;

				lobbyBeforeLoad = default;

				snapshotUploads.Clear();

				snapshotPendingAcks.Clear();

				foreach (var t in serverTraits.WithInterface<IStartGame>())
					t.GameStarted(this);

				var firstFrame = startingFrame;
				if (GameSave != null && GameSave.LastOrdersFrame >= 0)
				{
					GameSave.ParseOrders(LobbyInfo, (frame, client, data) =>
					{
						foreach (var c in Conns)
							if (c.Validated)
								DispatchOrdersToClient(c, client, frame, data);
					});

					firstFrame += GameSave.LastOrdersFrame;
				}

				// ReceiveOrders projects player orders into the future so that all players can
				// apply them on the same world tick.
				// Clients require every frame to have an orders packet associated with it, so we must
				// inject an empty packet for each frame that we are skipping forwards.
				// TODO: Replace static latency with a dynamic order buffering system
				var conns = Conns.Where(c => c.Validated).ToList();
				foreach (var from in conns)
				{
					for (var i = 0; i < OrderLatency; i++)
					{
						from.LastOrdersFrame = firstFrame + i;
						var frameData = CreateFrame(from.PlayerIndex, from.LastOrdersFrame, []);
						foreach (var to in conns)
							DispatchFrameToClient(to, from.PlayerIndex, frameData);

						RecordOrder(from.LastOrdersFrame, [], from.PlayerIndex);
						GameSave?.DispatchOrders(from, from.LastOrdersFrame, []);
					}
				}
			}
		}

		public bool InterpretCommand(string command, Connection conn)
		{
			foreach (var t in serverTraits.WithInterface<IInterpretCommand>())
				if (t.InterpretCommand(this, conn, GetClient(conn), command))
					return true;

			return false;
		}

		public ConnectionTarget GetEndpointForLocalConnection()
		{
			var endpoints = new List<DnsEndPoint>();
			foreach (var listener in listeners)
			{
				var endpoint = (IPEndPoint)listener.LocalEndpoint;
				if (IPAddress.IPv6Any.Equals(endpoint.Address))
					endpoints.Add(new DnsEndPoint(IPAddress.IPv6Loopback.ToString(), endpoint.Port));
				else if (IPAddress.Any.Equals(endpoint.Address))
					endpoints.Add(new DnsEndPoint(IPAddress.Loopback.ToString(), endpoint.Port));
				else
					endpoints.Add(new DnsEndPoint(endpoint.Address.ToString(), endpoint.Port));
			}

			return new ConnectionTarget(endpoints);
		}

		public bool MapIsUnknown(string uid)
		{
			if (string.IsNullOrEmpty(uid))
				return true;

			var status = ModData.MapCache[uid].Status;
			return status != MapStatus.Available && status != MapStatus.DownloadAvailable;
		}

		public bool MapIsKnown(string uid)
		{
			if (string.IsNullOrEmpty(uid))
				return false;

			if (MapPool != null && !MapPool.Contains(uid))
				return false;

			var status = ModData.MapCache[uid].Status;
			return status == MapStatus.Available || status == MapStatus.DownloadAvailable;
		}

		interface IServerEvent { void Invoke(Server server); }

		sealed class ConnectionConnectEvent : IServerEvent
		{
			readonly Socket socket;
			public ConnectionConnectEvent(Socket socket)
			{
				this.socket = socket;
			}

			void IServerEvent.Invoke(Server server)
			{
				server.AcceptConnection(socket);
			}
		}

		sealed class ConnectionDisconnectEvent : IServerEvent
		{
			readonly Connection connection;
			public ConnectionDisconnectEvent(Connection connection)
			{
				this.connection = connection;
			}

			void IServerEvent.Invoke(Server server)
			{
				server.DropClient(connection);
			}
		}

		sealed class ConnectionPacketEvent : IServerEvent
		{
			readonly Connection connection;
			readonly int frame;
			readonly byte[] data;

			public ConnectionPacketEvent(Connection connection, int frame, byte[] data)
			{
				this.connection = connection;
				this.frame = frame;
				this.data = data;
			}

			void IServerEvent.Invoke(Server server)
			{
				server.ReceiveOrders(connection, frame, data);
			}
		}

		sealed class ConnectionPingEvent : IServerEvent
		{
			readonly Connection connection;
			readonly int[] pingHistory;

			// TODO: future net code changes
#pragma warning disable IDE0052
			readonly byte queueLength;
#pragma warning restore IDE0052

			public ConnectionPingEvent(Connection connection, int[] pingHistory, byte queueLength)
			{
				this.connection = connection;
				this.pingHistory = pingHistory;
				this.queueLength = queueLength;
			}

			void IServerEvent.Invoke(Server server)
			{
				server.ReceivePing(connection, pingHistory);
			}
		}

		sealed class CallbackEvent : IServerEvent
		{
			readonly Action action;

			public CallbackEvent(Action action)
			{
				this.action = action;
			}

			void IServerEvent.Invoke(Server server)
			{
				action();
			}
		}
	}
}
