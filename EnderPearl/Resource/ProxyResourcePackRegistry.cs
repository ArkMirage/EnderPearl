using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using EnderPearl.Listener;
using ContentIdentity = global::Protocol.Types.ContentIdentity;
using Experiments = global::Protocol.Types.Experiments;
using MceUuid = global::Protocol.Types.mce.UUID;
using PackIdVersion = global::Protocol.Types.PackIdVersion;
using PackInfoData = global::Protocol.Types.PackInfoData;
using PackInstanceId = global::Protocol.Types.PackInstanceId;
using SemVersion = global::Protocol.Types.SemVersion;
using PackType = global::Protocol.PackType;
using ResourcePackChunkDataPacket = global::Protocol.Packets.ResourcePackChunkDataPacket;
using ResourcePackDataInfoPacket = global::Protocol.Packets.ResourcePackDataInfoPacket;
using ResourcePackStackPacket = global::Protocol.Packets.ResourcePackStackPacket;
using ResourcePacksInfoPacket = global::Protocol.Packets.ResourcePacksInfoPacket;
using EnderPearl.Logging;

namespace EnderPearl.Resource
{
	/// <summary>
	/// The set of <c>.mcpack</c> files the proxy serves itself, plus builders that merge them into
	/// the backend's resource-pack handshake packets. Proxy packs win UUID conflicts when their
	/// semantic version is equal or newer than the backend pack's.
	/// </summary>
	public sealed class ProxyResourcePackRegistry
	{
		private static readonly ProxyResourcePackRegistry EMPTY = new ProxyResourcePackRegistry(new List<ProxyResourcePackEntry>());

		/// <summary>
		/// Read on every join and written when a backend's pack is learned, so both are snapshots
		/// swapped under a lock rather than collections mutated in place: a join in flight keeps the
		/// set it started with instead of seeing half an update.
		/// </summary>
		private volatile IReadOnlyList<ProxyResourcePackEntry> packs;
		private volatile IReadOnlyDictionary<Guid, ProxyResourcePackEntry> packsByUuid;

		private readonly object installMutex = new object();

		public ProxyResourcePackRegistry()
		{
			packs = Array.Empty<ProxyResourcePackEntry>();
			packsByUuid = new Dictionary<Guid, ProxyResourcePackEntry>();
		}

		private ProxyResourcePackRegistry(List<ProxyResourcePackEntry> packs)
		{
			Install(packs);
		}

		/// <summary>Swaps in a snapshot; mirrors the Java constructor body now that instances are mutable.</summary>
		private void Install(List<ProxyResourcePackEntry> packs)
		{
			List<ProxyResourcePackEntry> snapshot = new List<ProxyResourcePackEntry>(packs);
			Dictionary<Guid, ProxyResourcePackEntry> map = new Dictionary<Guid, ProxyResourcePackEntry>();
			foreach (ProxyResourcePackEntry pack in snapshot)
			{
				map[pack.Uuid] = pack;
			}
			this.packs = snapshot.AsReadOnly();
			this.packsByUuid = map;
		}

		/// <summary>A registry that starts empty but can still learn packs; see <see cref="Add"/>.</summary>
		public static ProxyResourcePackRegistry MutableEmpty()
		{
			return new ProxyResourcePackRegistry();
		}

		/// <summary>
		/// Adds a pack learned at runtime, keeping the newer of the two when the uuid is already known.
		///
		/// <para>Refused on the shared <see cref="EMPTY"/> instance, which every packless proxy holds:
		/// adding to it would hand one connection's packs to every other.</para>
		/// </summary>
		/// <returns>true when the registry changed</returns>
		public bool Add(ProxyResourcePackEntry? entry)
		{
			if (ReferenceEquals(this, EMPTY) || entry == null)
			{
				return false;
			}
			lock (installMutex)
			{
				ProxyResourcePackEntry? existing = packsByUuid.TryGetValue(entry.Uuid, out var found) ? found : null;
				if (existing != null && CompareVersions(existing.Version, entry.Version) >= 0)
				{
					return false;
				}
				List<ProxyResourcePackEntry> updated = new List<ProxyResourcePackEntry>(packs.Count + 1);
				foreach (ProxyResourcePackEntry pack in packs)
				{
					if (!pack.Uuid.Equals(entry.Uuid))
					{
						updated.Add(pack);
					}
				}
				updated.Add(entry);
				Install(updated);
				return true;
			}
		}

