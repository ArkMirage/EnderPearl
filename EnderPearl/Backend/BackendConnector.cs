using System;
using System.Collections.Generic;
using System.Net;
using System.Threading.Tasks;
using EnderPearl.Auth;
using EnderPearl.Command;
using EnderPearl.Permission;
using EnderPearl.Config;
using EnderPearl.Protocol;
using EnderPearl.Player;
using global::Protocol.Packets;
using EnderPearl.Core;
using EnderPearl.Relay;
using EnderPearl.Server;

namespace EnderPearl.Backend
{
	/// <summary>
	/// Dials backends and drives a player onto them: the join try-list at login, /server-style switches,
	/// and the Failover path all end here.
	/// </summary>
		public sealed class BackendConnector
	{
		public required ProxyCommandManager CommandManager { get; init; }
		public required OnlineLoginForge OnlineLoginForge { get; init; }

		private readonly BackendProtocolDetector protocolDetector = new();
		private readonly ReconnectRoutes reconnectRoutes = new();
		private BackendSwitcher? switcherInstance;
		private BackendFailover? failoverInstance;
		private JoinFailover? joinFailoverInstance;

		public BackendSwitcher Switcher => switcherInstance ??= new BackendSwitcher(this, ProxyServer.Policy.BackendSwitch);

		public BackendFailover Failover => failoverInstance ??= new BackendFailover(ProxyServer.BackendDirectory, this, ProxyServer.Policy.Failover);

		public JoinFailover JoinFailover => joinFailoverInstance ??= new JoinFailover(this);

	/// <summary>
	/// Builds the derived components and runs their startup-time config checks. Call once, after the
	/// object initializer, before the first join is dialled.
	/// </summary>
		public BackendConnector Prepare()
		{
			switcherInstance = Switcher;
			failoverInstance = Failover;
			joinFailoverInstance = JoinFailover;
			return this;
		}

	/// <summary>
		/// Whether this player can only reach a backend by reconnecting.
		///
		/// <para>A Bedrock client fixes its block-id scheme from the StartGame it logged in with and cannot
		/// be told otherwise while it is playing, so a seamless handoff to a backend on the other scheme
		/// delivers chunks the client cannot decode: the player stands in an empty or scrambled world.
		/// Backends that hash block ids (every Bedrock server) and ones that number them by palette order
		/// (a Geyser instance fronting a Java server) are the two schemes in practice.</para>
		///
		/// <para>Answered false while either side is unknown. Guessing "reconnect" for an unvisited backend
		/// would put a loading screen in front of the ordinary same-scheme switch that makes up almost
		/// every move on a network; the scheme is learned from the first StartGame and persisted, so the
		/// uncertainty lasts one visit rather than one restart.</para>
		/// </summary>
		public bool NeedsReconnectToReach(ProxyConnection connection, BackendConfig backend)
		{
			bool? clientHashed = connection.ClientBlockIdsHashed();
			bool? backendHashed = BackendBlockSchemes.IsHashed(backend.Name);
			return clientHashed != null && backendHashed != null && clientHashed != backendHashed;
		}

		/// <summary>
		/// Sends the player back to the proxy to reach a backend a handoff cannot.
		///
		/// <para>The transfer names the proxy's own address, so the player never leaves it: the same
		/// listener answers, the same identity is verified again, and the backend stays unreachable from
		/// outside. What changes is that the client re-runs level init, which is the only way it will
		/// read a different block-id scheme.</para>
		/// </summary>
		public bool ReconnectTo(ProxyConnection connection, BackendConfig backend)
		{
			ReconnectAddress? target = ReconnectAddressOf(connection);
			if (target == null)
			{
				SendMessageTo(connection, "Unable to reach " + backend.Name + " from here. Reconnect and pick it from the server list.");
				Logger.Info(
					$"Cannot send {connection.ClientLogin.AuthData.DisplayName} to {backend.Name}: it needs a reconnect, and the proxy has no address to send them back to."
					+ " Set PublicAddress in the config.");
				return false;
			}

			reconnectRoutes.Remember(connection.ClientLogin.AuthData.Xuid, backend.Name);
			Logger.Info(
				$"Sending {connection.ClientLogin.AuthData.DisplayName} to {backend.Name} by reconnect via {target.Host}:{target.Port}"
				+ " (it numbers block ids differently to the world they logged into).");
			SendMessageTo(connection, "Taking you to " + backend.Name + "...");

			TransferPacket transfer = new TransferPacket
			{
				ServerAddress = target.Host,
				ServerPort = (ushort)target.Port
			};
			connection.Client.SendPacket(transfer);
			return true;
		}

