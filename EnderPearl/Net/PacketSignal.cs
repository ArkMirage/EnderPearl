using Protocol.Packets;

namespace EnderPearl.Net
{
	/// <summary>Mirrors the Cloudburst PacketSignal: HANDLED stops, UNHANDLED falls through.</summary>
	public enum PacketSignal
	{
		Handled,
		Unhandled
	}

	/// <summary>
	/// A packet handler attached to a session. Java's BedrockPacketHandler used one method per packet
	/// type; here a single Handle with pattern matching plays that role.
	/// </summary>
	public interface IPacketHandler
	{
		PacketSignal Handle(IPacket packet);
	}
}
