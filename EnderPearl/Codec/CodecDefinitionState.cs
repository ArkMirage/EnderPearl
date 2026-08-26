using EnderPearl.Palette;

namespace EnderPearl.Codec
{
	/// <summary>
	/// Intentional no-op.
	///
	/// <para>The Java original installed Cloudburst "unknown definition" fallback registries onto each
	/// <c>BedrockSession</c>'s codec helper, so that blocks, items and camera presets a backend mentions
	/// but the proxy has never seen survive re-serialization toward the client instead of throwing, and
	/// so <see cref="InstallItemMapping"/> can renumber item network ids between one backend's registry
	/// and the union registry the client was given at login. This build's protocol library has no
	/// per-session definition-registry concept to install into — item ids pass through raw — so both
	/// installs do nothing here.</para>
	///
	/// <para>Kept as a deliberate shim so relay call sites read the same as the Java original and a future
	/// codec swap can slot real behaviour back in.</para>
	/// </summary>
	public static class CodecDefinitionState
	{
		/// <summary>Cloudburst-specific memory-limit/definition-fallback shim; intentionally empty here.</summary>
		public static void InstallFallbacks(object session)
		{
			// No-op: this codec library carries no DefinitionRegistry state on its sessions.
			_ = session;
		}

		/// <summary>
		/// Installs a per-backend item id translation instead of one shared registry. Each side gets the
		/// registry that makes its decode produce definitions numbered for the other side (see
		/// ItemPaletteMapping). No-op here: there is no definition layer to install into, so item ids
		/// pass through unchanged and only match when backends number identically.
		/// </summary>
		public static void InstallItemMapping(object backend, object client, ItemPaletteMapping mapping)
		{
			_ = backend;
			_ = client;
			_ = mapping;
		}
	}
}
