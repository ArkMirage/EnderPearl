using System;
using System.Collections.Generic;
using System.Net;
using System.Threading.Tasks;
using EnderPearl.Auth;
using EnderPearl.Command;
using EnderPearl.Permission;
using EnderPearl.Config;
using EnderPearl.Diagnostics;
using EnderPearl.Crypto;
using EnderPearl.Crypto;
using EnderPearl.Palette;
using EnderPearl.Protocol;
using EnderPearl.Session;
using global::Protocol.Packets;
using EnderPearl.Logging;

namespace EnderPearl.Backend
{
	/// <summary>
	/// Dials backends and drives a player onto them: the join try-list at login, /server-style switches,
	/// and the failover path all end here.
	/// </summary>
	public sealed class BackendConnector
	{
		private readonly BackendDirectory backendDirectory;
		private readonly ProxyCommandRegistry commandRegistry;
		private readonly ProtocolRegistry protocolRegistry;
		private readonly BedrockCodecInfo? backendProtocolOverride;
		private readonly BackendProtocolDetector backendProtocolDetector;
		private readonly OnlineLoginForge onlineLoginForge;
		private readonly Func<string, string> verifiedXuidLookup;
		private readonly ProxyPolicy policy;
		private readonly BackendSwitchConfig switchConfig;
		private readonly ConnectedPlayerRegistry connectedPlayers;
		private readonly ProxyPermissions permissions;
		private readonly ProxyPlayerEnum playerEnum;
		private readonly BackendPaletteStore? paletteStore;
		private readonly string publicAddress;
		private readonly int listenPort;
		private readonly MojangMimicIdentity? mimicIdentity;
		private readonly ReconnectRoutes reconnectRoutes = new();
		private readonly BackendSwitcher switcher;
		private readonly BackendFailover failover;
		private readonly JoinFailover joinFailover;
		// Set once at startup, after the connector exists: NetworkCommands needs the switcher this
		// owns, and the connector needs the commands to build a router.
		private volatile NetworkCommands? networkCommands;

		public BackendConnector(
			BackendDirectory backendDirectory,
			ProxyCommandRegistry commandRegistry,
			MojangMimicIdentity? mimicIdentity,
			ProtocolRegistry protocolRegistry,
			BedrockCodecInfo? backendProtocolOverride,
			OnlineLoginForge onlineLoginForge,
			Func<string, string>? verifiedXuidLookup,
			ProxyPolicy policy,
			ConnectedPlayerRegistry connectedPlayers,
			ProxyPermissions permissions,
			ProxyPlayerEnum playerEnum
		)
			: this(backendDirectory, commandRegistry, mimicIdentity, protocolRegistry, backendProtocolOverride,
				onlineLoginForge, verifiedXuidLookup, policy, connectedPlayers,
				permissions, playerEnum, null, "", DEFAULT_LISTEN_PORT)
		{
		}

		public const int DEFAULT_LISTEN_PORT = 19132;

		public BackendConnector(
			BackendDirectory backendDirectory,
			ProxyCommandRegistry commandRegistry,
			MojangMimicIdentity? mimicIdentity,
			ProtocolRegistry protocolRegistry,
			BedrockCodecInfo? backendProtocolOverride,
			OnlineLoginForge onlineLoginForge,
			Func<string, string>? verifiedXuidLookup,
			ProxyPolicy policy,
			ConnectedPlayerRegistry connectedPlayers,
			ProxyPermissions permissions,
			ProxyPlayerEnum playerEnum,
			BackendPaletteStore? paletteStore,
			string? publicAddress,
			int listenPort
		)
		{
			this.mimicIdentity = mimicIdentity;
			this.paletteStore = paletteStore;
			this.publicAddress = publicAddress == null ? "" : publicAddress.Trim();
			this.listenPort = listenPort;
			this.backendDirectory = backendDirectory;
			this.commandRegistry = commandRegistry;
			this.protocolRegistry = protocolRegistry;
			this.backendProtocolOverride = backendProtocolOverride;
			this.backendProtocolDetector = new BackendProtocolDetector();
			this.onlineLoginForge = onlineLoginForge;
			this.verifiedXuidLookup = verifiedXuidLookup ?? (_ => "");
			this.policy = policy ?? ProxyPolicy.Defaults();
			switchConfig = this.policy.BackendSwitch;
			this.connectedPlayers = connectedPlayers;
			this.permissions = permissions ?? ProxyPermissions.InMemory(this.policy.Permissions);
			this.playerEnum = playerEnum;
			switcher = new BackendSwitcher(this, switchConfig);
			failover = new BackendFailover(backendDirectory, this, this.policy.Failover);
			joinFailover = new JoinFailover(this);
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
			bool? backendHashed = paletteStore == null ? null : paletteStore.BlockIdsHashed(backend.Name);
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
					+ " Set publicAddress in the config.");
				return false;
			}

