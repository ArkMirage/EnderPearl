using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using EnderPearl.Auth;
using EnderPearl.Core;
using global::Protocol;
using global::Protocol.Codec.Connection.Encryption;
using global::Protocol.Packets;
using EnderPearl.Player;
using EnderPearl.Relay;
using EnderPearl.Server;

namespace EnderPearl.Backend
{
	/// <summary>
	/// Drives the proxy-to-backend login sequence: network settings, the forged offline login, the
	/// encryption handshake, then hands both legs over to the relay handlers.
	/// </summary>
	public sealed class BackendInitialPacketHandler : PacketHandler
	{
		public required ProxyConnection Connection { get; init; }
		public required BackendSession Backend { get; init; }
		public required string BackendName { get; init; }
		public required BackendCommandRouter CommandRouter { get; init; }
		public required EnderPearl.Command.ProxyCommandManager CommandManager { get; init; }
		public required BackendSwitcher BackendSwitcher { get; init; }
		public required BackendActivation Activation { get; init; }
		public required BackendFailover Failover { get; init; }
		public required JoinFailover JoinFailover { get; init; }

		private System.Collections.Generic.IReadOnlySet<string> PassthroughCommands => ProxyServer.Commands.PassthroughFor(BackendName);

		private bool warnedPreHandshakeDisconnect;

		public override PacketSignal Handle(IPacket packet)
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
			Backend.Session.mOpenCompression = true;
			Backend.Session.mCompressionAlgorithm = packet.CompressionThreshold > 0
				? MapCompression(packet.CompressionAlgorithm)
				: CompressionAlgorithm.None;
			if (ProxyConnection.IsPacketTracingConfigured())
			{
				LogBackendLoginCapabilities(Connection.BackendLogin);
			}
			Backend.SendPacketImmediately(Connection.BackendLogin);
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
				Logger.Info($"Backend {BackendName} sent PlayStatus before handshake: {packet.Status}.");
			}
			if (IsLoginFailure(packet.Status))
			{
				// The backend has already said no; failing here turns a version mismatch into an
				// immediate move to the next candidate instead of waiting out RakNet's timeout.
				Logger.Info(
					$"Backend {BackendName} rejected the proxy login ({packet.Status}); treating as an immediate failure instead of waiting for the session to time out. If that backend runs a newer Minecraft version, set backend.{BackendName}.protocol.");
				warnedPreHandshakeDisconnect = true;
				Backend.SetDisconnectClientOnClose(false);
				var failure = new InvalidOperationException(
					"Backend " + BackendName + " rejected the login: " + packet.Status);
				if (!JoinFailover.HandleJoinFailure(Connection, BackendName, failure.Message))
				{
					Activation.OnFailure(Backend, failure);
				}
				Backend.Disconnect("Login rejected");
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
			string disconnectMessage = ProxyPackets.DisconnectMessage(packet);
			Logger.Info(
				$"Backend {BackendName} disconnected before handshake: reason={packet.Reason} skipped={packet.Messages.Index == 1} message={disconnectMessage} filtered={ProxyPackets.DisconnectFilteredMessage(packet)}.");
			WarnPreHandshakeDisconnect(packet);
			warnedPreHandshakeDisconnect = true;
			Backend.SetDisconnectClientOnClose(false);
			var failure = new InvalidOperationException(
				"Backend " + BackendName + " rejected the proxy login pre-handshake: " + packet.Reason +
				(string.IsNullOrEmpty(disconnectMessage) ? "" : " (" + disconnectMessage + ")"));
			if (!JoinFailover.HandleJoinFailure(Connection, BackendName, failure.Message))
			{
				Activation.OnFailure(Backend, failure);
			}
			Backend.Disconnect("Login rejected");
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
				byte[] key = BedrockCrypto.SecretKey(Connection.KeyPair, serverKeyBytes, salt);

				Backend.Session.mCryptoManager = new CryptoManager(key);
				Backend.Session.mOpenCrypto = true;
				Backend.SendPacketImmediately(new ClientToServerHandshakePacket());
				Backend.SetPacketHandler(new BackendRelayPacketHandler
				{
					Connection = Connection,
					Backend = Backend,
					BackendName = BackendName,
					Activation = Activation,
					CommandsInjector = new EnderPearl.Command.AvailableCommandsInjector(
						CommandManager,
						VisibleBackendNames(),
						AdvertiseCommand
					),
					Failover = Failover,
					JoinFailover = JoinFailover,
					BackendSwitcher = BackendSwitcher
				});
				Activation.OnReady(Backend);
				Connection.Client.SetPacketHandler(new ClientRelayPacketHandler(
					Connection,
					new EnderPearl.Command.ProxyCommandInterceptor(
						CommandManager,
						PassthroughCommands,
						ProxyServer.Commands.Qualifier
					),
					CommandRouter
				));
				Logger.Info($"Connected player {Connection.ClientLogin.AuthData.DisplayName} to backend {BackendName}.");
				return PacketSignal.Handled;
			}
			catch (Exception exception)
			{
				Activation.OnFailure(Backend, exception);
				throw new InvalidOperationException("Unable to complete backend encryption handshake", exception);
			}
		}

		public override void OnDisconnected(string reason)
		{
			Logger.Info($"Backend {BackendName} closed before completing the encryption handshake: {reason}.");
			if (JoinFailover.HandleJoinFailure(Connection, BackendName, reason))
			{
				return;
			}
			if (warnedPreHandshakeDisconnect)
			{
				return;
			}
			Logger.Info(
				$"WARNING: Backend {BackendName} did not accept the proxy's offline backend login. If the backend has online mode enabled, proxied joins will not work; set the backend to offline mode and secure it with the EnderPearlGuard plugin.");
			// Nobody else has taken this failure (no disconnect packet arrived, no join candidate
			// moved): notify the activation so a pending switch abandons right away instead of
			// leaving a dead pendingBackend reference feeding the relay's drop-gate.
			Activation.OnFailure(Backend, new InvalidOperationException(
				"Backend " + BackendName + " closed before completing the encryption handshake: " + reason));
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
			return !PassthroughCommands.Contains(commandName.ToLowerInvariant())
				&& ProxyServer.Permissions.Allows(
					Connection.ClientLogin.AuthData.Xuid,
					Connection.ClientLogin.AuthData.DisplayName,
					commandName
				);
		}

		/// <summary>
		/// The backends this player may send themselves to; a restricted backend is left out of the
		/// command tree entirely.
		/// </summary>
		private List<string> VisibleBackendNames()
		{
			string xuid = Connection.ClientLogin.AuthData.Xuid;
			string displayName = Connection.ClientLogin.AuthData.DisplayName;
			var visible = new List<string>();
			foreach (string name in ProxyServer.BackendDirectory.BackendNames())
			{
				if (ProxyServer.Permissions.MayJoinBackend(xuid, displayName, name))
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
				ProxyPackets.DisconnectMessage(packet),
				ProxyPackets.DisconnectFilteredMessage(packet)
			).ToLowerInvariant();
			if (text.Contains("online")
				|| text.Contains("auth")
				|| text.Contains("xbox")
				|| text.Contains("login")
				|| text.Contains("notauthenticated"))
			{
				Logger.Info(
					$"WARNING: Backend {BackendName} appears to require online/authenticated backend logins. The proxy uses a forged offline backend login, so this backend must have online mode disabled or proxied joins will not work.");
				return;
			}
			Logger.Info(
				$"WARNING: Backend {BackendName} rejected the proxy login before the encryption handshake. If online mode is enabled on that backend, proxied joins will not work until it is disabled.");
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
