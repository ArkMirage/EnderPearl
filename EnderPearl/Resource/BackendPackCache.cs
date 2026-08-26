using System;
using System.IO;
using System.Linq;
using EnderPearl.Logging;

namespace EnderPearl.Resource
{
	/// <summary>
	/// Backend resource packs the proxy has seen, kept on disk so it can serve them itself.
	///
	/// <para>A client downloads packs during its login handshake and never again, so a backend joined
	/// mid-session cannot ask for its packs: the proxy answers that handshake on the player's behalf
	/// and the packs are simply missing for them. Serving every backend's packs at login is the fix,
	/// and this is what removes the manual step of copying each one into the resourcePacks directory —
	/// the proxy keeps a copy the first time it sees the bytes, and every login after that includes
	/// them.</para>
	///
	/// <para>Files are named <c>&lt;uuid&gt;_&lt;version&gt;.mcpack</c>, so a pack whose version is bumped is a new
	/// file rather than an overwrite, and an operator can see at a glance what the proxy has learned.
	/// The bytes are exactly what the backend sent, verified against the hash the backend advertised
	/// before anything is written.</para>
	/// </summary>
	public sealed class BackendPackCache
	{
		/// <summary>
		/// Refuse to buffer a pack larger than this. The download is held in memory to be hashed before
		/// it is trusted — the cap is what stops one misconfigured backend from deciding the proxy's
		/// heap size.
		/// </summary>
		public const int MAX_PACK_BYTES = 96 * 1024 * 1024;

		private readonly string? directory;
		private readonly ProxyResourcePackRegistry? registry;

		private BackendPackCache(string? directory, ProxyResourcePackRegistry? registry)
		{
			this.directory = directory;
			this.registry = registry;
		}

		/// <summary>A cache that remembers nothing, for resourcePacks.cacheBackendPacks=false.</summary>
		public static BackendPackCache Disabled()
		{
			return new BackendPackCache(null, null);
		}

		public static BackendPackCache Of(string directory, ProxyResourcePackRegistry registry)
		{
			return new BackendPackCache(directory, registry);
		}

		public bool IsEnabled()
		{
			return directory != null && registry != null;
		}

		/// <summary>True when this pack is already served by the proxy, from the cache or from the packs directory.</summary>
		public bool Has(Guid packId, int[]? version)
		{
			if (registry == null)
			{
				return false;
			}
			ProxyResourcePackEntry? existing = registry.FindByUuid(packId);
			return existing != null
				&& ProxyResourcePackRegistry.CompareVersions(existing.Version, version ?? new[] { 0, 0, 0 }) >= 0;
		}

		/// <summary>
		/// Verifies, stores and starts serving a pack downloaded from a backend.
		/// </summary>
		/// <param name="expectedHash">the hash the backend advertised, or null if it advertised none</param>
		/// <returns>true when the pack was accepted</returns>
		public bool Store(Guid packId, byte[]? data, byte[]? expectedHash)
		{
			if (!IsEnabled() || registry == null || directory == null || packId == Guid.Empty || data == null || data.Length == 0)
			{
				return false;
			}
			ProxyResourcePackEntry? entry = ProxyResourcePackRegistry.EntryFrom(data);
			if (entry == null)
			{
				Logger.Info($"Not caching backend pack {packId}: it has no readable manifest.json.");
				return false;
			}
			if (!entry.Uuid.Equals(packId))
			{
				// A pack whose manifest disagrees with the id it was served under would be served to
				// clients under the wrong identity, which is how one backend's pack silently shadows
				// another's.
				Logger.Info($"Not caching backend pack {packId}: its manifest claims uuid {entry.Uuid}.");
				return false;
			}
			if (expectedHash != null && expectedHash.Length > 0
				&& !expectedHash.AsSpan().SequenceEqual(entry.Hash))
			{
				Logger.Info(
					$"Not caching backend pack {packId} v{entry.VersionString()}: the download does not match the hash the backend advertised.");
				return false;
			}
			if (!registry.Add(entry))
			{
				return false;
			}
			Write(entry);
			Logger.Info(
				$"Cached resource pack {entry.Name} v{entry.VersionString()} (uuid={entry.Uuid}, {entry.Data.Length} bytes) from a backend; "
				+ "every client that logs in from now on gets it.");
			return true;
		}

		private void Write(ProxyResourcePackEntry entry)
		{
			string target = Path.Combine(directory!,
				entry.Uuid.ToString().ToLowerInvariant() + "_" + entry.VersionString() + ".mcpack");
			string temporary = target + ".tmp";
			try
			{
				Directory.CreateDirectory(directory!);
				File.WriteAllBytes(temporary, entry.Data);
				// Replaced in one step: a half-written pack read back at the next start would be served
				// to clients as though it were whole.
				File.Move(temporary, target, overwrite: true);
			}
			catch (Exception e) when (e is IOException or UnauthorizedAccessException)
			{
				Logger.Info($"Could not write cached resource pack {target}: {e.Message}");
				try
				{
					File.Delete(temporary);
				}
				catch (IOException)
				{
				}
			}
		}
	}
}