		/// <summary>Reads a pack from bytes exactly as a .mcpack on disk would be read.</summary>
		public static ProxyResourcePackEntry? EntryFrom(byte[] data)
		{
			ManifestInfo? manifest = ParseManifest(data);
			if (manifest == null)
			{
				return null;
			}
			return new ProxyResourcePackEntry(manifest.Uuid, manifest.Version, manifest.Name, data, Sha256(data));
		}

		/// <summary>
		/// Loads the operator's packs plus the ones cached from backends, into a registry that can still
		/// learn more while the proxy runs.
		///
		/// <para>A pack placed in <paramref name="dir"/> by hand wins a tie against the cached copy of
		/// the same version: it is the one an operator can actually edit.</para>
		/// </summary>
		public static ProxyResourcePackRegistry Load(string? dir, string? cacheDir)
		{
			ProxyResourcePackRegistry registry = MutableEmpty();
			foreach (ProxyResourcePackEntry entry in LoadEntries(dir, ""))
			{
				registry.Add(entry);
			}
			foreach (ProxyResourcePackEntry entry in LoadEntries(cacheDir, ", cached from a backend"))
			{
				registry.Add(entry);
			}
			return registry;
		}

		public static ProxyResourcePackRegistry Empty()
		{
			return EMPTY;
		}

		public static ProxyResourcePackRegistry Load(string? dir)
		{
			List<ProxyResourcePackEntry> loaded = LoadEntries(dir, "");
			if (loaded.Count == 0)
			{
				return EMPTY;
			}
			return new ProxyResourcePackRegistry(loaded);
		}

		private static List<ProxyResourcePackEntry> LoadEntries(string? dir, string origin)
		{
			if (dir == null || !Directory.Exists(dir))
			{
				return new List<ProxyResourcePackEntry>();
			}
			List<ProxyResourcePackEntry> loaded = new List<ProxyResourcePackEntry>();
			try
			{
				IEnumerable<string> paths = Directory.EnumerateFileSystemEntries(dir);
				foreach (string path in paths.Where(p => LooksLikePack(p)).OrderBy(p => p, StringComparer.Ordinal))
				{
					try
					{
						ProxyResourcePackEntry? entry = Directory.Exists(path) ? LoadFolderPack(path) : LoadPack(path);
						if (entry != null)
						{
							loaded.Add(entry);
							Logger.Info(
								$"Loaded proxy resource pack: {entry.Name} v{entry.VersionString()} (uuid={entry.Uuid}, {entry.Data.Length} bytes"
								+ (Directory.Exists(path) ? ", zipped from folder" : "")
								+ origin + ")."
							);
						}
					}
					catch (Exception e)
					{
						Logger.Info($"Failed to load proxy resource pack {Path.GetFileName(path)}: {e.Message}");
					}
				}
			}
			catch (Exception e)
			{
				Logger.Info($"Failed to list proxy resource packs directory {dir}: {e.Message}");
				return new List<ProxyResourcePackEntry>();
			}
			return loaded;
		}

		/// <summary>A packaged pack file, or a directory that could hold an unpackaged one.</summary>
		private static bool LooksLikePack(string path)
		{
			if (Directory.Exists(path))
			{
				return true;
			}
			string name = Path.GetFileName(path).ToLowerInvariant();
			return name.EndsWith(".mcpack", StringComparison.Ordinal)
				|| name.EndsWith(".zip", StringComparison.Ordinal);
		}

		private static ProxyResourcePackEntry? LoadPack(string path)
		{
			byte[] data = File.ReadAllBytes(path);
			ManifestInfo? manifest = ParseManifest(data);
			if (manifest == null)
			{
				Logger.Info($"Skipping {Path.GetFileName(path)}: no valid manifest.json found.");
				return null;
			}
			byte[] hash = Sha256(data);
			return new ProxyResourcePackEntry(manifest.Uuid, manifest.Version, manifest.Name, data, hash);
		}