		/// <summary>
		/// Where to tell the client to reconnect: the operator's PublicAddress if set, otherwise the
		/// address this player themselves connected with.
		///
		/// <para>The claim carries the port the player actually used, which is the right one to send them
		/// back to when the proxy sits behind a forwarded port. It is unsigned and a modified client can
		/// claim anything, which is harmless here: the worst outcome is that a player fails to reconnect
		/// to an address they supplied.</para>
		/// </summary>
		private ReconnectAddress? ReconnectAddressOf(ProxyConnection connection)
		{
			ReconnectAddress? configured = ReconnectAddress.Parse(ProxyServer.Config.PublicAddress, ProxyServer.Config.ListenAddress.Port);
			if (configured != null)
			{
				return configured;
			}
			return ReconnectAddress.Parse(ClientServerAddress(connection), ProxyServer.Config.ListenAddress.Port);
		}

		public ReconnectRoutes ReconnectRoutes => reconnectRoutes;

		/// <summary>False while the backend has never been seen, so the config key remains the way to say so.</summary>
		private bool DoesNotImplementSubChunks(BackendConfig backend)
		{
			bool? hashed = BackendBlockSchemes.IsHashed(backend.Name);
			return hashed != null && !hashed.Value;
		}

		private static void SendMessageTo(ProxyConnection connection, string message)
		{
			BackendSwitcher.SendMessage(connection, message);
		}

		/// <summary>Connects a joining player, walking the configured try-list if the first will not have them.</summary>
		public void Connect(ProxyConnection connection)
		{
			List<BackendConfig> candidates = JoinCandidates.Expand(
				InitialBackend(connection),
				ProxyServer.Policy.Join,
				ProxyServer.BackendDirectory);
			BackendConfig first = candidates[0];
			connection.BeginJoinSequence(candidates.GetRange(1, candidates.Count - 1));
			Connect(connection, first);
		}

		/// <summary>
		/// The backend a joining player lands on: their forced host if the address they connected with
		/// has one, otherwise the default backend.
		/// </summary>
		private BackendConfig InitialBackend(ProxyConnection connection)
		{
			// A player the proxy itself just asked to reconnect goes where they were headed, ahead of
			// any other rule: they did not choose to log in, they were sent round the loop to reach a
			// backend a handoff could not, and dropping them on the default one instead would look like
			// the move had simply failed.
			// (Java reached the same outcome via find(String.valueOf(take(...))): a null route became
			// the harmless literal "null", which simply missed the map. Here an absent route skips the
			// lookup - BackendDirectory.Find throws on blank names.)
			string? pendingRoute = reconnectRoutes.Take(connection.ClientLogin.AuthData.Xuid);
			BackendConfig? pending = pendingRoute == null ? null : ProxyServer.BackendDirectory.Find(pendingRoute);
			if (pending != null)
			{
				Logger.Info(
					$"Routing {connection.ClientLogin.AuthData.DisplayName} to backend {pending.Name}: completing the reconnect they were sent on.");
				return pending;
			}

			ForcedHostsConfig forcedHosts = ProxyServer.Policy.ForcedHosts;
			if (forcedHosts.IsEmpty())
			{
				return ProxyServer.BackendDirectory.DefaultBackend();
			}
			string serverAddress = ClientServerAddress(connection);
			if (forcedHosts.TryBackendFor(serverAddress, out string? forcedName))
			{
				BackendConfig? forced = ProxyServer.BackendDirectory.Find(forcedName!);
				if (forced != null)
				{
					Logger.Info(
						$"Routing {connection.ClientLogin.AuthData.DisplayName} to backend {forced.Name} by forced host '{serverAddress}'.");
					return forced;
				}
			}
			return ProxyServer.BackendDirectory.DefaultBackend();
		}

