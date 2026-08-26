using global::Protocol;
using global::Protocol.Packets;
using EnderPearl.Protocol;

namespace EnderPearl.Network
{
	/// <summary>The outcome of the pre-login network-settings handshake.</summary>
	public abstract record NetworkSettingsNegotiationResult
	{
		public sealed record Accepted(BedrockCodecInfo ClientCodec, NetworkSettingsPacket NetworkSettings)
			: NetworkSettingsNegotiationResult;

		/// <summary>
		/// The requested protocol is carried through so the rejection can name it: when a new Minecraft
		/// version lands, the number in that log line is the first thing needed to add support for it.
		/// </summary>
		public sealed record Rejected(int RequestedProtocol, PlayStatusPacket PlayStatus)
			: NetworkSettingsNegotiationResult;
	}

	public sealed class NetworkSettingsNegotiator
	{
		private readonly EnderPearl.Protocol.ProtocolNegotiator protocolNegotiator;
		private readonly PacketCompressionAlgorithm compressionAlgorithm;
		private readonly int compressionThreshold;

		public NetworkSettingsNegotiator(
			EnderPearl.Protocol.ProtocolNegotiator protocolNegotiator,
			PacketCompressionAlgorithm compressionAlgorithm,
			int compressionThreshold
		)
		{
			if (protocolNegotiator == null)
			{
				throw new ArgumentNullException(nameof(protocolNegotiator));
			}
			if (compressionThreshold < 0)
			{
				throw new ArgumentException("compressionThreshold cannot be negative");
			}
			this.protocolNegotiator = protocolNegotiator;
			this.compressionAlgorithm = compressionAlgorithm;
			this.compressionThreshold = compressionThreshold;
		}

		public NetworkSettingsNegotiationResult Handle(RequestNetworkSettingsPacket request)
		{
			EnderPearl.Protocol.ProtocolNegotiation negotiation = protocolNegotiator.Negotiate(request);
			if (negotiation is EnderPearl.Protocol.ProtocolNegotiation.Accepted accepted)
			{
				return new NetworkSettingsNegotiationResult.Accepted(
					accepted.ClientCodec,
					AcceptedNetworkSettings()
				);
			}

			var rejected = (EnderPearl.Protocol.ProtocolNegotiation.Rejected)negotiation;
			var playStatus = new PlayStatusPacket();
			playStatus.Status = rejected.Status;
			return new NetworkSettingsNegotiationResult.Rejected(rejected.RequestedProtocol, playStatus);
		}

		private NetworkSettingsPacket AcceptedNetworkSettings()
		{
			var packet = new NetworkSettingsPacket();
			packet.CompressionAlgorithm = compressionAlgorithm;
			packet.CompressionThreshold = (ushort)Math.Clamp(compressionThreshold, 0, ushort.MaxValue);
			packet.ClientThrottleEnabled = false;
			packet.ClientThrottleThreshold = 0;
			packet.ClientThrottleScalar = 0f;
			return packet;
		}
	}
}