		/// <summary>
		/// Load an unpackaged pack: a directory holding manifest.json, zipped in memory so the rest of
		/// the pipeline sees exactly what a .mcpack would have given it.
		///
		/// <para>The zip is built deterministically — entries sorted, one fixed timestamp — so a pack
		/// that did not change on disk keeps the same SHA-256 across restarts and clients do not
		/// redownload it.</para>
		/// </summary>
		private static ProxyResourcePackEntry? LoadFolderPack(string dir)
		{
			string? root = FindManifestRoot(dir);
			if (root == null)
			{
				// Not every directory beside the packs is a pack; say nothing about the ones that aren't.
				return null;
			}
			ManifestInfo? manifest = ParseManifestJson(File.ReadAllText(Path.Combine(root, "manifest.json"), System.Text.Encoding.UTF8));
			if (manifest == null)
			{
				Logger.Info($"Skipping {Path.GetFileName(dir)}: manifest.json is not a valid pack manifest.");
				return null;
			}
			byte[] data = ZipDirectory(root);
			return new ProxyResourcePackEntry(manifest.Uuid, manifest.Version, manifest.Name, data, Sha256(data));
		}

		private const string MANIFEST = "manifest.json";

		/// <summary>2000-01-01T00:00:00Z. Any fixed value inside the DOS-time range keeps folder zips reproducible.</summary>
		private static readonly DateTimeOffset ZIP_TIMESTAMP = new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);

		/// <summary>
		/// The directory the zip should be rooted at: the folder itself when manifest.json sits in it,
		/// or its single subdirectory when the pack was unpacked one level down (the shape you get from
		/// extracting a .mcpack that wrapped its contents in a folder).
		/// </summary>
		private static string? FindManifestRoot(string dir)
		{
			if (File.Exists(Path.Combine(dir, MANIFEST)))
			{
				return dir;
			}
			List<string> children;
			try
			{
				children = Directory.EnumerateFileSystemEntries(dir).OrderBy(p => p, StringComparer.Ordinal).ToList();
			}
			catch (Exception)
			{
				return null;
			}
			string? nested = null;
			foreach (string child in children)
			{
				if (Directory.Exists(child) && File.Exists(Path.Combine(child, MANIFEST)))
				{
					if (nested != null)
					{
						// Several packs side by side: ambiguous, and picking one would hide the others.
						string parentName = Path.GetDirectoryName(Path.GetFullPath(dir));
						string hint = Path.GetDirectoryName(dir) == null ? "the packs directory" : Path.GetFileName(parentName ?? "");
						Logger.Info(
							$"Skipping {Path.GetFileName(dir)}: it holds several packs; move each one into {hint} directly.");
						return null;
					}
					nested = child;
				}
			}
			return nested;
		}

		private static byte[] ZipDirectory(string root)
		{
			List<string> files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
				.Where(p => !IsJunk(Path.GetFileName(p)))
				.OrderBy(p => p, StringComparer.Ordinal)
				.ToList();
			using MemoryStream buffer = new MemoryStream();
			using (ZipArchive zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
			{
				foreach (string file in files)
				{
					string name = Path.GetRelativePath(root, file).Replace('\\', '/');
					ZipArchiveEntry entry = zip.CreateEntry(name);
					entry.LastWriteTime = ZIP_TIMESTAMP;
					using Stream entryStream = entry.Open();
					using FileStream input = File.OpenRead(file);
					input.CopyTo(entryStream);
				}
			}
			return buffer.ToArray();
		}

		private static bool IsJunk(string fileName)
		{
			return fileName.Equals(".DS_Store", StringComparison.Ordinal)
				|| fileName.Equals("Thumbs.db", StringComparison.OrdinalIgnoreCase)
				|| fileName.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase);
		}

		private static ManifestInfo? ParseManifest(byte[] zipData)
		{
			try
			{
				using MemoryStream stream = new MemoryStream(zipData, writable: false);
				using ZipArchive zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
				foreach (ZipArchiveEntry entry in zip.Entries)
				{
					string name = entry.FullName.ToLowerInvariant();
					if (name.Equals("manifest.json", StringComparison.Ordinal) || name.EndsWith("/manifest.json", StringComparison.Ordinal))
					{
						byte[] manifestBytes = ReadAllBytes(entry);
						return ParseManifestJson(System.Text.Encoding.UTF8.GetString(manifestBytes));
					}
				}
			}
			catch (Exception)
			{
				// Treated exactly like "no manifest found", matching the Java catch-all.
			}
			return null;
		}