		private static string ClientServerAddress(ProxyConnection connection)
		{
			return connection.ClientLogin.SkinData.TryGetPropertyValue("ServerAddress", out var node)
				? node?.ToString() ?? ""
				: "";
		}

		public void Connect(ProxyConnection connection, BackendConfig backendConfig)
		{
			connection.BeginJoinAttempt();
			ConnectInternal(connection, backendConfig, true, new PlainActivation(connection, backendConfig, JoinFailover));
		}

		private sealed class PlainActivation : BackendActivation
		{
			private readonly ProxyConnection connection;
			private readonly BackendConfig backendConfig;
			private readonly JoinFailover JoinFailover;

			public PlainActivation(ProxyConnection connection, BackendConfig backendConfig, JoinFailover JoinFailover)
			{
				this.connection = connection;
				this.backendConfig = backendConfig;
				this.JoinFailover = JoinFailover;
			}

			public override void OnReady(BackendSession backend)
			{
				connection.SetBackend(backendConfig.Name, backend);
			}

			public override void OnStartGame(BackendSession backend)
			{
			}

			public override void OnFailure(BackendSession? backend, Exception exception)
			{
				// Covers both "the backend never answered" and "the handshake failed".
				string reason = exception is UnsupportedVersionPairException ? exception.Message : "unreachable";
				if (JoinFailover.HandleJoinFailure(connection, backendConfig.Name, reason))
				{
					return;
				}
				connection.Client.Disconnect(FailureMessage(exception, "Unable to connect to backend server"));
			}
		}

		/// <summary>
		/// Moves an already-playing client to another backend. Completes when the target's StartGame has
		/// arrived and the client has been handed over; faults when the switch fails.
		/// </summary>
		public Task ConnectForSwitch(ProxyConnection connection, BackendConfig backendConfig)
		{
			var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
			try
			{
				ConnectInternal(connection, backendConfig, false, new SwitchActivation(
					connection, backendConfig, completion));
			}
			catch (Exception exception)
			{
				// onFailure has already run and completed the future; this only covers a throw that
				// never reached it.
				completion.TrySetException(exception);
			}
			return completion.Task;
		}

		private sealed class SwitchActivation : BackendActivation
		{
			private readonly ProxyConnection connection;
			private readonly BackendConfig backendConfig;
			private readonly TaskCompletionSource completion;

			public SwitchActivation(ProxyConnection connection, BackendConfig backendConfig, TaskCompletionSource completion)
			{
				this.connection = connection;
				this.backendConfig = backendConfig;
				this.completion = completion;
			}

			public override void OnReady(BackendSession backend)
			{
				BackendSwitcher.SendMessage(connection, "Joining " + backendConfig.Name + "...");
			}

			public override void OnStartGame(BackendSession backend)
			{
				BackendSession? previous = connection.ReplaceBackend(backendConfig.Name, backend);
				if (previous != null && !ReferenceEquals(previous, backend) && previous.IsConnected)
				{
					previous.Disconnect("Switching backend");
				}
				BackendSwitcher.SendMessage(connection, "Connected to " + backendConfig.Name + ".");
				completion.TrySetResult();
			}

			public override void OnFailure(BackendSession? backend, Exception exception)
			{
				// The switch lock is the caller's; releasing it here would let a second switch start
				// in the middle of a retry sequence.
				connection.ClearPendingBackend(backend!);
				if (exception is UnsupportedVersionPairException unsupported)
				{
					BackendSwitcher.SendMessage(connection, unsupported.Message);
				}
				if (backend != null && backend.IsConnected)
				{
					backend.SetDisconnectClientOnClose(false);
					backend.DiscardInboundPackets();
					backend.Disconnect("Backend switch failed");
				}
				completion.TrySetException(exception);
			}
		}

