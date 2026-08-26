using System;
using ContentIdentity = global::Protocol.Types.ContentIdentity;
using MceUuid = global::Protocol.Types.mce.UUID;
using PackIdVersion = global::Protocol.Types.PackIdVersion;
using PackInfoData = global::Protocol.Types.PackInfoData;
using SemVersion = global::Protocol.Types.SemVersion;

namespace EnderPearl.Resource
{
	/// <summary>
	/// One <c>.mcpack</c> served by the proxy itself: its manifest identity plus the raw archive
	/// bytes and their SHA-256 hash, ready to be advertised and chunked out to clients.
	/// </summary>
	public sealed record ProxyResourcePackEntry(Guid Uuid, int[] Version, string Name, byte[] Data, byte[] Hash)
	{
		/// <summary>
		/// Bytes per ResourcePackChunkDataPacket. 100KB rather than a megabyte on purpose.
		///
		/// <para>The client asks for one chunk at a time, so this is also the burst size: a 1MB chunk leaves
		/// RakNet with ~750 datagrams to send at once, and the client's acknowledgements for them arrive
		/// back in a handful of ticks. That inbound burst is counted by the per-address packet limiter
		/// (security.rateLimit.packetLimit), which blocks the address mid-login; the handshake then stalls
		/// and the player times out before they ever join. Smaller chunks spread the same bytes over more
		/// request/response round trips, so the return traffic stays inside the budget that protects the
		/// public listener.</para>
		/// </summary>
		internal const int CHUNK_SIZE = 100 * 1024;

		public string VersionString()
		{
			return Version[0] + "." + Version[1] + "." + Version[2];
		}

		public PackInfoData ToInfoEntry()
		{
			// Protocol library shape mapping (differs from Cloudburst's Entry constructor):
			// uuid/version -> PackIdVersion, size -> PackSize, contentKey "", subPackName = name,
			// contentId -> ContentIdentity.Identity = uuid string, scripting/raytracing/addon off,
			// cdnUrl "".
			return new PackInfoData
			{
				PackIdVersion = new PackIdVersion
				{
					PackUUID = ToMceUuid(Uuid),
					PackVersion = new SemVersion { Version = VersionString() }
				},
				PackSize = (ulong)Data.Length,
				ContentKey = "",
				SubpackName = Name,
				ContentIdentity = new ContentIdentity { Identity = Uuid.ToString() },
				HasScripts = false,
				IsAddonPack = false,
				IsRayTracingCapable = false,
				CDNURL = ""
			};
		}

		/// <summary>Packs a <see cref="Guid"/> into the wire's big-endian 64-bit halves.</summary>
		private static MceUuid ToMceUuid(Guid guid)
		{
			byte[] bytes = guid.ToByteArray();
			ulong mostSignificantBits = 0;
			ulong leastSignificantBits = 0;
			for (int i = 0; i < 8; i++)
			{
				mostSignificantBits = (mostSignificantBits << 8) | bytes[i];
			}
			for (int i = 8; i < 16; i++)
			{
				leastSignificantBits = (leastSignificantBits << 8) | bytes[i];
			}
			return new MceUuid { MostSignificantBits = mostSignificantBits, LeastSignificantBits = leastSignificantBits };
		}
	}
}
