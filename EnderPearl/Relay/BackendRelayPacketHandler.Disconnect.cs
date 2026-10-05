using EnderPearl.Backend;
using EnderPearl.Core;
using global::Protocol.Packets;

namespace EnderPearl.Relay;
/// <summary>
/// Backend disconnect interception: turns a backend's DisconnectPacket into a Failover when policy
/// allows.
///
/// <para>"Disconnect" rather than "kick", because the direction is what a reader has to get right:
/// this is the backend ending the session, not the proxy. The proxy disconnecting a client is
/// <see cref="Frontend.ListenerSession.Disconnect"/>, reached from the terminal's /kick.</para>
/// </summary>
public sealed partial class BackendRelayPacketHandler
{
	/// <summary>
	/// Set when a backend's disconnect was relayed to the client instead of being turned into a
	/// Failover.
	///
	/// <para>The socket closes a moment later and <c>onDisconnect</c> would otherwise read that as the
	/// backend dying and start the Failover the disconnect was just spared - which is what put a banned
	/// player on the fallback and then straight back onto the backend that banned them.</para>
	/// </summary>
	private bool disconnectPassedThrough;

	/// <summary>
	/// Set once this backend's disconnect has been claimed by Failover. Its socket stays open for a short
	/// while afterwards, and anything else it sends in that window belongs to a world the player is
	/// already leaving.
	/// </summary>
	private bool disconnectIntercepted;

	/// <summary>
	/// Turns a disconnect from the backend the player is on into a Failover, the way Velocity turns one
	/// into a redirect.
	///
	/// <para>A graceful backend shutdown sends this well before the socket closes, so without the
	/// interception the client is gone long before OnDisconnected - and the transport timeout that would
	/// eventually fire is ten seconds too late to matter. Forwarding resumes unchanged when Failover
	/// declines the disconnect, so a player with no fallback still sees the backend's own message.</para>
	/// </summary>
	private bool InterceptBackendDisconnect(IPacket packet)
	{
		if (disconnectIntercepted)
		{
			return true;
		}
		if (Failover == null)
		{
			return false;
		}
		string reason;
		// Whether the backend wrote something for this player, which is what separates a ban from a
		// host going away. ProxyPackets.Index==1 is the wire flag itself ("message skipped"), so
		// DisconnectHasMessage() is the backend's own statement rather than a guess at its text.
		// This codec's framing layer drops undecodable packet ids before they reach a handler, so
		// the Java UnknownPacket branch (raw id == DISCONNECT_PACKET_ID, counted as "no message"
		// because a disconnect that did not decode cannot be read) has no counterpart here.
		bool backendSuppliedMessage;
		if (packet is DisconnectPacket disconnect)
		{
			reason = DisconnectReason(disconnect);
			backendSuppliedMessage = ProxyPackets.DisconnectHasMessage(disconnect);
		}
		else
		{
			return false;
		}
		if (!Failover.FailsOverOnBackendDisconnect(backendSuppliedMessage))
		{
			// The backend decided something about this player - banned, whitelisted out, removed by a
			// moderator. Rescuing them to a fallback overrides that decision, and because the
			// fallback transfers them straight back it also loops: disconnect, Failover, transfer,
			// disconnect again. Forwarding the packet unchanged lets the player read the backend's own
			// message, which is the one worth showing; the flag stops OnDisconnected starting a
			// Failover behind it.
			disconnectPassedThrough = true;
			Logger.Info(
				$"Backend {BackendName} disconnected {Connection.Client.RemoteEndPoint} ({reason}); passing the disconnect through to the client."
			);
			return false;
		}
		Logger.Info(
			// A decoded disconnect is the only way a packet reaches this method in this build, so the
			// Java "its disconnect did not decode" alternative never applies.
			$"Backend {BackendName} disconnected {Connection.Client.RemoteEndPoint} ({reason}); intercepted."
		);
		if (!Failover.Begin(Connection, BackendName, reason))
		{
			return false;
		}
		disconnectIntercepted = true;
		// The socket closes moments after this packet; OnDisconnected must not undo the Failover.
		Backend.SetDisconnectClientOnClose(false);
		// Close it now rather than waiting: until it does, IsConnected is still true and the
		// client's input keeps being forwarded into a world it is already being moved out of.
		if (Backend.IsConnected)
		{
			Backend.Disconnect("Failing the player over after a backend disconnect");
		}
		return true;
	}

	/// <summary>
	/// Violations arrive as typed PacketViolationWarningPackets and are logged where they are
	/// handled; see HandleClientbound. The Java hand-decoder path that attributed a fatal
	/// violation to the disconnect cause has no counterpart here - this codec's framing layer
	/// drops undecodable packet ids before they reach a handler.
	/// </summary>
	private static string DisconnectReason(DisconnectPacket disconnect)
	{
		string message = ProxyPackets.DisconnectMessage(disconnect);
		if (!string.IsNullOrWhiteSpace(message))
		{
			return message;
		}
		return disconnect.Reason.ToString();
	}
}
