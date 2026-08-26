using System;

namespace EnderPearl.Protocol
{
	/// <summary>The codec triple a translation runs between. Single-protocol build: all three are 1.26.40.</summary>
	public sealed class TranslationContext
	{
		public BedrockCodecInfo ClientCodec { get; }

		public BedrockCodecInfo CanonicalCodec { get; }

		public BedrockCodecInfo BackendCodec { get; }

		public TranslationContext(BedrockCodecInfo clientCodec, BedrockCodecInfo canonicalCodec, BedrockCodecInfo backendCodec)
		{
			ClientCodec = clientCodec ?? throw new ArgumentNullException(nameof(clientCodec));
			CanonicalCodec = canonicalCodec ?? throw new ArgumentNullException(nameof(canonicalCodec));
			BackendCodec = backendCodec ?? throw new ArgumentNullException(nameof(backendCodec));
		}
	}

	/// <summary>
	/// The immutable binding a session uses: which codec each side speaks and the translator between
	/// them. Single-protocol build: identity translation, same codec everywhere.
	/// </summary>
	public sealed class ProtocolBinding
	{
		public BedrockCodecInfo ClientCodec { get; }

		public BedrockCodecInfo CanonicalCodec { get; }

		public BedrockCodecInfo BackendCodec { get; }

		public PacketTranslator Translator { get; }

		public ProtocolBinding(
			BedrockCodecInfo clientCodec,
			BedrockCodecInfo canonicalCodec,
			BedrockCodecInfo backendCodec,
			PacketTranslator translator)
		{
			ClientCodec = clientCodec ?? throw new ArgumentNullException(nameof(clientCodec));
			CanonicalCodec = canonicalCodec ?? throw new ArgumentNullException(nameof(canonicalCodec));
			BackendCodec = backendCodec ?? throw new ArgumentNullException(nameof(backendCodec));
			Translator = translator ?? throw new ArgumentNullException(nameof(translator));
		}
	}
}
