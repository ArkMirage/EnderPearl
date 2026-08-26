using System;
using System.Globalization;
using System.IO;

namespace EnderPearl.Security
{
	/// <summary>
	/// Caps how large a decompressed batch may be while a session is still unauthenticated.
	///
	/// <para><c>bedrock.maxDecompressedBytes</c> (10 MB) bounds the bytes a batch decompresses to, but not
	/// the work those bytes buy. Everything downstream is driven by the batch's <em>content</em>, and
	/// two of those consumers amplify it by one to two orders of magnitude:</para>
	///
	/// <list type="bullet">
	///   <item>the batch decoder turns each byte into a retained slice (~53 bytes of heap each), and</item>
	///   <item>login JWT parsing hands the raw, <em>unverified</em> login JWT straight to a JSON parser -
	///       measured at ~79 bytes of heap per source byte for a nested payload, before a single signature
	///       has been checked.</item>
	/// </list>
	///
	/// <para>Zeros deflate about 1000:1, so ~10 KB on the wire reaches the 10 MB cap, and at those
	/// amplification factors that is several hundred megabytes of heap on an I/O thread from an
	/// unauthenticated client. The batch decoder now caps its packet count, which closes the first; this
	/// closes the second, and any future consumer with the same shape, by bounding the input itself for
	/// exactly as long as the peer is anonymous.</para>
	///
	/// <para>The limit only applies before login completes. A real login batch is tens to a few hundred
	/// kilobytes, so the default sits well above any legitimate one; gameplay batches, which are the
	/// genuinely large ones, are never measured against it.</para>
	///
	/// <para>In the Java original this was a Netty <c>ChannelInboundHandlerAdapter</c> parked in the pipeline.
	/// This build has no pipeline: <see cref="EnderPearl.Net.PacketSession"/> calls <see cref="ThrowIfTooLarge"/>
	/// on every decompressed batch (or consults <see cref="MaxPreAuthBatchBytes"/> through its
	/// <c>MaxInboundBatchBytesProvider</c> hook), which is why this class is a static policy helper instead.</para>
	/// </summary>
	public static class PreAuthBatchLimiter
	{
		public const string NAME = "endstone-preauth-batch-limiter";

		/// <summary>Default 1 MiB - roughly ten times the largest login seen in practice.</summary>
		public const int DEFAULT_MAX_PRE_AUTH_BATCH_BYTES = 1024 * 1024;

		/// <summary>
		/// Maximum decompressed batch size accepted from a not-yet-authenticated peer, in bytes;
		/// 0 disables the check. Override with the <c>BEDROCK_MAXPREAUTHBATCHBYTES</c> environment
		/// variable or AppContext data "bedrock.maxPreAuthBatchBytes" (the -D equivalent).
		/// </summary>
		public static int MaxPreAuthBatchBytes = ResolveMaxPreAuthBatchBytes();

		private static int ResolveMaxPreAuthBatchBytes()
		{
			object? configured = AppContext.GetData("bedrock.maxPreAuthBatchBytes");
			if (configured is int intValue)
			{
				return intValue;
			}
			if (configured is string textValue && int.TryParse(textValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedText))
			{
				return parsedText;
			}
			string? environment = Environment.GetEnvironmentVariable("BEDROCK_MAXPREAUTHBATCHBYTES");
			if (int.TryParse(environment, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedEnvironment))
			{
				return parsedEnvironment;
			}
			return DEFAULT_MAX_PRE_AUTH_BATCH_BYTES;
		}

		/// <summary>
		/// Enforces the limit for one inbound batch. Throws <see cref="IOException"/> when
		/// <paramref name="decompressedSize"/> exceeds the cap while <paramref name="authenticated"/> is false;
		/// authenticated sessions and a limit of 0 (disabled) pass everything.
		/// </summary>
		public static void ThrowIfTooLarge(long decompressedSize, bool authenticated)
		{
			int maxBytes = MaxPreAuthBatchBytes;
			if (maxBytes <= 0 || authenticated)
			{
				return;
			}
			if (decompressedSize <= maxBytes)
			{
				return;
			}
			// The Java handler owned the released batch here because nothing downstream would see it;
			// this build builds plain managed objects, so there is nothing to release - throwing is enough.
			throw new IOException("Pre-login batch of " + decompressedSize + " bytes exceeds the maximum of " + maxBytes + " bytes");
		}
	}
}