		private void ConnectInternal(
			ProxyConnection connection,
			BackendConfig backendConfig,
			bool disconnectClientOnClose,
			BackendActivation activation
		)
		{
			Logger.Info(
				$"Dialing backend {backendConfig.Name} at {backendConfig.Address} (join={disconnectClientOnClose}) for {connection.ClientLogin.AuthData.DisplayName}.");
			try
			{
				BackendProtocol backendProtocol = ResolveBackendProtocol(backendConfig);
				connection.SetBackendLogin(BuildBackendLogin(connection, backendConfig, backendProtocol));
				if (ProxyConnection.IsPacketTracingConfigured())
				{
					Logger.Info(
						$"Selected backend {backendConfig.Name} protocol {VersionName(backendProtocol.MinecraftVersion, backendProtocol.ProtocolVersion)} for client {BedrockCodecInfo.Current}.");
				}
			}
			catch (UnsupportedVersionPairException exception)
			{
				activation.OnFailure(null, exception);
				throw;
			}

			BackendSession? createdSession = null;
			try
			{
				RakNet.Conn conn = Dial(backendConfig.Address);
				createdSession = new BackendSession(conn);
				createdSession.Connection = connection;
				createdSession.SetDisconnectClientOnClose(disconnectClientOnClose);
				// Inferred rather than configured wherever possible: a backend that numbers block ids by
				// palette order is not really a Bedrock server and does not implement the sub-chunk
				// system either. The config key stays as an override for a backend nobody has visited
				// yet, but an ordinary install never needs to set it.
				createdSession.SetDropSubChunkRequests(
					backendConfig.DropSubChunkRequests || DoesNotImplementSubChunks(backendConfig));
				if (!disconnectClientOnClose)
				{
					connection.SetPendingBackend(createdSession);
				}
				createdSession.SetPacketHandler(new BackendInitialPacketHandler
				{
					Connection = connection,
					Backend = createdSession,
					BackendName = backendConfig.Name,
					CommandRouter = new BackendCommandRouter(),
					CommandManager = CommandManager,
					BackendSwitcher = Switcher,
					Activation = activation,
					Failover = Failover,
					JoinFailover = JoinFailover
				});
				// Handler first, read loop second (Java's initSession ordering).
				createdSession.StartReading();
			}
			catch (Exception exception)
			{
				// onFailure must run anyway - it is the only report the caller gets, and skipping it
				// for the most ordinary failure of all ("the backend is down") leaves a player stuck.
				activation.OnFailure(createdSession, new InvalidOperationException(
					"Unable to connect to backend " + backendConfig.Address, exception));
				throw new InvalidOperationException("Unable to connect to backend " + backendConfig.Address, exception);
			}

			BackendSession backend = createdSession!;
			backend.SendPacketImmediately(new RequestNetworkSettingsPacket
			{
				ClientNetworkVersion = BedrockCodecInfo.Current.ProtocolVersion
			});
		}

		private RakNet.Conn Dial(IPEndPoint address)
		{
			var dialer = new RakNet.Dialer
			{
				ErrorLog = message => Logger.Info($"[Proxy To Server] {message}"),
				MaxMTU = 1492
			};
			// Java set RAK_CONNECT_TIMEOUT from switch.connectTimeoutMillis (default 5000ms); without
			// it a dead backend costs the RakNet library's full 10s session timeout per attempt, which
			// halves the number of tries that fit inside the /server retry window.
			return dialer.DialTimeoutInternal(address.ToString(), TimeSpan.FromMilliseconds(ProxyServer.Policy.BackendSwitch.ConnectTimeoutMillis));
		}

		private sealed record BackendProtocol(int ProtocolVersion, string MinecraftVersion);

