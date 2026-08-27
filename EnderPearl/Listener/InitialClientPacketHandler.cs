using System;
using System.Threading;
using EnderPearl.Auth;
using EnderPearl.Diagnostics;
using EnderPearl.Backend;
using EnderPearl.Crypto;
using EnderPearl.Net;
using EnderPearl.Network;
using EnderPearl.Protocol;

using EnderPearl.Session;
using global::Protocol.Codec.Connection.Encryption;
using global::Protocol.Packets;
using EnderPearl.Logging;

namespace EnderPearl.Listener
{
	/// <summary>
	/// Drives a fresh client connection through the proxy's own login sequence: protocol negotiation,
	/// Xbox-live authentication, the proxy-side encryption handshake, then the backend join.
	/// </summary>
	public sealed class InitialClientPacketHandler : IPacketHandler
	{
		private readonly ListenerSession session;
		private readonly NetworkSettingsNegotiator networkSettingsNegotiator;
		private readonly BackendConnector backendConnector;
		private readonly ClientLoginAuthenticator authenticator;
		private readonly OnlineLoginForge onlineLoginForge;
		private readonly ConnectedPlayerRegistry connectedPlayers;
		private readonly Action playerCountChanged;
		private byte[]? clientEncryptionKey;
		private ProxyConnection? connection;
		private int joinStarted;

		public InitialClientPacketHandler(
			ListenerSession session,
			NetworkSettingsNegotiator networkSettingsNegotiator,
			BackendConnector backendConnector,
			ClientLoginAuthenticator authenticator,
			OnlineLoginForge onlineLoginForge,
			ConnectedPlayerRegistry connectedPlayers,
			Action playerCountChanged
		)
		{
			this.session = session;
			this.networkSettingsNegotiator = networkSettingsNegotiator;
			this.backendConnector = backendConnector;
			this.authenticator = authenticator;
			this.onlineLoginForge = onlineLoginForge;
			this.connectedPlayers = connectedPlayers;
			this.playerCountChanged = playerCountChanged;
		}

		public PacketSignal Handle(IPacket packet)
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
			NetworkSettingsNegotiationResult result = networkSettingsNegotiator.Handle(packet);
			if (result is NetworkSettingsNegotiationResult.Accepted accepted)
			{
				session.ClientCodec = accepted.ClientCodec;
				EnderPearl.Codec.CodecDefinitionState.InstallFallbacks(session);
				session.SendPacketImmediately(accepted.NetworkSettings);
				// The client enables the negotiated algorithm as soon as it receives NetworkSettings
				// (threshold 0 = compress every batch), so inbound parsing must decompress from here on.
				// Under-threshold batches still parse fine: they arrive with the raw (0xFF) prefix.
				session.Session.mOpenCompression = true;
				session.Session.mCompressionAlgorithm = BackendInitialPacketHandler.MapCompression(
					accepted.NetworkSettings.CompressionAlgorithm);
				if (ProxyConnection.IsPacketTracingConfigured())
				{
					Logger.Info(
						$"Accepted {session.RemoteEndPoint} using protocol {accepted.ClientCodec.ProtocolVersion}.");
				}
				return PacketSignal.Handled;
			}

			var rejected = (NetworkSettingsNegotiationResult.Rejected)result;
			session.SendPacketImmediately(rejected.PlayStatus);
			session.Disconnect("disconnectionScreen.outdatedClient");
			// The protocol number is the point of this line: a client newer than the proxy is how a new
			// Minecraft release announces itself, and that number is the first thing needed to add
			// support for it.
			Logger.Info(
				$"Rejected {session.RemoteEndPoint} with {rejected.PlayStatus.Status}: client protocol {rejected.RequestedProtocol}, proxy speaks up to {CanonicalProtocol.Newest().ProtocolVersion} ({CanonicalProtocol.Newest().MinecraftVersion}).");
			return PacketSignal.Handled;
		}

		private PacketSignal Handle(LoginPacket packet)
		{
			try
			{
				if (session.ClientCodec == null)
				{
					session.Disconnect("Network settings have not been negotiated");
					return PacketSignal.Handled;
				}

				ClientLogin clientLogin = authenticator.Authenticate(packet);
				ECDsaHolder keyPair = BedrockCrypto.CreateKeyPair();
				byte[] token = BedrockCrypto.RandomToken();
				clientEncryptionKey = BedrockCrypto.SecretKey(keyPair, clientLogin.IdentityPublicKey, token);

				connection = new ProxyConnection(
					session,
					new ProxySessionProfile(
						session.ClientCodec!,
						session.ClientCodec!,
						session.ClientCodec!,
						IdentityTranslator.INSTANCE
					),
					clientLogin,
					keyPair,
					onlineLoginForge.Forge(keyPair, clientLogin)
				);

				RegistrationResult registration = connectedPlayers.Register(connection);
				if (registration == RegistrationResult.DUPLICATE_XUID)
				{
					session.Disconnect("This Xbox account is already connected to the proxy");
					connection = null;
					clientEncryptionKey = null;
					return PacketSignal.Handled;
				}
				if (registration == RegistrationResult.FULL)
				{
					session.Disconnect("Proxy is full");
					connection = null;
					clientEncryptionKey = null;
					return PacketSignal.Handled;
				}
				session.ProxyConnection = connection;
				playerCountChanged();
				Logger.Info(
					$"Player {clientLogin.AuthData.DisplayName} (XUID {clientLogin.AuthData.Xuid}) joined the proxy from {connection.ClientAddress()}{(clientLogin.IsJavaEdition() ? " (a bridged edition)" : "")}.");

				ServerToClientHandshakePacket handshake = new ServerToClientHandshakePacket();
				handshake.HandshakeWebToken = BedrockCrypto.HandshakeJwt(keyPair, token);
				session.SendPacketImmediately(handshake);
				// Encryption arms only after the handshake itself went out in plaintext.
				session.Session.mCryptoManager = new CryptoManager(clientEncryptionKey);
				session.Session.mOpenCrypto = true;
				return PacketSignal.Handled;
			}
			catch (Exception exception)
			{
				session.Disconnect("Unable to authenticate with Xbox Live");
				Logger.Error($"Unable to authenticate client login: {exception}");
				return PacketSignal.Handled;
			}
		}

		private PacketSignal HandleHandshake()
		{
			if (connection == null || clientEncryptionKey == null)
			{
				session.Disconnect("Login handshake was not initialized");
				return PacketSignal.Handled;
			}
			if (Interlocked.Exchange(ref joinStarted, 1) == 1)
			{
				// A client that repeats the encryption handshake must not start a second join sequence
				// against the same session - the first dial owns the routing from here.
				Logger.Info(
					$"Ignoring repeated ClientToServerHandshake from {session.RemoteEndPoint}; join already in progress.");
				return PacketSignal.Handled;
			}

			try
			{
				backendConnector.Connect(connection);
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
				session.Disconnect(message);
				Logger.Error($"Unable to connect to backend server: {exception}");
			}
			return PacketSignal.Handled;
		}
	}
}