		private static ManifestInfo? ParseManifestJson(string json)
		{
			try
			{
				// Locate the "header" object and extract uuid, version array and name from it.
				// (The Java original hand-scanned the text; a JSON DOM does the same job properly.)
				using JsonDocument document = JsonDocument.Parse(json);
				JsonElement header;
				if (!TryFindHeaderObject(document.RootElement, out header))
				{
					return null;
				}

				if (!header.TryGetProperty("uuid", out JsonElement uuidElement) || uuidElement.ValueKind != JsonValueKind.String
					|| !Guid.TryParseExact(uuidElement.GetString(), "D", out Guid uuid))
				{
					return null;
				}

				int[] version = { 1, 0, 0 };
				int[]? parsed = ExtractIntArray(header, "version");
				if (parsed != null && parsed.Length >= 3)
				{
					version = parsed;
				}

				string name = "Resource Pack";
				string? extractedName = ExtractStringValue(header, "name");
				if (!string.IsNullOrWhiteSpace(extractedName))
				{
					name = extractedName!;
				}

				return new ManifestInfo(uuid, version, name);
			}
			catch (Exception)
			{
				return null;
			}
		}

		private static bool TryFindHeaderObject(JsonElement element, out JsonElement header)
		{
			switch (element.ValueKind)
			{
				case JsonValueKind.Object:
					foreach (JsonProperty property in element.EnumerateObject())
					{
						if (property.Value.ValueKind == JsonValueKind.Object && property.NameEquals("header"))
						{
							header = property.Value;
							return true;
						}
					}
					foreach (JsonProperty property in element.EnumerateObject())
					{
						if ((property.Value.ValueKind == JsonValueKind.Object || property.Value.ValueKind == JsonValueKind.Array)
							&& TryFindHeaderObject(property.Value, out header))
						{
							return true;
						}
					}
					break;
				case JsonValueKind.Array:
					foreach (JsonElement item in element.EnumerateArray())
					{
						if (item.ValueKind is JsonValueKind.Object or JsonValueKind.Array && TryFindHeaderObject(item, out header))
						{
							return true;
						}
					}
					break;
			}
			header = default;
			return false;
		}

		private static string? ExtractStringValue(JsonElement parent, string key)
		{
			if (!parent.TryGetProperty(key, out JsonElement value) || value.ValueKind != JsonValueKind.String)
			{
				return null;
			}
			return value.GetString();
		}

		private static int[]? ExtractIntArray(JsonElement parent, string key)
		{
			if (!parent.TryGetProperty(key, out JsonElement value) || value.ValueKind != JsonValueKind.Array)
			{
				return null;
			}
			List<int> result = new List<int>(value.GetArrayLength());
			foreach (JsonElement item in value.EnumerateArray())
			{
				result.Add(item.GetInt32());
			}
			return result.ToArray();
		}

		private static byte[] ReadAllBytes(ZipArchiveEntry entry)
		{
			using Stream input = entry.Open();
			using MemoryStream output = new MemoryStream();
			input.CopyTo(output);
			return output.ToArray();
		}

		private static byte[] Sha256(byte[] data)
		{
			return SHA256.HashData(data);
		}

		public bool IsEmpty()
		{
			return packs.Count == 0;
		}

		public IReadOnlyList<ProxyResourcePackEntry> Packs()
		{
			return packs;
		}

		public ProxyResourcePackEntry? FindByUuid(Guid uuid)
		{
			return packsByUuid.TryGetValue(uuid, out ProxyResourcePackEntry? pack) ? pack : null;
		}

		public bool IsProxyPack(Guid uuid)
		{
			return packsByUuid.ContainsKey(uuid);
		}

