using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using EnderPearl.Crypto;
using EnderPearl.Crypto;
using EnderPearl.Net;
using global::Protocol;
using global::Protocol.Codec.Connection.Encryption;
using global::Protocol.Packets;
using EnderPearl.Logging;

namespace EnderPearl.Backend
{
	/// <summary>
	/// Drives the proxy-to-backend login sequence: network settings, the forged offline login, the
	/// encryption handshake, then hands both legs over to the relay handlers.
	/// </summary>
	public sealed class BackendInitialPacketHandler : IPacketHandler, IDisconnectNotifier
	{
		private readonly ProxyConnection connection;
		private readonly BackendSession backend;
		private readonly string backendName;
		private readonly BackendCommandRouter commandRouter;
		private readonly EnderPearl.Command.ProxyCommandRegistry commandRegistry;
		private readonly BackendDirectory backendDirectory;
		private readonly BackendSwitcher backendSwitcher;
		private readonly BackendActivation activation;
		private readonly Func<string, string> verifiedXuidLookup;
		private readonly BackendFailover failover;
		private readonly JoinFailover joinFailover;
		private readonly EnderPearl.Permission.ProxyPermissions permissions;
		private readonly EnderPearl.Command.ProxyPlayerEnum playerEnum;
		private readonly EnderPearl.Config.CommandsConfig commandsConfig;
		/// <summary>The command names this backend has taken over; resolved once, since backendName is fixed.</summary>
		private readonly System.Collections.Generic.IReadOnlySet<string> passthroughCommands;
		private bool warnedPreHandshakeDisconnect;

		public BackendInitialPacketHandler(
			ProxyConnection connection,
			BackendSession backend,
			string backendName,
			BackendCommandRouter commandRouter,
			EnderPearl.Command.ProxyCommandRegistry commandRegistry,
			BackendDirectory backendDirectory,
			BackendSwitcher backendSwitcher,
			BackendActivation activation,
			Func<string, string>? verifiedXuidLookup,
			BackendFailover failover,
			JoinFailover joinFailover,
			EnderPearl.Permission.ProxyPermissions permissions,
			EnderPearl.Command.ProxyPlayerEnum playerEnum,
			EnderPearl.Config.CommandsConfig? commandsConfig
		)
		{
			this.connection = connection;
			this.backend = backend;
			this.backendName = backendName;
			this.commandRouter = commandRouter;
			this.commandRegistry = commandRegistry;
			this.backendDirectory = backendDirectory;
			this.backendSwitcher = backendSwitcher;
			this.activation = activation;
			this.verifiedXuidLookup = verifiedXuidLookup ?? (_ => "");
			this.failover = failover;
			this.joinFailover = joinFailover;
			this.permissions = permissions;
			this.playerEnum = playerEnum;
			this.commandsConfig = commandsConfig ?? EnderPearl.Config.CommandsConfig.Defaults();
			this.passthroughCommands = this.commandsConfig.PassthroughFor(backendName);
		}

		public PacketSignal Handle(IPacket packet)
		{
			switch (packet)
			{
				case NetworkSettingsPacket p:
					return Handle(p);
				case PlayStatusPacket p:
					return Handle(p);
				case DisconnectPacket p:
					return Handle(p);
				case ServerToClientHandshakePacket p:
					return Handle(p);
				default:
					return PacketSignal.Unhandled;
			}
		}

		private PacketSignal Handle(NetworkSettingsPacket packet)
		{
			// Java: threshold > 0 -> compress with the negotiated algorithm, else NONE. (The client
			// leg differs by design: there the proxy itself sends threshold 0, which modern clients
			// treat as compress-everything.)
			backend.Session.mOpenCompression = true;
			backend.Session.mCompressionAlgorithm = packet.CompressionThreshold > 0
				? MapCompression(packet.CompressionAlgorithm)
				: CompressionAlgorithm.None;
			if (ProxyConnection.IsPacketTracingConfigured())
			{
				LogBackendLoginCapabilities(connection.BackendLogin);
			}
			backend.SendPacketImmediately(connection.BackendLogin);
			return PacketSignal.Handled;
		}

		internal static CompressionAlgorithm MapCompression(PacketCompressionAlgorithm algorithm)
		{
			return algorithm switch
			{
				PacketCompressionAlgorithm.Snappy => CompressionAlgorithm.Snappy,
				PacketCompressionAlgorithm.None => CompressionAlgorithm.None,
				_ => CompressionAlgorithm.ZLib
			};
		}