			reconnectRoutes.Remember(connection.ClientLogin.AuthData.Xuid, backend.Name);
			Logger.Info(
				$"Sending {connection.ClientLogin.AuthData.DisplayName} to {backend.Name} by reconnect via {target.Host}:{target.Port}"
				+ " (it numbers block ids differently to the world they logged into).");
			SendMessageTo(connection, "Taking you to " + backend.Name + "...");

			TransferPacket transfer = new TransferPacket();
			transfer.ServerAddress = target.Host;
			transfer.ServerPort = (ushort)target.Port;
			connection.Client().SendPacket(transfer);
			return true;
		}

		/// <summary>
		/// Where to tell the client to reconnect: the operator's publicAddress if set, otherwise the
		/// address this player themselves connected with.
		///
		/// <para>The claim carries the port the player actually used, which is the right one to send them
		/// back to when the proxy sits behind a forwarded port. It is unsigned and a modified client can
		/// claim anything, which is harmless here: the worst outcome is that a player fails to reconnect
		/// to an address they supplied.</para>
		/// </summary>
		private ReconnectAddress? ReconnectAddressOf(ProxyConnection connection)
		{
			ReconnectAddress? configured = ReconnectAddress.Parse(publicAddress, listenPort);
			if (configured != null)
			{
				return configured;
			}
			return ReconnectAddress.Parse(ClientServerAddress(connection), listenPort);
		}

		public ReconnectRoutes ReconnectRoutes => reconnectRoutes;

		/// <summary>False while the backend has never been seen, so the config key remains the way to say so.</summary>
		private bool DoesNotImplementSubChunks(BackendConfig backend)
		{
			bool? hashed = paletteStore == null ? null : paletteStore.BlockIdsHashed(backend.Name);
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
				policy.Join,
				backendDirectory);
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
			BackendConfig? pending = pendingRoute == null ? null : backendDirectory.Find(pendingRoute);
			if (pending != null)
			{
				Logger.Info(
					$"Routing {connection.ClientLogin.AuthData.DisplayName} to backend {pending.Name}: completing the reconnect they were sent on.");
				return pending;
			}

			ForcedHostsConfig forcedHosts = policy.ForcedHosts;
			if (forcedHosts.IsEmpty())
			{
				return backendDirectory.DefaultBackend();
			}
			string serverAddress = ClientServerAddress(connection);
			if (forcedHosts.TryBackendFor(serverAddress, out string? forcedName))
			{
				BackendConfig? forced = backendDirectory.Find(forcedName!);
				if (forced != null)
				{
					Logger.Info(
						$"Routing {connection.ClientLogin.AuthData.DisplayName} to backend {forced.Name} by forced host '{serverAddress}'.");
					return forced;
				}
			}
			return backendDirectory.DefaultBackend();
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
			ConnectInternal(connection, backendConfig, true, new PlainActivation(connection, backendConfig, joinFailover));
		}

		private sealed class PlainActivation : BackendActivation
		{
			private readonly ProxyConnection connection;
			private readonly BackendConfig backendConfig;
			private readonly JoinFailover joinFailover;

			public PlainActivation(ProxyConnection connection, BackendConfig backendConfig, JoinFailover joinFailover)
			{
				this.connection = connection;
				this.backendConfig = backendConfig;
				this.joinFailover = joinFailover;
			}

			public void OnReady(BackendSession backend)
			{
				connection.SetBackend(backendConfig.Name, backend);
			}

			public void OnStartGame(BackendSession backend)
			{
			}

			public void OnFailure(BackendSession? backend, Exception exception)
			{
				// Covers both "the backend never answered" and "the handshake failed".
				string reason = exception is UnsupportedVersionPairException ? exception.Message : "unreachable";
				if (joinFailover.HandleJoinFailure(connection, backendConfig.Name, reason))
				{
					return;
				}
				connection.Client().Disconnect(FailureMessage(exception, "Unable to connect to backend server"));
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

			public void OnReady(BackendSession backend)
			{
				BackendSwitcher.SendMessage(connection, "Joining " + backendConfig.Name + "...");
			}

			public void OnStartGame(BackendSession backend)
			{
				BackendSession? previous = connection.ReplaceBackend(backendConfig.Name, backend);
				if (previous != null && !ReferenceEquals(previous, backend) && previous.IsConnected)
				{
					previous.Disconnect("Switching backend");
				}
				BackendSwitcher.SendMessage(connection, "Connected to " + backendConfig.Name + ".");
				completion.TrySetResult();
			}

			public void OnFailure(BackendSession? backend, Exception exception)
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

		public BackendFailover Failover() => failover;

		public ProxyPlayerEnum PlayerEnum() => playerEnum;

		public BackendSwitcher Switcher() => switcher;

		public void SetNetworkCommands(NetworkCommands networkCommands)
		{
			this.networkCommands = networkCommands;
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
			ProxySessionProfile previousProfile = connection.SessionProfile;
			ProtocolBinding? binding = null;
			var guardedActivation = new GuardedActivation(connection, previousProfile, disconnectClientOnClose, activation);
			try
			{
				BackendProtocol backendProtocol = ResolveBackendProtocol(backendConfig, connection);
				binding = ResolveBinding(connection, backendConfig, backendProtocol);
				connection.SetSessionProfile(ProxySessionProfile.From(binding));
				connection.SetBackendLogin(BuildBackendLogin(connection, binding, backendConfig, backendProtocol));
				if (ProxyConnection.IsPacketTracingConfigured())
				{
					Logger.Info(
						$"Selected backend {backendConfig.Name} protocol {VersionName(backendProtocol.MinecraftVersion, backendProtocol.ProtocolVersion)} for client {VersionName(connection.Client().ClientCodec!.MinecraftVersion, connection.Client().ClientCodec!.ProtocolVersion)}.");
				}
			}
			catch (UnsupportedVersionPairException exception)
			{
				// Java's catch restored the session profile unconditionally (the guarded activation's
				// own restore is switch-only); a failed binding resolution must not leave the newer
				// profile installed for the rest of the session.
				if (previousProfile != null)
				{
					connection.SetSessionProfile(previousProfile);
				}
				guardedActivation.OnFailure(null, exception);
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
				EnderPearl.Codec.CodecDefinitionState.InstallFallbacks(createdSession);
				createdSession.SetPacketHandler(new BackendInitialPacketHandler(
					connection,
					createdSession,
					backendConfig.Name,
					new BackendCommandRouter(
						backendDirectory,
						switcher,
						networkCommands,
						permissions,
						policy.Security
					),
					commandRegistry,
					backendDirectory,
					switcher,
					guardedActivation,
					verifiedXuidLookup,
					failover,
					joinFailover,
					permissions,
					playerEnum,
					policy.Commands
				));
				// Handler first, read loop second (Java's initSession ordering).
				createdSession.StartReading();
			}
			catch (Exception exception)
			{
				if (!disconnectClientOnClose && previousProfile != null)
				{
					connection.SetSessionProfile(previousProfile);
				}
				// onFailure must run anyway - it is the only report the caller gets, and skipping it
				// for the most ordinary failure of all ("the backend is down") leaves a player stuck.
				guardedActivation.OnFailure(createdSession, new InvalidOperationException(
					"Unable to connect to backend " + backendConfig.Address, exception));
				throw new InvalidOperationException("Unable to connect to backend " + backendConfig.Address, exception);
			}

			BackendSession backend = createdSession!;
			var request = new RequestNetworkSettingsPacket();
			request.ClientNetworkVersion = binding!.BackendCodec.ProtocolVersion;
			backend.SendPacketImmediately(request);
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
			return dialer.DialTimeoutInternal(address.ToString(), TimeSpan.FromMilliseconds(switchConfig.ConnectTimeoutMillis));
		}

		private ProtocolBinding ResolveBinding(
			ProxyConnection connection,
			BackendConfig backendConfig,
			BackendProtocol backendProtocol
		)
		{
			int clientProtocol = connection.Client().ClientCodec!.ProtocolVersion;
			if (!protocolRegistry.TryFindBinding(clientProtocol, backendProtocol.ProtocolVersion, out ProtocolBinding? binding))
			{
				throw new UnsupportedVersionPairException(
					"This client and backend version pair is not supported: client "
					+ VersionName(connection.Client().ClientCodec!.MinecraftVersion, clientProtocol)
					+ " cannot connect to backend "
					+ VersionName(backendProtocol.MinecraftVersion, backendProtocol.ProtocolVersion)
					+ "."
				);
			}
			return binding!;
		}

		private sealed record BackendProtocol(int ProtocolVersion, string MinecraftVersion);

		private BackendProtocol ResolveBackendProtocol(BackendConfig backendConfig, ProxyConnection connection)
		{
			// A backend's own setting wins over the global one. During an upgrade the fleet is always
			// mixed, so speaking the wrong version gets the login rejected as LOGIN_FAILED_CLIENT_OLD.
			if (backendConfig.Protocol != null)
			{
				return new BackendProtocol(backendConfig.Protocol.ProtocolVersion, backendConfig.Protocol.MinecraftVersion);
			}
			if (backendProtocolOverride != null)
			{
				return new BackendProtocol(backendProtocolOverride.ProtocolVersion, backendProtocolOverride.MinecraftVersion);
			}

			BackendProtocolDetector.PongResult pong;
			try
			{
				pong = backendProtocolDetector.Detect(backendConfig.Address);
			}
			catch (Exception exception)
			{
				// Probing is a convenience, not a requirement: some builds answer the unconnected ping
				// with a truncated pong that carries no version payload. Assume the backend matches the
				// client rather than refusing a join we have not actually tried.
				return AssumeClientProtocol(backendConfig, connection, exception);
			}

			int protocolVersion = pong.ProtocolVersion;
			string minecraftVersion = pong.Version;
			if (!protocolRegistry.TryFindBackendCodec(protocolVersion, out _))
			{
				throw new UnsupportedVersionPairException(
					"Unsupported backend version "
					+ VersionName(minecraftVersion, protocolVersion)
					+ " on " + backendConfig.Name + "."
				);
			}
			return new BackendProtocol(protocolVersion, minecraftVersion);
		}

		private BackendProtocol AssumeClientProtocol(
			BackendConfig backendConfig,
			ProxyConnection connection,
			Exception cause
		)
		{
			BedrockCodecInfo clientCodec = connection.Client().ClientCodec!;
			if (!protocolRegistry.TryFindBackendCodec(clientCodec.ProtocolVersion, out _))
			{
				throw new UnsupportedVersionPairException(
					"Unable to detect backend protocol for " + backendConfig.Name + " at " + backendConfig.Address + ".",
					cause);
			}
			Logger.Info(
				$"WARNING: {backendConfig.Name} at {backendConfig.Address} did not answer the protocol probe ({cause.Message}). Assuming it speaks the client's {VersionName(clientCodec.MinecraftVersion, clientCodec.ProtocolVersion)}; set backend.protocol in the config to skip probing.");
			return new BackendProtocol(clientCodec.ProtocolVersion, clientCodec.MinecraftVersion);
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
			ProtocolBinding binding,
			BackendConfig backendConfig,
			BackendProtocol backendProtocol
		)
		{
			int backendProtocolVersion = binding.BackendCodec.ProtocolVersion;
			LogBackendIdentity(connection, backendConfig, backendProtocolVersion);
			// Java used getHostString()+":"+port - the host exactly as configured, no reverse lookup.
			string serverAddress = backendConfig.HostString + ":" + backendConfig.Address.Port;
			// This build only ever talks to 1.26.10+ servers, which expect the modern OIDC token format.
			LoginPacket backendLogin = onlineLoginForge.Forge(
				connection.KeyPair,
				connection.ClientLogin,
				backendProtocol.MinecraftVersion,
				serverAddress,
				mimicIdentity
			);
			return backendLogin;
		}

		private sealed class GuardedActivation : BackendActivation
		{
			private readonly ProxyConnection connection;
			private readonly ProxySessionProfile? previousProfile;
			private readonly bool disconnectClientOnClose;
			private readonly BackendActivation inner;

			public GuardedActivation(ProxyConnection connection, ProxySessionProfile? previousProfile,
				bool disconnectClientOnClose, BackendActivation inner)
			{
				this.connection = connection;
				this.previousProfile = previousProfile;
				this.disconnectClientOnClose = disconnectClientOnClose;
				this.inner = inner;
			}

			public void OnReady(BackendSession backend) => inner.OnReady(backend);

			public void OnStartGame(BackendSession backend) => inner.OnStartGame(backend);

			public void OnFailure(BackendSession? backend, Exception exception)
			{
				if (!disconnectClientOnClose && previousProfile != null)
				{
					connection.SetSessionProfile(previousProfile);
				}
				inner.OnFailure(backend, exception);
			}
		}
	}
}