		private BackendProtocol ResolveBackendProtocol(BackendConfig backendConfig)
		{
			// A backend's own setting wins over the global one. During an upgrade the fleet is always
			// mixed, so speaking the wrong version gets the login rejected as LOGIN_FAILED_CLIENT_OLD.
			if (backendConfig.Protocol != null)
			{
				return new BackendProtocol(backendConfig.Protocol.ProtocolVersion, backendConfig.Protocol.MinecraftVersion);
			}
			BedrockCodecInfo? overrideCodec = ProxyServer.Config.BackendProtocol;
			if (overrideCodec != null)
			{
				return new BackendProtocol(overrideCodec.ProtocolVersion, overrideCodec.MinecraftVersion);
			}

			BackendProtocolDetector.PongResult pong;
			try
			{
				pong = protocolDetector.Detect(backendConfig.Address);
			}
			catch (Exception exception)
			{
				// Probing is a convenience, not a requirement: some builds answer the unconnected ping
				// with a truncated pong that carries no version payload. Assume the backend matches the
				// proxy rather than refusing a join we have not actually tried.
				return AssumeSupportedProtocol(backendConfig, exception);
			}

			int protocolVersion = pong.ProtocolVersion;
			string minecraftVersion = pong.Version;
			if (protocolVersion != BedrockCodecInfo.Current.ProtocolVersion)
			{
				throw new UnsupportedVersionPairException(
					"Unsupported backend version "
					+ VersionName(minecraftVersion, protocolVersion)
					+ " on " + backendConfig.Name + "."
				);
			}
			return new BackendProtocol(protocolVersion, minecraftVersion);
		}

		private static BackendProtocol AssumeSupportedProtocol(BackendConfig backendConfig, Exception cause)
		{
			BedrockCodecInfo assumed = BedrockCodecInfo.Current;
			Logger.Info(
				$"WARNING: {backendConfig.Name} at {backendConfig.Address} did not answer the protocol probe ({cause.Message}). Assuming it speaks {assumed}; set backend.protocol in the config to skip probing.");
			return new BackendProtocol(assumed.ProtocolVersion, assumed.MinecraftVersion);
		}

		private static string VersionName(string? minecraftVersion, int protocolVersion)
		{
			if (string.IsNullOrWhiteSpace(minecraftVersion))
			{
				return "protocol " + protocolVersion;
			}
			return minecraftVersion + " (protocol " + protocolVersion + ")";
		}

		private static string FailureMessage(Exception exception, string fallback)
		{
			return exception is UnsupportedVersionPairException ? exception.Message : fallback;
		}

		/// <summary>
		/// Prints every field a backend can key persistent player data on, so rejoin-to-rejoin identity
		/// drift shows up as one diffable line instead of a support ticket about lost inventories.
		/// </summary>
		private static void LogBackendIdentity(ProxyConnection connection, BackendConfig backendConfig, int backendProtocolVersion)
		{
			if (!ProxyConnection.IsPacketTracingConfigured())
			{
				return;
			}
			AuthData authData = connection.ClientLogin.AuthData;
			Logger.Info(
				$"BACKEND IDENTITY for {backendConfig.Name} (protocol {backendProtocolVersion}): name={authData.DisplayName} xuid={authData.Xuid} identity={authData.Identity}");
		}

		private LoginPacket BuildBackendLogin(
			ProxyConnection connection,
			BackendConfig backendConfig,
			BackendProtocol backendProtocol
		)
		{
			int backendProtocolVersion = backendProtocol.ProtocolVersion;
			LogBackendIdentity(connection, backendConfig, backendProtocolVersion);
			// Java used getHostString()+":"+port - the host exactly as configured, no reverse lookup.
			string serverAddress = backendConfig.HostString + ":" + backendConfig.Address.Port;
			// This build only ever talks to 1.26.10+ servers, which expect the modern OIDC token format.
			LoginPacket backendLogin = OnlineLoginForge.Forge(
				connection.KeyPair,
				connection.ClientLogin,
				backendProtocol.MinecraftVersion,
				serverAddress,
				ProxyServer.MimicIdentity
			);
			return backendLogin;
		}
	}
}