		private PacketSignal Handle(PlayStatusPacket packet)
		{
			if (ProxyConnection.IsPacketTracingConfigured())
			{
				Logger.Info($"Backend {backendName} sent PlayStatus before handshake: {packet.Status}.");
			}
			if (IsLoginFailure(packet.Status))
			{
				// The backend has already said no; failing here turns a version mismatch into an
				// immediate move to the next candidate instead of waiting out RakNet's timeout.
				Logger.Info(
					$"Backend {backendName} rejected the proxy login ({packet.Status}); treating as an immediate failure instead of waiting for the session to time out. If that backend runs a newer Minecraft version, set backend.{backendName}.protocol.");
				warnedPreHandshakeDisconnect = true;
				backend.SetDisconnectClientOnClose(false);
				var failure = new InvalidOperationException(
					"Backend " + backendName + " rejected the login: " + packet.Status);
				if (joinFailover == null || !joinFailover.HandleJoinFailure(connection, backendName, failure.Message))
				{
					activation.OnFailure(backend, failure);
				}
				backend.Disconnect("Login rejected");
			}
			return PacketSignal.Handled;
		}

		/// <summary>A PlayStatus arriving before the encryption handshake can only be bad news.</summary>
		private static bool IsLoginFailure(PlayStatus status)
		{
			return status != PlayStatus.LoginSuccess;
		}

		private PacketSignal Handle(DisconnectPacket packet)
		{
			string kickMessage = Messages.DisconnectMessage(packet);
			Logger.Info(
				$"Backend {backendName} disconnected before handshake: reason={packet.Reason} skipped={packet.Messages.Index == 1} message={kickMessage} filtered={Messages.DisconnectFilteredMessage(packet)}.");
			WarnPreHandshakeDisconnect(packet);
			warnedPreHandshakeDisconnect = true;
			 backend.SetDisconnectClientOnClose(false);
			 var failure = new InvalidOperationException(
			 "Backend " + backendName + " rejected the proxy login pre-handshake: " + packet.Reason+
			(string.IsNullOrEmpty(kickMessage) ? "" : " (" + kickMessage + ")"));
			     if (joinFailover == null || !joinFailover.HandleJoinFailure(connection, backendName, failure.Message))
				 {
				 activation.OnFailure(backend, failure);
			     }
			 backend.Disconnect("Login rejected");
			return PacketSignal.Handled;
		}

		private PacketSignal Handle(ServerToClientHandshakePacket packet)
		{
			try
			{
				string token = packet.HandshakeWebToken;
				IDictionary<string, System.Text.Json.JsonElement> headers = JwtHelper.DecodeHeaders(token);
				string x5u = headers["x5u"].GetString()!;
				byte[] x5uBytes = JwtHelper.Base64UrlDecode(x5u);

				byte[] serverKeyBytes = x5uBytes;
				byte[] salt = JwtHelper.Base64UrlDecode(
					System.Text.Json.JsonDocument.Parse(JwtHelper.DecodePayload(token)).RootElement.GetProperty("salt").GetString()!);
				byte[] key = BedrockCrypto.SecretKey(connection.KeyPair, serverKeyBytes, salt);

				backend.Session.mCryptoManager = new CryptoManager(key);
				backend.Session.mOpenCrypto = true;
				backend.SendPacketImmediately(new ClientToServerHandshakePacket());
				backend.SetPacketHandler(new BackendRelayPacketHandler(
					connection,
					backend,
					backendName,
					activation,
					new EnderPearl.Command.AvailableCommandsInjector(
						commandRegistry,
						VisibleBackendNames(),
						AdvertiseCommand,
						playerEnum
					),
					verifiedXuidLookup,
					failover,
					joinFailover,
					backendDirectory,
					backendSwitcher
				));
				activation.OnReady(backend);
				connection.Client().SetPacketHandler(new ClientRelayPacketHandler(
					connection,
					new EnderPearl.Command.ProxyCommandInterceptor(
						commandRegistry,
						passthroughCommands,
						commandsConfig.Qualifier
					),
					commandRouter
				));
				Logger.Info($"Connected player {connection.ClientLogin.AuthData.DisplayName} to backend {backendName}.");
				return PacketSignal.Handled;
			}
			catch (Exception exception)
			{
				activation.OnFailure(backend, exception);
				throw new InvalidOperationException("Unable to complete backend encryption handshake", exception);
			}
		}