		/// <summary>
		/// Build a merged ResourcePacksInfoPacket with proxy packs prepended to backend packs.
		/// UUID conflicts are resolved by version: the newer version wins.
		/// </summary>
		public ResourcePacksInfoPacket BuildMergedInfo(ResourcePacksInfoPacket backendInfo)
		{
			ResourcePacksInfoPacket merged = new ResourcePacksInfoPacket();
			merged.ResourcePackRequired = backendInfo.ResourcePackRequired;
			merged.HasAddonPacks = backendInfo.HasAddonPacks;
			merged.HasScripts = backendInfo.HasScripts;
			// Java also copied forcingServerPacksEnabled; protocol 2168 has no such field. The
			// closest surviving flag is ForceDisableVibrantVisuals - carried over so the backend's
			// intent survives instead of silently resetting to false.
			merged.ForceDisableVibrantVisuals = backendInfo.ForceDisableVibrantVisuals;
			merged.WorldTemplateIdAndVersion = backendInfo.WorldTemplateIdAndVersion != null
					? backendInfo.WorldTemplateIdAndVersion
					: NewZeroWorldTemplate();

			// Index backend resource packs by UUID
			Dictionary<Guid, PackInfoData> backendByUuid = new Dictionary<Guid, PackInfoData>();
			foreach (PackInfoData e in backendInfo.ResourcePacks)
			{
				backendByUuid[ToGuid(e.PackIdVersion.PackUUID)] = e;
			}

			// Process proxy packs: add them (winning UUID conflicts by version)
			HashSet<Guid> addedFromProxy = new HashSet<Guid>();
			foreach (ProxyResourcePackEntry proxyPack in packs)
			{
				PackInfoData? conflict;
				backendByUuid.TryGetValue(proxyPack.Uuid, out conflict);
				if (conflict != null)
				{
					int[] backendVer = ParseVersion(conflict.PackIdVersion.PackVersion?.Version);
					if (CompareVersions(proxyPack.Version, backendVer) >= 0)
					{
						// Proxy version wins (or tie): use proxy pack.
						//
						// Silent. This is per pack, per join, and the ordinary case is a tie — five
						// identical lines every time anyone connects. The genuinely interesting variant,
						// where the two versions actually differ, is not worth reinstating here either:
						// it would still repeat on every join. If that needs reporting, do it once when
						// the registry loads, comparing against the backend's advertised set.
						merged.ResourcePacks.Add(proxyPack.ToInfoEntry());
						backendByUuid.Remove(proxyPack.Uuid);
					}
					// else: backend version wins; backend entry stays in backendByUuid
				}
				else
				{
					merged.ResourcePacks.Add(proxyPack.ToInfoEntry());
				}
				addedFromProxy.Add(proxyPack.Uuid);
			}

			// Add remaining backend resource packs (those not displaced by proxy)
			foreach (PackInfoData e in backendInfo.ResourcePacks)
			{
				if (backendByUuid.ContainsKey(ToGuid(e.PackIdVersion.PackUUID)))
				{
					merged.ResourcePacks.Add(e);
				}
			}

			// Behavior packs: pass through unchanged. This protocol version folds behaviour packs
			// into the same list as resource packs, so there is no separate list to forward.
			return merged;
		}

		/// <summary>
		/// Build a merged ResourcePackStackPacket injecting proxy packs.
		/// UUID conflicts resolved by version (newer wins).
		/// </summary>
		public ResourcePackStackPacket BuildMergedStack(ResourcePackStackPacket backendStack)
		{
			ResourcePackStackPacket merged = new ResourcePackStackPacket();
			merged.TexturePackRequired = backendStack.TexturePackRequired;
			merged.BaseGameVersion = backendStack.BaseGameVersion;
			merged.IncludeEditorPacks = backendStack.IncludeEditorPacks;
			Experiments experiments = new Experiments();
			experiments.Toggles.AddRange(backendStack.Experiments != null ? backendStack.Experiments.Toggles : new List<global::Protocol.Types.ExperimentsAnon.ExperimentToggle>());
			experiments.ExperimentsEverToggled = backendStack.Experiments != null && backendStack.Experiments.ExperimentsEverToggled;
			merged.Experiments = experiments;

			// Index backend resource stack by UUID string (lowercase)
			Dictionary<string, PackInstanceId> backendStackByUuid = new Dictionary<string, PackInstanceId>(StringComparer.Ordinal);
			foreach (PackInstanceId e in backendStack.TexturePackList)
			{
				backendStackByUuid[e.PackID.ToLowerInvariant()] = e;
			}

			// Add proxy packs to stack (resolving UUID conflicts)
			foreach (ProxyResourcePackEntry proxyPack in packs)
			{
				string uuidKey = proxyPack.Uuid.ToString().ToLowerInvariant();
				PackInstanceId? conflict;
				backendStackByUuid.TryGetValue(uuidKey, out conflict);
				if (conflict != null)
				{
					int[] backendVer = ParseVersion(conflict.Version);
					if (CompareVersions(proxyPack.Version, backendVer) >= 0)
					{
						// Proxy version wins: replace backend entry
						merged.TexturePackList.Add(NewStackEntry(proxyPack));
						backendStackByUuid.Remove(uuidKey);
					}
					// else: backend version wins; backend entry stays
				}
				else
				{
					merged.TexturePackList.Add(NewStackEntry(proxyPack));
				}
			}

			// Add remaining backend stack entries
			foreach (PackInstanceId e in backendStack.TexturePackList)
			{
				if (backendStackByUuid.ContainsKey(e.PackID.ToLowerInvariant()))
				{
					merged.TexturePackList.Add(e);
				}
			}

			// Behaviour packs: folded into TexturePackList in this protocol version; they were
			// already carried across above.
			return merged;
		}

