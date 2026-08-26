namespace EnderPearl.Protocol
{
	/// <summary>
	/// A codec identity: which Bedrock wire protocol a side speaks.
	///
	/// <p>This build of EnderPearl speaks exactly one protocol - Bedrock 1.26.40, protocol 2168, matching
	/// the bundled protocol library (branch <c>v2168_1.26.40</c>, <c>ProtocolVersion.VERSION = 2168</c>).
	/// The Java original carried six codecs and chained translators between them; per project scope only
	/// 1.26.40 is implemented here, so every session's three codecs (client/canonical/backend) are this
	/// one and translation is always identity.</p>
	/// </summary>
	public sealed class BedrockCodecInfo
	{
		public int ProtocolVersion { get; }

		public string MinecraftVersion { get; }

		private BedrockCodecInfo(int protocolVersion, string minecraftVersion)
		{
			ProtocolVersion = protocolVersion;
			MinecraftVersion = minecraftVersion;
		}

		public static readonly BedrockCodecInfo V1_26_40 = new(2168, "1.26.40");

		public override string ToString() => MinecraftVersion + " (protocol " + ProtocolVersion + ")";
	}

	/// <summary>
	/// The protocols EnderPearl knows how to speak. This build registers only V1_26_40; the Java original
	/// was an enum over six codecs (V1_21_130..V1_26_40). Kept as a holder with the same member names
	/// so call sites read the same.
	/// </summary>
	public sealed class CanonicalProtocol
	{
		private CanonicalProtocol()
		{
		}

		public static BedrockCodecInfo V1_26_40 => BedrockCodecInfo.V1_26_40;

		/// <summary>
		/// The newest client version the proxy speaks. This is what the server list advertises, so
		/// anything user-facing that names a version should derive it from here rather than hardcode one.
		/// </summary>
		public static BedrockCodecInfo Newest() => BedrockCodecInfo.V1_26_40;

		/// <summary>
		/// Resolves a config value ("auto", "2168", "1.26.40" or "26.40") to a codec; null means
		/// "auto"/unset and inherits whatever detection or the client provides.
		/// </summary>
		public static BedrockCodecInfo? FromConfig(string? value)
		{
			if (string.IsNullOrWhiteSpace(value) || "auto".Equals(value.Trim(), StringComparison.OrdinalIgnoreCase))
			{
				return null;
			}
			string normalized = value.Trim();
			if (normalized.Equals(V1_26_40.ProtocolVersion.ToString(), StringComparison.Ordinal)
				|| normalized.Equals(V1_26_40.MinecraftVersion, StringComparison.OrdinalIgnoreCase)
				|| normalized.Equals(V1_26_40.MinecraftVersion[2..], StringComparison.OrdinalIgnoreCase))
			{
				return V1_26_40;
			}
			throw new ArgumentException("Unsupported backend protocol: " + value);
		}
	}
}
