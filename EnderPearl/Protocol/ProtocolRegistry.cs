using System;
using System.Collections.Generic;
using global::Protocol;
using global::Protocol.Packets;
using EnderPearl.Config;

namespace EnderPearl.Protocol
{
	/// <summary>
	/// Maps a connecting client protocol to a backend protocol.
	///
	/// <p>The Java original stored adjacent-version translators as a directed graph and BFS-chained
	/// them (the ViaVersion model) across six codecs. This build speaks only Bedrock 1.26.40
	/// (protocol 2168), so the graph has one node and no edges: every accepted client and every
	/// backend uses the same codec with identity translation. The public API shape is kept - codecs,
	/// bindings, advertised codec, unsupported status - so the relay code reads like the original.</p>
	/// </summary>
	public sealed class ProtocolRegistry
	{
		private readonly LinkedHashMap<int, BedrockCodecInfo> codecs;

		private ProtocolRegistry(LinkedHashMap<int, BedrockCodecInfo> codecs)
		{
			this.codecs = codecs;
		}

		public static ProtocolRegistry CreateDefault()
		{
			return DefaultBuilder().Build();
		}

		/// <summary>
		/// Everything <see cref="CreateDefault"/> registers, still open for more. Only V1_26_40 is
		/// implemented in this build; the six-codec graph of the Java original is out of scope.
		/// </summary>
		public static Builder DefaultBuilder()
		{
			return new Builder().Codec(CanonicalProtocol.V1_26_40);
		}

		public bool TryFindClientCodec(int protocolVersion, out BedrockCodecInfo? codec)
		{
			return TryFindCodec(protocolVersion, out codec);
		}

		public bool TryFindBackendCodec(int protocolVersion, out BedrockCodecInfo? codec)
		{
			return TryFindCodec(protocolVersion, out codec);
		}

		private bool TryFindCodec(int protocolVersion, out BedrockCodecInfo? codec)
		{
			return codecs.TryGetValue(protocolVersion, out codec);
		}

		public bool TryFindBinding(int clientProtocolVersion, int backendProtocolVersion, out ProtocolBinding? binding)
		{
			if (!codecs.TryGetValue(clientProtocolVersion, out BedrockCodecInfo? clientCodec)
				|| !codecs.TryGetValue(backendProtocolVersion, out BedrockCodecInfo? backendCodec))
			{
				binding = null;
				return false;
			}
			binding = new ProtocolBinding(clientCodec, backendCodec, backendCodec, IdentityTranslator.INSTANCE);
			return true;
		}

		public BedrockCodecInfo AdvertisedClientCodec()
		{
			BedrockCodecInfo? newest = null;
			foreach (BedrockCodecInfo codec in codecs.Values)
			{
				if (newest == null || codec.ProtocolVersion > newest.ProtocolVersion)
				{
					newest = codec;
				}
			}
			return newest ?? CanonicalProtocol.V1_26_40;
		}

		public PlayStatus UnsupportedStatus(int protocolVersion)
		{
			int newestSupportedProtocol = AdvertisedClientCodec().ProtocolVersion;
			return protocolVersion > newestSupportedProtocol
				? PlayStatus.LoginFailedServerOld
				: PlayStatus.LoginFailedClientOld;
		}

		public sealed class Builder
		{
			private readonly LinkedHashMap<int, BedrockCodecInfo> codecs = new();

			internal Builder()
			{
			}

			public Builder Codec(BedrockCodecInfo codec)
			{
				if (!codecs.ContainsKey(codec.ProtocolVersion))
				{
					codecs.Add(codec.ProtocolVersion, codec);
				}
				return this;
			}

			public ProtocolRegistry Build()
			{
				return new ProtocolRegistry(codecs);
			}
		}
	}
}
