using System;
using System.Collections.Generic;
using EnderPearl.Resource;
using global::Protocol.Packets;
using ResourcePackClientResponsePayload = global::Protocol.Types.ResourcePackClientResponsePacketPayload;
using EnderPearl.Logging;

namespace EnderPearl.Backend
{
	/// <summary>
	/// Downloads a backend's resource packs on the proxy's own behalf, during a mid-session switch.
	///
	/// <para>The proxy only ever sees pack bytes when a client downloads them, and a client downloads
	/// them only during its login handshake. A backend a player switches to is therefore never asked
	/// for its packs by anyone: the proxy answers that handshake itself, and the packs stay unknown no
	/// matter how many players go there. This plays the client's part of the handshake — request the
	/// packs, pull the chunks, hand them to the cache — so the backend is learned once and every later
	/// login includes its packs.</para>
	///
	/// <para>It costs the first switching player a wait, which is why it is bounded twice over: a pack
	/// larger than <see cref="BackendPackCache.MAX_PACK_BYTES"/> is skipped outright, and if the
	/// backend stops answering the whole fetch is abandoned. Either way the switch continues — a player
	/// waiting forever for a texture is a worse failure than a missing texture.</para>
	/// </summary>
	internal sealed class BackendPackFetch
	{
		private readonly BackendPackCache cache;
		private readonly string backendName;
		private readonly Action<IPacket> toBackend;
		private readonly Action onFinished;

		private readonly Dictionary<Guid, Wanted> wanted = new();
		private readonly Dictionary<Guid, Download> downloads = new();
		private bool finished;

		private BackendPackFetch(
			BackendPackCache cache,
			string backendName,
			Action<IPacket> toBackend,
			Action onFinished)
		{
			this.cache = cache;
			this.backendName = backendName;
			this.toBackend = toBackend;
			this.onFinished = onFinished;
		}

		/// <summary>
		/// Starts a fetch for everything in <paramref name="packsInfo"/> the proxy cannot already serve,
		/// or returns null when there is nothing to fetch and the caller should complete the handshake
		/// as before.
		/// </summary>
		public static BackendPackFetch? Start(
			BackendPackCache cache,
			string backendName,
			ResourcePacksInfoPacket packsInfo,
			Action<IPacket> toBackend,
			Action onFinished)
		{
			if (!cache.IsEnabled())
			{
				return null;
			}
			BackendPackFetch fetch = new BackendPackFetch(cache, backendName, toBackend, onFinished);
			List<string> requestIds = new List<string>();
			foreach (global::Protocol.Types.PackInfoData entry in packsInfo.ResourcePacks)
			{
				global::Protocol.Types.PackIdVersion? idVersion = entry.PackIdVersion;
				if (idVersion == null)
				{
					continue;
				}
				Guid packId = ProxyResourcePackRegistry.ToGuid(idVersion.PackUUID);
				if (packId == Guid.Empty)
				{
					continue;
				}
				string versionString = idVersion.PackVersion?.Version ?? "";
				int[] version = ProxyResourcePackRegistry.ParseVersion(versionString);
				if (cache.Has(packId, version))
				{
					continue;
				}
				if (!string.IsNullOrEmpty(entry.ContentKey))
				{
					// An encrypted pack is useless to anyone but the backend that holds the key; storing
					// it would only mean serving bytes no client can open.
					Logger.Info(
						$"Not caching encrypted resource pack {packId} from backend {backendName}; copy it into "
						+ "resourcePacks.dir if players need it after a switch.");
					continue;
				}
				if (entry.PackSize > (ulong)BackendPackCache.MAX_PACK_BYTES)
				{
					Logger.Info(
						$"Not caching resource pack {packId} from backend {backendName}: {entry.PackSize} bytes is over the "
						+ $"{BackendPackCache.MAX_PACK_BYTES} byte limit.");
					continue;
				}
				fetch.wanted[packId] = new Wanted(packId, versionString);
				requestIds.Add(packId + "_" + versionString);
			}
			if (requestIds.Count == 0)
			{
				return null;
			}
			Logger.Info(
				$"Downloading {requestIds.Count} resource pack(s) from backend {backendName} so later logins can serve them; "
				+ "the player switching now waits for it once.");
			// ResourcePackClientResponsePacket.Status.SEND_PACKS -> wire discriminant 1 (Downloading).
			ResourcePackClientResponsePayload.Downloading downloading = new ResourcePackClientResponsePayload.Downloading
			{
				ResponseType = "",
			};
			downloading.DownloadingPacks.AddRange(requestIds);
			ResourcePackClientResponsePacket request = new ResourcePackClientResponsePacket();
			request.Response = OneOf.OneOf<
				ResourcePackClientResponsePayload.Cancel,
				ResourcePackClientResponsePayload.Downloading,
				ResourcePackClientResponsePayload.DownloadingFinished,
				ResourcePackClientResponsePayload.ResourcePackStackFinished>.FromT1(downloading);
			toBackend(request);
			return fetch;
		}

