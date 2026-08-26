using System;
using System.Text;
using Protocol.Utility.IO;

namespace EnderPearl.Diagnostics
{
	/// <summary>
	/// A <c>PacketViolationWarningPacket</c> the backend sent us, decoded by hand.
	///
	/// <p>This has to be done by hand because the packet registry marks packet 156 server-bound in the
	/// Java original's codec, so a copy arriving on the client leg could arrive as an unknown packet -
	/// the proxy never got a typed one. Which is unfortunate, because it is the single most informative
	/// packet BDS sends: it names the packet the proxy got wrong, and whether the connection is about to
	/// be torn down over it.</p>
	///
	/// <p>Four fields, three of them zigzag varints:</p>
	/// <pre>
	///   varint  Type            0 = malformed packet
	///   varint  Severity        0 = warning, 1 = final warning, 2 = terminating connection
	///   varint  CausePacketId   the packet id BDS could not read
	///   string  Message         BDS's own reader error
	/// </pre>
	/// </summary>
	public sealed record PacketViolation(int Type, int Severity, int CausePacketId, string Message)
	{
		public const int PACKET_ID = 156;

		public const int SEVERITY_WARNING = 0;
		public const int SEVERITY_FINAL_WARNING = 1;
		public const int SEVERITY_TERMINATING = 2;

		/// <summary>Only a terminating violation is fatal; the softer two are BDS complaining but carrying on.</summary>
		public bool IsTerminating() => Severity >= SEVERITY_TERMINATING;

		public string SeverityName()
		{
			return Severity switch
			{
				SEVERITY_WARNING => "warning",
				SEVERITY_FINAL_WARNING => "final warning",
				SEVERITY_TERMINATING => "terminating connection",
				_ => "severity " + Severity
			};
		}

		public override string ToString()
		{
			return "packet " + CausePacketId + " rejected (" + SeverityName() + "): " + Message;
		}

		/// <summary>
		/// Decodes the raw packet body without consuming it, or null when the bytes are not a violation
		/// this understands. Never throws: a malformed diagnostic must not become a second fault.
		/// </summary>
		public static PacketViolation? Decode(ReadOnlyMemory<byte> payload)
		{
			try
			{
				using var reader = new MemoryStreamReader(payload);
				int type = (int)VarInt.ReadSInt32(reader);
				int severity = (int)VarInt.ReadSInt32(reader);
				int causePacketId = (int)VarInt.ReadSInt32(reader);
				string message = reader.ReadLengthPrefixedString();
				return new PacketViolation(type, severity, causePacketId, message);
			}
			catch (Exception)
			{
				return null;
			}
		}
	}
}
