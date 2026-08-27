namespace EnderPearl.Codec
{
	/// <summary>
	/// Intentional no-op.
	///
	/// <para>The Java original installed Cloudburst "unknown definition" fallback registries onto each
	/// <c>BedrockSession</c>'s codec helper, so that blocks, items and camera presets a backend mentions
	/// but the proxy has never seen survive re-serialization toward the client instead of throwing.
	/// This build's protocol library has no per-session definition-registry concept to install into,
	/// so the install does nothing here.</para>
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
	}
}