		public void OnDisconnected(string reason)
		{
			Logger.Info($"Backend {backendName} closed before completing the encryption handshake: {reason}.");
			if (joinFailover != null && joinFailover.HandleJoinFailure(connection, backendName, reason))
			{
				return;
			}
			if (warnedPreHandshakeDisconnect)
			{
				return;
			}
			Logger.Info(
				$"WARNING: Backend {backendName} did not accept the proxy's offline backend login. If the backend has online mode enabled, proxied joins will not work; set the backend to offline mode and secure it with the EnderPearlGuard plugin.");
			// Nobody else has taken this failure (no disconnect packet arrived, no join candidate
			     // moved): notify the activation so a pending switch abandons right away instead of
      // leaving a dead pendingBackend reference feeding the relay's drop-gate.
			 activation.OnFailure(backend, new InvalidOperationException(
			 "Backend " + backendName + " closed before completing the encryption handshake: " + reason));
		}

		/// <summary>
		/// Whether a proxy command belongs in this player's command tree on this backend.
		///
		/// <para>Two separate reasons to leave one out. An admin command is hidden from a player who may
		/// not run it — cosmetic only, since the client can send any line it likes and the router
		/// re-checks on execution. A command this backend has taken over is hidden because the backend
		/// registers that name itself: injecting the proxy's entry alongside would either be dropped by
		/// the injector's de-duplication or, worse, advertise proxy semantics for a name the proxy is
		/// going to forward.</para>
		/// </summary>
		private bool AdvertiseCommand(string commandName)
		{
			return !passthroughCommands.Contains(commandName.ToLowerInvariant())
				&& permissions.Allows(
					connection.ClientLogin.AuthData.Xuid,
					connection.ClientLogin.AuthData.DisplayName,
					commandName
				);
		}

		/// <summary>
		/// The backends this player may send themselves to; a restricted backend is left out of the
		/// command tree entirely.
		/// </summary>
		private List<string> VisibleBackendNames()
		{
			string xuid = connection.ClientLogin.AuthData.Xuid;
			string displayName = connection.ClientLogin.AuthData.DisplayName;
			var visible = new List<string>();
			foreach (string name in backendDirectory.BackendNames())
			{
				if (permissions.MayJoinBackend(xuid, displayName, name))
				{
					visible.Add(name);
				}
			}
			return visible;
		}

		private void WarnPreHandshakeDisconnect(DisconnectPacket packet)
		{
			warnedPreHandshakeDisconnect = true;
			string text = string.Join(" ",
				packet.Reason.ToString(),
				Messages.DisconnectMessage(packet),
				Messages.DisconnectFilteredMessage(packet)
			).ToLowerInvariant();
			if (text.Contains("online")
				|| text.Contains("auth")
				|| text.Contains("xbox")
				|| text.Contains("login")
				|| text.Contains("notauthenticated"))
			{
				Logger.Info(
					$"WARNING: Backend {backendName} appears to require online/authenticated backend logins. The proxy uses a forged offline backend login, so this backend must have online mode disabled or proxied joins will not work.");
				return;
			}
			Logger.Info(
				$"WARNING: Backend {backendName} rejected the proxy login before the encryption handshake. If online mode is enabled on that backend, proxied joins will not work until it is disabled.");
		}

		private void LogBackendLoginCapabilities(LoginPacket login)
		{
			try
			{
				string payloadJson = JwtHelper.DecodePayload(Encoding.UTF8.GetString(login.ConnectionRequest.ToArray()));
				using var doc = System.Text.Json.JsonDocument.Parse(payloadJson);
				var skinData = doc.RootElement.Clone();
				Logger.Info(
					$"Sending backend LoginPacket: protocol={login.ClientNetworkVersion} GameVersion={Get(skinData, "GameVersion")} ServerAddress={Get(skinData, "ServerAddress")} CompatibleWithClientSideChunkGen={Get(skinData, "CompatibleWithClientSideChunkGen")} MaxViewDistance={Get(skinData, "MaxViewDistance")} DeviceOS={Get(skinData, "DeviceOS")}.");
			}
			catch (Exception exception)
			{
				Logger.Info(
					$"Sending backend LoginPacket: protocol={login.ClientNetworkVersion} clientJwtCapabilities=unreadable ({exception.GetType().Name}).");
			}
		}

		private static string Get(System.Text.Json.JsonElement element, string name)
		{
			return element.TryGetProperty(name, out System.Text.Json.JsonElement value) ? value.ToString() : "";
		}
	}
}
