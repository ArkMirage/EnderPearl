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

	/// <summary>Checks whether a client's requested protocol can be served.</summary>
	public sealed class ProtocolNegotiator
	{
		public ProtocolNegotiation Negotiate(RequestNetworkSettingsPacket packet)
		{
			int requestedProtocol = packet.ClientNetworkVersion;
			int supportedProtocol = (int)global::Protocol.ProtocolVersion.VERSION;
			if (requestedProtocol == supportedProtocol)
			{
				return new ProtocolNegotiation.Accepted();
			}
			return new ProtocolNegotiation.Rejected(
				requestedProtocol,
				requestedProtocol > supportedProtocol
					? PlayStatus.LoginFailedServerOld
					: PlayStatus.LoginFailedClientOld
			);
		}
	}
}
