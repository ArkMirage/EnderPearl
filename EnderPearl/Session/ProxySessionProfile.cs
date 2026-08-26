using EnderPearl.Protocol;

namespace EnderPearl.Session
{
	/// <summary>
	/// The immutable codec triple + translator snapshot derived from a <see cref="ProtocolBinding"/>,
	/// exposing the translation context used for the session's relay.
	/// </summary>
	public sealed record ProxySessionProfile(
		BedrockCodecInfo ClientCodec,
		BedrockCodecInfo CanonicalCodec,
		BedrockCodecInfo BackendCodec,
		PacketTranslator Translator)
	{
		private TranslationContext? cachedTranslationContext;

		public static ProxySessionProfile From(ProtocolBinding binding)
		{
			return new ProxySessionProfile(
				binding.ClientCodec,
				binding.CanonicalCodec,
				binding.BackendCodec,
				binding.Translator
			);
		}

		/// <summary>
		/// The relay builds this per translated packet; profiles are immutable so one shared instance
		/// is enough. A benign race may build it twice and discard one copy.
		/// </summary>
		public TranslationContext TranslationContext()
		{
			return cachedTranslationContext ??= new TranslationContext(ClientCodec, CanonicalCodec, BackendCodec);
		}
	}
}
