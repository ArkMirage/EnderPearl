using System;
using global::Protocol.Packets;
using PlayStatus = global::Protocol.PlayStatus;

namespace EnderPearl.Protocol
{
	/// <summary>
	/// The outcome of negotiating a client's requested protocol version.
	/// </summary>
	public abstract class ProtocolNegotiation
	{
		private ProtocolNegotiation()
		{
		}

		public sealed class Accepted : ProtocolNegotiation
		{
			public BedrockCodecInfo ClientCodec { get; }

			public Accepted(BedrockCodecInfo clientCodec)
			{
				ClientCodec = clientCodec ?? throw new ArgumentNullException(nameof(clientCodec));
			}
		}

		public sealed class Rejected : ProtocolNegotiation
		{
			public int RequestedProtocol { get; }

			public PlayStatus Status { get; }

			public Rejected(int requestedProtocol, PlayStatus status)
			{
				RequestedProtocol = requestedProtocol;
				Status = status;
			}
		}
	}

	/// <summary>Asks the registry whether a client's requested protocol can be served.</summary>
	public sealed class ProtocolNegotiator
	{
		private readonly ProtocolRegistry registry;

		public ProtocolNegotiator(ProtocolRegistry registry)
		{
			this.registry = registry ?? throw new ArgumentNullException(nameof(registry));
		}

		public ProtocolNegotiation Negotiate(RequestNetworkSettingsPacket packet)
		{
			int requestedProtocol = packet.ClientNetworkVersion;
			return registry.TryFindClientCodec(requestedProtocol, out BedrockCodecInfo? clientCodec)
				? new ProtocolNegotiation.Accepted(clientCodec!)
				: new ProtocolNegotiation.Rejected(requestedProtocol, registry.UnsupportedStatus(requestedProtocol));
		}
	}
}
