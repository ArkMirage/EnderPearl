using Protocol.Packets;

namespace EnderPearl.Protocol
{
	/// <summary>
	/// Translates a packet between two protocols.
	///
	/// <p>This build speaks a single protocol (1.26.40), so the only translator is the identity one;
	/// the interface is kept so the relay code reads exactly like the Java original and a future
	/// multi-protocol build can slot real translators in without touching call sites.</p>
	///
	/// <p>Translate methods return the packet to forward, or null when the source packet has no safe
	/// representation in the target protocol and should be dropped.</p>
	/// </summary>
	public interface PacketTranslator
	{
		IPacket? TranslateServerbound(IPacket packet, TranslationContext context);

		IPacket? TranslateClientbound(IPacket packet, TranslationContext context);
	}

	/// <summary>The identity translator: every packet passes through unchanged.</summary>
	public sealed class IdentityTranslator : PacketTranslator
	{
		public static readonly IdentityTranslator INSTANCE = new();

		private IdentityTranslator()
		{
		}

		public IPacket? TranslateServerbound(IPacket packet, TranslationContext context) => packet;

		public IPacket? TranslateClientbound(IPacket packet, TranslationContext context) => packet;
	}
}