		private static PackInstanceId NewStackEntry(ProxyResourcePackEntry pack)
		{
			return new PackInstanceId
			{
				PackID = pack.Uuid.ToString(),
				Version = pack.VersionString(),
				SubPackName = ""
			};
		}

		private static PackIdVersion NewZeroWorldTemplate()
		{
			// new UUID(0, 0) with an empty version string
			return new PackIdVersion
			{
				PackUUID = new MceUuid(),
				PackVersion = new SemVersion { Version = "" }
			};
		}

		/// <summary>Send ResourcePackDataInfoPacket to client for a proxy pack.</summary>
		public void SendDataInfo(ListenerSession client, Guid packId)
		{
			if (!packsByUuid.TryGetValue(packId, out ProxyResourcePackEntry? pack) || pack == null)
			{
				return;
			}

			long chunkCount = (long)Math.Ceiling((double)pack.Data.Length / ProxyResourcePackEntry.CHUNK_SIZE);
			ResourcePackDataInfoPacket dataInfo = new ResourcePackDataInfoPacket();
			dataInfo.ResourceName = packId.ToString();
			dataInfo.ChunkSize = ProxyResourcePackEntry.CHUNK_SIZE;
			dataInfo.NumberOfChunks = (uint)chunkCount;
			dataInfo.FileSize = (ulong)pack.Data.Length;
			dataInfo.FileHash = pack.Hash;
			dataInfo.IsPremiumPack = false;
			dataInfo.PackType = (byte)PackType.Resources;
			client.SendPacket(dataInfo);
		}

		/// <summary>Send a ResourcePackChunkDataPacket to client for a proxy pack chunk.</summary>
		public void SendChunk(ListenerSession client, Guid packId, int chunkIndex)
		{
			if (!packsByUuid.TryGetValue(packId, out ProxyResourcePackEntry? pack) || pack == null)
			{
				return;
			}

			int start = chunkIndex * ProxyResourcePackEntry.CHUNK_SIZE;
			if (start >= pack.Data.Length)
			{
				return;
			}
			int end = Math.Min(start + ProxyResourcePackEntry.CHUNK_SIZE, pack.Data.Length);
			byte[] chunk = new byte[end - start];
			Buffer.BlockCopy(pack.Data, start, chunk, 0, end - start);

			ResourcePackChunkDataPacket chunkData = new ResourcePackChunkDataPacket();
			chunkData.ResourceName = packId.ToString();
			chunkData.ChunkID = (uint)chunkIndex;
			chunkData.ByteOffset = (ulong)start;
			chunkData.ChunkData = chunk;
			client.SendPacket(chunkData);
		}

		internal static Guid ToGuid(MceUuid uuid)
		{
			Span<byte> bytes = stackalloc byte[16];
			ulong mostSignificantBits = uuid.MostSignificantBits;
			ulong leastSignificantBits = uuid.LeastSignificantBits;
			for (int i = 7; i >= 0; i--)
			{
				bytes[i] = (byte)mostSignificantBits;
				mostSignificantBits >>= 8;
			}
			for (int i = 15; i >= 8; i--)
			{
				bytes[i] = (byte)leastSignificantBits;
				leastSignificantBits >>= 8;
			}
			return new Guid(bytes);
		}

		// ---- version helpers ----

		public static int[] ParseVersion(string? version)
		{
			if (string.IsNullOrEmpty(version))
			{
				return new int[] { 0, 0, 0 };
			}
			string[] parts = version.Split('.');
			int[] result = { 0, 0, 0 };
			for (int i = 0; i < Math.Min(parts.Length, 3); i++)
			{
				if (int.TryParse(parts[i].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed))
				{
					result[i] = parsed;
				}
			}
			return result;
		}

		public static int CompareVersions(int[] a, int[] b)
		{
			for (int i = 0; i < 3; i++)
			{
				int cmp = a[i].CompareTo(b[i]);
				if (cmp != 0)
				{
					return cmp;
				}
			}
			return 0;
		}

		// ---- inner records ----

		private sealed record ManifestInfo(Guid Uuid, int[] Version, string Name);
	}
}
