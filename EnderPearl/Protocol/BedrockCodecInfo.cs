using System;
using System.Reflection;

namespace EnderPearl.Protocol
{
	/// <summary>
	/// A codec identity: which Bedrock wire protocol a side speaks.
	///
	/// <p>Both halves are read from the protocol library at startup rather than written here, so moving
	/// to another build of it - a package whose protocol number differs but whose packet definitions do
	/// not - takes no edit to EnderPearl. The number comes from <c>Protocol.ProtocolVersion.VERSION</c>;
	/// the release name comes from the library assembly's informational version, which the release
	/// workflow stamps as <c>1.26.40-v2168</c> - everything before the tag is what a player recognises
	/// as the version.</p>
	///
	/// <p>This build speaks exactly one protocol, so there is one instance. The Java original carried six
	/// codecs and chained translators between them; here a client and a backend either both speak this
	/// one or the pair is refused, so there is nothing to translate between.</p>
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

		/// <summary>
		/// The one protocol this build speaks: whatever the referenced library implements. This is what
		/// the server list advertises, so anything user-facing that names a version derives it from here
		/// rather than hardcoding one.
		/// </summary>
		public static readonly BedrockCodecInfo Current = new((int)global::Protocol.ProtocolVersion.VERSION, LibraryReleaseName());

		public override string ToString() => MinecraftVersion + " (protocol " + ProtocolVersion + ")";

		/// <summary>
		/// Resolves a config value ("auto", "2168", "1.26.40" or "26.40") to the current codec; null
		/// means "auto"/unset and inherits whatever detection or the client provides.
		/// </summary>
		public static BedrockCodecInfo? FromConfig(string? value)
		{
			if (string.IsNullOrWhiteSpace(value) || "auto".Equals(value.Trim(), StringComparison.OrdinalIgnoreCase))
			{
				return null;
			}
			string normalized = value.Trim();
			if (normalized.Equals(Current.ProtocolVersion.ToString(), StringComparison.Ordinal)
				|| normalized.Equals(Current.MinecraftVersion, StringComparison.OrdinalIgnoreCase)
				|| normalized.Equals(Current.MinecraftVersion[2..], StringComparison.OrdinalIgnoreCase))
			{
				return Current;
			}
			throw new ArgumentException("Unsupported backend protocol: " + value);
		}

		private static string LibraryReleaseName()
		{
			string? informational = typeof(global::Protocol.ProtocolVersion).Assembly
				.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
			int tag = informational?.IndexOf('-') ?? -1;
			return tag > 0 ? informational![..tag] : informational ?? "";
		}
	}
}