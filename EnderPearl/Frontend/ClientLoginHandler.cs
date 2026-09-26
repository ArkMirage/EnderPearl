using System;
using System.Threading;
using EnderPearl.Auth;
using EnderPearl.Backend;
using EnderPearl.Core;
using EnderPearl.Protocol;
using EnderPearl.Player;
using EnderPearl.Server;
using global::Protocol.Codec.Connection.Encryption;
using global::Protocol.Packets;

namespace EnderPearl.Frontend
{
	/// <summary>
	/// Drives a fresh client connection through the proxy's own login sequence: protocol negotiation,
	/// Xbox-live authentication, the proxy-side encryption handshake, then the backend join.
	/// </summary>
	public sealed class ClientLoginHandler : PacketHandler
	{
		public required ListenerSession Session { get; init; }
		public required NetworkSettingsNegotiator Negotiator { get; init; }
		public required BackendConnector Connector { get; init; }
		public required ClientLoginAuthenticator Authenticator { get; init; }
		public required OnlineLoginForge LoginForge { get; init; }
		public required Action PlayerCountChanged { get; init; }

		private byte[]? clientEncryptionKey;
		private ProxyConnection? connection;
		private int joinStarted;
		private bool networkSettingsNegotiated;

		public override PacketSignal Handle(IPacket packet)
		{
			switch (packet)
			{
				case RequestNetworkSettingsPacket p:
					return Handle(p);
				case LoginPacket p:
					return Handle(p);
				case ClientToServerHandshakePacket:
					return HandleHandshake();
				default:
					return PacketSignal.Unhandled;
			}
		}

		private PacketSignal Handle(RequestNetworkSettingsPacket packet)
		{
			NetworkSettingsNegotiationResult result = Negotiator.Handle(packet);
			if (result is NetworkSettingsNegotiationResult.Accepted accepted)
			{
				networkSettingsNegotiated = true;
				Session.SendPacketImmediately(accepted.NetworkSettings);
				// The client enables the negotiated algorithm as soon as it receives NetworkSettings
				// (threshold 0 = compress every batch), so inbound parsing must decompress from here on.
				// Under-threshold batches still parse fine: they arrive with the raw (0xFF) prefix.
				Session.Session.mOpenCompression = true;
				Session.Session.mCompressionAlgorithm = BackendInitialPacketHandler.MapCompression(
					accepted.NetworkSettings.CompressionAlgorithm);
				if (ProxyConnection.IsPacketTracingConfigured())
				{
					int negotiated = (int)global::Protocol.ProtocolVersion.VERSION;
					Logger.Info(
						$"Accepted {Session.RemoteEndPoint} using protocol {negotiated}.");
				}
				return PacketSignal.Handled;
			}

			var rejected = (NetworkSettingsNegotiationResult.Rejected)result;
			Session.SendPacketImmediately(rejected.PlayStatus);
			Session.Disconnect("disconnectionScreen.outdatedClient");
			// The protocol number is the point of this line: a client newer than the proxy is how a new
			// Minecraft release announces itself, and that number is the first thing needed to add
			// support for it.
			int supportedProtocol = (int)global::Protocol.ProtocolVersion.VERSION;
			Logger.Info(
				$"Rejected {Session.RemoteEndPoint} with {rejected.PlayStatus.Status}: client protocol {rejected.RequestedProtocol}, proxy speaks protocol {supportedProtocol}.");
			return PacketSignal.Handled;
		}

		private PacketSignal Handle(LoginPacket packet)
		{
			try
			{
				if (!networkSettingsNegotiated)
				{
					Session.Disconnect("Network settings have not been negotiated");
					return PacketSignal.Handled;
				}

				ClientLogin clientLogin = Authenticator.Authenticate(packet);
				ECDsaHolder keyPair = BedrockCrypto.CreateKeyPair();
				byte[] token = BedrockCrypto.RandomToken();
				clientEncryptionKey = BedrockCrypto.SecretKey(keyPair, clientLogin.IdentityPublicKey, token);

				connection = new ProxyConnection
				{
					Client = Session,
					ClientLogin = clientLogin,
					KeyPair = keyPair,
					BackendLogin = LoginForge.Forge(keyPair, clientLogin)
				};

				RegistrationResult registration = ProxyServer.ConnectedPlayers.Register(connection);
				if (registration == RegistrationResult.DUPLICATE_XUID)
				{
					Session.Disconnect("This Xbox account is already connected to the proxy");
					connection = null;
					clientEncryptionKey = null;
					return PacketSignal.Handled;
				}
				if (registration == RegistrationResult.FULL)
				{
					Session.Disconnect("Proxy is full");
					connection = null;
					clientEncryptionKey = null;
					return PacketSignal.Handled;
				}
				Session.ProxyConnection = connection;
				PlayerCountChanged();
				Logger.Info(
					$"Player {clientLogin.AuthData.DisplayName} (XUID {clientLogin.AuthData.Xuid}) joined the proxy from {connection.ClientAddress()}.");

				ServerToClientHandshakePacket handshake = new ServerToClientHandshakePacket
				{
					HandshakeWebToken = BedrockCrypto.HandshakeJwt(keyPair, token)
				};
				Session.SendPacketImmediately(handshake);
				// Encryption arms only after the handshake itself went out in plaintext.
				Session.Session.mCryptoManager = new CryptoManager(clientEncryptionKey);
				Session.Session.mOpenCrypto = true;
				return PacketSignal.Handled;
			}
			catch (Exception exception)
			{
				Session.Disconnect("Unable to authenticate with Xbox Live");
				Logger.Error($"Unable to authenticate client login: {exception}");
				return PacketSignal.Handled;
			}
		}

		private PacketSignal HandleHandshake()
		{
			if (connection == null || clientEncryptionKey == null)
			{
				Session.Disconnect("Login handshake was not initialized");
				return PacketSignal.Handled;
			}
			if (Interlocked.Exchange(ref joinStarted, 1) == 1)
			{
				// A client that repeats the encryption handshake must not start a second join sequence
				// against the same session - the first dial owns the routing from here.
				Logger.Info(
					$"Ignoring repeated ClientToServerHandshake from {Session.RemoteEndPoint}; join already in progress.");
				return PacketSignal.Handled;
			}

			try
			{
				Connector.Connect(connection);
			}
			catch (Exception exception)
			{
				// connect() reports failure through the activation before it throws, so by now the join
				// try-list may already be working on the next candidate. Kicking here would end the
				// session it is trying to save.
				if (connection.IsJoinSequenceActive())
				{
					return PacketSignal.Handled;
				}
				string message = exception is UnsupportedVersionPairException unsupported
					? unsupported.Message
					: "Unable to connect to backend server";
				Session.Disconnect(message);
				Logger.Error($"Unable to connect to backend server: {exception}");
			}
			return PacketSignal.Handled;
		}
	}
}
