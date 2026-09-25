using Protocol.Packets;

namespace EnderPearl.Core
{
	/// <summary>
	/// A packet handler attached to a session. Java's BedrockPacketHandler used one method per packet
	/// type; here a single Handle with pattern matching plays that role.
	///
	/// <para>A handler that also wants to learn when its transport closed overrides
	/// <see cref="OnDisconnected"/> (Java: onDisconnect) - there is no separate notifier interface.</para>
	/// </summary>
	public abstract class PacketHandler
	{
		/// <summary>A handler that swallows everything (Java's discardInboundPackets).</summary>
		public static readonly PacketHandler Discarding = new DiscardingHandler();

		public abstract PacketSignal Handle(IPacket packet);

		/// <summary>The transport this handler was attached to closed. Default: nothing.</summary>
		public virtual void OnDisconnected(string reason)
		{
		}

		private sealed class DiscardingHandler : PacketHandler
		{
			public override PacketSignal Handle(IPacket packet) => PacketSignal.Handled;
		}
	}
}
