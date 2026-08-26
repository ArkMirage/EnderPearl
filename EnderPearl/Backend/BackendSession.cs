using System;
using System.Threading;
using EnderPearl.Net;
using EnderPearl.Session;
using Protocol.Packets;
using RakNet;
using EnderPearl.Logging;

namespace EnderPearl.Backend
{
	/// <summary>
	/// The backend-facing leg of one proxied player: the RakNet connection the proxy dials to a BDS
	/// server. Java subclassed BedrockClientSession; here the transport lives in
	/// <see cref="PacketConnection"/> and this class adds the proxy-specific state and close semantics.
	/// </summary>
	public sealed class BackendSession : PacketConnection, IPacketHandler
	{
		private volatile bool disconnectClientOnClose = true;
		private volatile bool dropSubChunkRequests;

		public BackendSession(Conn conn)
		{
			Attach(conn);
		}

		public ProxyConnection? Connection { get; set; }

		public void SetDisconnectClientOnClose(bool value)
		{
			disconnectClientOnClose = value;
		}

		/// <summary>Whether SubChunkRequests are withheld from this backend; see BackendConfig.DropSubChunkRequests.</summary>
		public bool DropSubChunkRequests()
		{
			return dropSubChunkRequests;
		}

		public void SetDropSubChunkRequests(bool dropSubChunkRequests)
		{
			this.dropSubChunkRequests = dropSubChunkRequests;
		}

		public PacketSignal Handle(IPacket packet) => PacketSignal.Unhandled;

		/// <summary>
		/// Closes the backend leg without any Bedrock-level goodbye; the RakNet layer sends its own
		/// disconnect notification. Java's client-session disconnect does exactly this.
		/// </summary>
		public void Disconnect(string reason)
		{
			if (!IsConnected)
			{
				return;
			}
			Logger.Info(
				$"Closing backend leg to {RemoteEndPoint} ({reason}); sending RakNet disconnect notification.");
			CloseTransport();
		}

		protected override void OnBatchDecodeFailure(Exception exception)
		{
			Logger.Error(
				$"Backend {RemoteEndPoint} sent an undecodable batch: {exception.Message}");
			CloseTransport();
		}

		protected override void OnTransportClosed()
		{
			base.OnTransportClosed();
			var connection = Connection;
			if (disconnectClientOnClose && connection != null && connection.Client().IsConnected)
			{
				// During a join sequence the next candidate is already being tried, and kicking here
				// would end the session that sequence exists to save. JoinFailover disconnects instead,
				// once the list runs out.
				if (connection.IsJoinSequenceActive() && !connection.HasClientJoinedWorld())
				{
					return;
				}
				connection.Client().Disconnect("Backend disconnected");
			}
		}

		public override void Dispose()
		{
			CloseTransport();
		}
	}
}
