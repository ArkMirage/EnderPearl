using System;

namespace EnderPearl.Diagnostics
{
	/// <summary>
	/// One protocol-level fault: the proxy and a backend disagreed about the wire, and a player paid.
	///
	/// <p>Kept distinct from a backend simply going away, because the two want opposite handling. A
	/// backend that is down is a transient infrastructure problem and moving the player to a fallback is
	/// the kind thing to do. A protocol fault is a <em>bug</em> - the fallback will not fix it, the player
	/// usually bounces straight back, and the failover hides the evidence. Those get disconnected with a
	/// reason and written to <see cref="ProtocolFaultLog"/>.</p>
	/// </summary>
	public sealed record ProtocolFault(string BackendName, string PlayerName, string Detail)
	{
		/// <summary>A fault built from a decoded PacketViolationWarningPacket: the authoritative case.</summary>
		public static ProtocolFault FromViolation(string backendName, string playerName, PacketViolation violation)
		{
			return new ProtocolFault(backendName, playerName, violation.ToString());
		}

		/// <summary>One self-contained line: everything needed to act on this without the relay log.</summary>
		public string Describe()
		{
			return "backend=" + BackendName + " player=" + PlayerName + " " + Detail;
		}
	}
}
