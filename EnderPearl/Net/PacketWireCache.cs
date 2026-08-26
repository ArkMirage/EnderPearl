using global::Protocol.Packets;

namespace EnderPearl.Net
{
	/// <summary>
	/// The protocol library caches each packet's encoded bytes: <c>Decode</c> stores the original
	/// wire slice and <c>Encode</code> returns it verbatim whenever it is non-empty, so the send
	/// path transmits exactly what arrived. Any field the proxy edits on a <em>decoded</em> packet
	/// therefore has no effect unless the cache is cleared first - this extension is that clear.
	///
	/// <para>Call <see cref="InvalidateWireCache"/> after mutating a decoded packet. Newly
	/// constructed packets (empty cache) encode fresh regardless and need no call.</para>
	/// </summary>
	public static class PacketWireCache
	{
		/// <summary>Discards the cached wire bytes so the next Encode serializes current field values.</summary>
		public static void InvalidateWireCache(this IPacket packet)
		{
			packet.bytes = ReadOnlyMemory<byte>.Empty;
		}
	}
}