		public bool IsFinished()
		{
			return finished;
		}

		/// <returns>true when the packet belonged to this fetch and must not reach the client</returns>
		public bool Handle(IPacket packet)
		{
			if (finished)
			{
				return false;
			}
			if (packet is ResourcePackDataInfoPacket dataInfo)
			{
				return BeginDownload(dataInfo);
			}
			if (packet is ResourcePackChunkDataPacket chunkData)
			{
				return AcceptChunk(chunkData);
			}
			return false;
		}

		private bool BeginDownload(ResourcePackDataInfoPacket dataInfo)
		{
			// The wire id is "uuid" or "uuid_version"; match on the uuid part.
			string resourceName = dataInfo.ResourceName ?? "";
			int underscore = resourceName.IndexOf('_');
			string uuidPart = underscore >= 0 ? resourceName.Substring(0, underscore) : resourceName;
			if (!Guid.TryParseExact(uuidPart, "D", out Guid packId) || !wanted.TryGetValue(packId, out _))
			{
				return false;
			}
			long size = (long)dataInfo.FileSize;
			if (size <= 0 || size > BackendPackCache.MAX_PACK_BYTES)
			{
				Logger.Info(
					$"Giving up on resource pack {packId} from backend {backendName}: it reports {size} bytes.");
				wanted.Remove(packId);
				FinishIfDone();
				return true;
			}
			downloads[packId] = new Download(
				new byte[size],
				dataInfo.FileHash,
				Math.Max(1u, dataInfo.ChunkSize));
			RequestChunk(packId, 0);
			return true;
		}

		private bool AcceptChunk(ResourcePackChunkDataPacket chunkData)
		{
			// Same "uuid" or "uuid_version" wire id as the DataInfo packet.
			string resourceName = chunkData.ResourceName ?? "";
			int underscore = resourceName.IndexOf('_');
			string uuidPart = underscore >= 0 ? resourceName.Substring(0, underscore) : resourceName;
			if (!Guid.TryParseExact(uuidPart, "D", out Guid packId)
				|| !downloads.TryGetValue(packId, out Download? download))
			{
				return false;
			}
			byte[]? data = chunkData.ChunkData;
			long offsetLong = Math.Min(download.Buffer.Length, (long)chunkData.ChunkID * download.ChunkSize);
			int offset = (int)Math.Max(0, Math.Min(offsetLong, int.MaxValue));
			int length = data == null ? 0 : Math.Min(data.Length, download.Buffer.Length - offset);
			if (length > 0 && offset >= 0 && offset < download.Buffer.Length)
			{
				// Copy without consuming: this packet is the backend's, and draining it here would
				// corrupt anything downstream that still expects it intact.
				Buffer.BlockCopy(data, 0, download.Buffer, offset, length);
				download.Filled += length;
			}
			if (download.Filled >= download.Buffer.Length)
			{
				downloads.Remove(packId);
				wanted.Remove(packId);
				cache.Store(packId, download.Buffer, download.Hash);
				FinishIfDone();
			}
			else
			{
				RequestChunk(packId, (int)chunkData.ChunkID + 1);
			}
			return true;
		}

		private void RequestChunk(Guid packId, int chunkIndex)
		{
			if (!wanted.TryGetValue(packId, out Wanted? pack))
			{
				return;
			}
			// Java's request carried "packId_version" as one wire string; this codec has a single
			// name field, so build the same joined form.
			ResourcePackChunkRequestPacket request = new ResourcePackChunkRequestPacket();
			request.ResourceName = packId + "_" + pack.Version;
			request.Chunk = chunkIndex;
			toBackend(request);
		}

		private void FinishIfDone()
		{
			if (wanted.Count == 0)
			{
				Finish();
			}
		}

		/// <summary>Ends the fetch, kept or not, and lets the switch handshake continue.</summary>
		public void Finish()
		{
			if (finished)
			{
				return;
			}
			finished = true;
			downloads.Clear();
			wanted.Clear();
			onFinished();
		}

		public void Abandon(string reason)
		{
			if (finished)
			{
				return;
			}
			Logger.Info(
				$"Stopped downloading resource packs from backend {backendName} ({reason}); the switch continues and the packs "
				+ "stay unserved until next time.");
			Finish();
		}

		private sealed record Wanted(Guid PackId, string Version);

		private sealed class Download
		{
			public readonly byte[] Buffer;
			public readonly byte[] Hash;
			public readonly long ChunkSize;
			public int Filled;

			public Download(byte[] buffer, byte[] hash, long chunkSize)
			{
				Buffer = buffer;
				Hash = hash;
				ChunkSize = chunkSize;
			}
		}
	}
}
