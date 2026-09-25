using global::Protocol.Packets;
using EnderPearl.Core;

namespace EnderPearl.Core
{
	/// <summary>
	/// Builders and readers for the few packets the proxy itself originates - the system chat notice
	/// and the kick packet - plus the readers that pull disconnect text back out of a backend's
	/// DisconnectPacket. Kept in one place so "what the proxy sends on its own behalf" has one answer.
	/// </summary>
	internal static class ProxyPackets
	{
		/// <summary>
		/// How the proxy talks to a player. Protocol 2168 carries only Raw/Chat/Translate text bodies;
		/// server notices are Raw (the modern encoding of what older protocols called a system message).
		/// </summary>
		public static TextPacket NewSystemText(string message)
		{
			return new TextPacket
			{
				MessageType = global::Protocol.TextPacketType.Raw,
				Localize = false,
				Body = OneOf.OneOf<global::Protocol.Types.TextPacketPayload.MessageOnly, global::Protocol.Types.TextPacketPayload.AuthorAndMessage, global::Protocol.Types.TextPacketPayload.MessageAndParams>.FromT0(
					new global::Protocol.Types.TextPacketPayload.MessageOnly
					{
						MessageType = global::Protocol.TextPacketType.Raw,
						Message = message
					}),
				SenderSXUID = "",
				PlatformId = ""
			};
		}

		/// <summary>The disconnect text a DisconnectPacket carries, or "" when the message was skipped.</summary>
		public static string DisconnectMessage(DisconnectPacket packet)
		{
			return packet.Messages.Index == 0 && packet.Messages.AsT0 != null
				? packet.Messages.AsT0.Message ?? ""
				: "";
		}

		public static string DisconnectFilteredMessage(DisconnectPacket packet)
		{
			return packet.Messages.Index == 0 && packet.Messages.AsT0 != null
				? packet.Messages.AsT0.FilteredMessage ?? ""
				: "";
		}

		/// <summary>Whether the backend sent disconnect text of its own (false = host-level disconnect).</summary>
		public static bool DisconnectHasMessage(DisconnectPacket packet)
		{
			// Java tested !message.isBlank(): a whitespace-only message counts as "no message", which
			// decides whether a disconnect is treated as a deliberate ban (pass through) or a host failure
			// (failover candidate).
			return packet.Messages.Index == 0 && !string.IsNullOrWhiteSpace(packet.Messages.AsT0?.Message);
		}
	}
}
