using System;
using System.Net;
using EnderPearl.Backend;
using EnderPearl.Net;
using EnderPearl.Protocol;
using Protocol.Connection;
using Protocol.Packets;
using Protocol.Types;
using RakNet;
using EnderPearl.Logging;

namespace EnderPearl.Listener
{
	/// <summary>
	/// The client-facing leg of one proxied player: the RakNet connection the Bedrock client speaks on.
	/// Java subclassed BedrockServerSession; here the transport lives in <see cref="PacketConnection"/>
	/// and this class adds the proxy-specific state and disconnect semantics.
	/// </summary>
	public sealed class ListenerSession : PacketConnection, IPacketHandler
	{
		private readonly Action<ListenerSession> closeListener;
		private volatile bool throttled = true;

		public ListenerSession(Conn conn, Action<ListenerSession> closeListener)
		{
			this.closeListener = closeListener ?? throw new ArgumentNullException(nameof(closeListener));
			Attach(conn);
		}

		public BedrockCodecInfo? ClientCodec { get; set; }

		public Session.ProxySessionProfile? SessionProfile { get; set; }

		public ProxyConnection? ProxyConnection { get; set; }

		/// <summary>
		/// Whether this session claimed a slot from the per-address connection throttle, and so must
		/// return one when it closes. False for bridge sessions, which never took one - releasing a
		/// slot that was not claimed would hand 127.0.0.1 a growing free allowance.
		/// </summary>
		public bool IsThrottled => throttled;

		public void SetThrottled(bool value)
		{
			throttled = value;
		}

		public PacketSignal Handle(IPacket packet) => PacketSignal.Unhandled;

		/// <summary>
		/// Kicks the client with a DisconnectPacket carrying <paramref name="reason"/>, then closes the
		/// transport. Safe to call from any thread and idempotent like Java's session.disconnect.
		/// </summary>
		public void Disconnect(string reason)
		{
			if (!IsConnected)
			{
				CloseTransport();
				return;
			}
			try
			{
				var packet = new DisconnectPacket();
				packet.Reason = DisconnectFailReason.Kicked;
				packet.Messages = OneOf.OneOf<DisconnectPacketMessages, object>.FromT0(new DisconnectPacketMessages
				{
					Message = reason,
					FilteredMessage = ""
				});
				SendPacket(packet);
			}
			catch (Exception exception)
			{
				Logger.Error($"Failed to send disconnect to {RemoteEndPoint}: {exception.Message}");
			}
			finally
			{
				CloseTransport();
			}
		}

		protected override void OnBatchDecodeFailure(Exception exception)
		{
			// A batch we could not parse is a protocol fault on the client leg: drop the connection
			// rather than relay garbage onward.
			CloseTransport();
		}

		protected override void OnTransportClosed()
		{
			base.OnTransportClosed();
			try
			{
				closeListener(this);
			}
			catch (Exception exception)
			{
				Logger.Error($"Listener close listener threw: {exception}");
			}
		}

		public override void Dispose()
		{
			CloseTransport();
		}
	}
}
