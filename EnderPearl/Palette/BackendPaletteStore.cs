using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using Protocol.Types;
using EnderPearl.Logging;

namespace EnderPearl.Palette
{
	/// <summary>
	/// Every backend's registries, learned from live sessions and remembered across restarts.
	///
	/// <para>A client can only be given the union of all backends' content at the moment it logs in,
	/// which is before it has been anywhere. The proxy therefore learns each backend's palette the first
	/// time anyone visits it and writes it to disk, so the next login already knows about backends this
	/// session has not touched. The cost of that design is one stale visit: after a backend's addons
	/// change, the first player to go there sees the old registry until they rejoin. That is reported,
	/// not silent — see <see cref="ItemPaletteMapping.UnmappedFromBackend"/>.</para>
	///
	/// <para>Nothing here is a security boundary: the cache holds only what backends already send every
	/// client at login. It is still read with a size limit, because a corrupt file should fail the load,
	/// not the process.</para>
	///
	/// <para>Deviation from Java: the cache file is GZIP-wrapped network-order NBT written by this
	/// library's own tag writer rather than cloudburst's big-endian GZIP NBT. The file is local state
	/// that never touches the wire, so only cross-build cache portability is lost.</para>
	/// </summary>
	public sealed class BackendPaletteStore
	{
		private const long MAX_CACHE_BYTES = 64L * 1024 * 1024;
		private const string BACKENDS = "backends";
		private const string ITEMS = "items";
		private const string ENTITY_IDENTIFIERS = "entityIdentifiers";
		private const string ENTITY_PROPERTIES = "entityProperties";
		private const string BLOCK_PROPERTIES = "blockProperties";
		private const string BLOCK_IDS_HASHED = "blockIdsHashed";
		private const string BLOCK_PROPERTIES_DATA = "data";
		private const string NAME = "name";
		private const string RUNTIME_ID = "id";
		private const string COMPONENT_BASED = "componentBased";
		private const string VERSION = "version";
		private const string COMPONENT_DATA = "componentData";

		private readonly string? cacheFile;
		private readonly Dictionary<string, BackendPalette> palettes = new();
		private readonly HashSet<string> reported = new();
		private readonly bool enabled;
		private readonly object mutex = new();

		private BackendPaletteStore(string? cacheFile, bool enabled)
		{
			this.cacheFile = cacheFile;
			this.enabled = enabled;
		}

		/// <summary>A store that learns nothing and unions nothing; every backend keeps its own ids.</summary>
		public static BackendPaletteStore Disabled()
		{
			return new BackendPaletteStore(null, false);
		}

		/// <summary>Loads the cache at <paramref name="cacheFile"/>, or starts empty when it does not exist or is unreadable.</summary>
		public static BackendPaletteStore Load(string? cacheFile)
		{
			BackendPaletteStore store = new(cacheFile, true);
			if (cacheFile == null || !File.Exists(cacheFile))
			{
				return store;
			}
			try
			{
				using FileStream input = File.OpenRead(cacheFile);
				if (input.Length > MAX_CACHE_BYTES)
				{
					throw new IOException("cache exceeds " + MAX_CACHE_BYTES + " bytes");
				}
				using GZipStream gzip = new(input, CompressionMode.Decompress);
				MemoryStream buffer = new();
				gzip.CopyTo(buffer);
				buffer.Position = 0;
				global::Protocol.Utility.IO.MemoryStreamReader reader = new(buffer.ToArray());
				CompoundTag root = new();
				root.Read(reader);
				store.ReadFrom(root);
			}
			catch (Exception e)
			{
				Logger.Info(
					$"Could not read the backend palette cache {cacheFile} ({e.Message}); it will be relearned as players visit each backend.");
				lock (store.mutex)
				{
					store.palettes.Clear();
				}
			}
			return store;
		}

		public bool IsEnabled()
		{
			return enabled;
		}

		public ISet<string> KnownBackends()
		{
			lock (mutex)
			{
				return new SortedSet<string>(palettes.Keys);
			}
		}

		public BackendPalette? Palette(string backendName)
		{
			lock (mutex)
			{
				return palettes.GetValueOrDefault(backendName);
			}
		}

		/// <summary>Every known palette except <paramref name="backendName"/>'s, in a stable order.</summary>
		public List<BackendPalette> OtherPalettes(string? backendName)
		{
			List<BackendPalette> others = new();
			lock (mutex)
			{
				foreach (string name in palettes.Keys.OrderBy(k => k, StringComparer.Ordinal))
				{
					if (!name.Equals(backendName, StringComparison.Ordinal))
					{
						others.Add(palettes[name]);
					}
				}
			}
			return others;
		}

		/// <returns>true when this changed what was known, so a player who logged in earlier may be holding a stale union</returns>
		public bool LearnItems(string? backendName, List<ItemData>? items)
		{
			if (!enabled || backendName == null || items == null || items.Count == 0)
			{
				return false;
			}
			lock (mutex)
			{
				BackendPalette existing = palettes.GetValueOrDefault(backendName) ?? BackendPalette.Empty(backendName);
				if (BackendPalette.SameItems(existing.Items, items))
				{
					return false;
				}
				palettes[backendName] = existing.WithItems(items);
				Save();
				return true;
			}
		}

		public bool LearnEntityIdentifiers(string? backendName, CompoundTag? identifiers)
		{
			if (!enabled || backendName == null || identifiers == null)
			{
				return false;
			}
			lock (mutex)
			{
				BackendPalette existing = palettes.GetValueOrDefault(backendName) ?? BackendPalette.Empty(backendName);
				if ((existing.EntityIdentifiers == null && identifiers.Value.Count == 0)
					|| (existing.EntityIdentifiers != null && existing.EntityIdentifiers.NbtEquals(identifiers)))
				{
					return false;
				}
				palettes[backendName] = existing.WithEntityIdentifiers(identifiers);
				Save();
				return true;
			}
		}

		public bool LearnBlockProperties(string? backendName, List<ServerBlockProperty>? blockProperties)
		{
			if (!enabled || backendName == null || blockProperties == null || blockProperties.Count == 0)
			{
				return false;
			}
			lock (mutex)
			{
				BackendPalette existing = palettes.GetValueOrDefault(backendName) ?? BackendPalette.Empty(backendName);
				if (SameBlockProperties(existing.BlockProperties, blockProperties))
				{
					return false;
				}
				palettes[backendName] = existing.WithBlockProperties(blockProperties);
				Save();
				return true;
			}
		}

		/// <summary>
		/// Records whether a backend hashes its block network ids.
		///
		/// <para>Unlike the rest of the store this is learned even when the palette itself is not shared,
		/// because it is exactly the backends whose blocks cannot be shared that this has to be known
		/// for.</para>
		/// </summary>
		public bool LearnBlockIdsHashed(string? backendName, bool blockIdsHashed)
		{
			if (!enabled || backendName == null)
			{
				return false;
			}
			lock (mutex)
			{
				BackendPalette existing = palettes.GetValueOrDefault(backendName) ?? BackendPalette.Empty(backendName);
				if (existing.BlockIdsHashed != null && existing.BlockIdsHashed.Value == blockIdsHashed)
				{
					return false;
				}
				palettes[backendName] = existing.WithBlockIdsHashed(blockIdsHashed);
				Save();
				return true;
			}
		}

		/// <summary>Whether the named backend hashes block ids, or null if it has never been seen.</summary>
		public bool? BlockIdsHashed(string? backendName)
		{
			if (backendName == null)
			{
				return null;
			}
			lock (mutex)
			{
				BackendPalette? palette = palettes.GetValueOrDefault(backendName);
				return palette?.BlockIdsHashed;
			}
		}

		public bool LearnEntityProperty(string? backendName, CompoundTag? property)
		{
			if (!enabled || backendName == null || property == null || property.IsEmpty())
			{
				return false;
			}
			lock (mutex)
			{
				BackendPalette existing = palettes.GetValueOrDefault(backendName) ?? BackendPalette.Empty(backendName);
				BackendPalette updated = existing.WithEntityProperty(property);
				if (!EntityPropertiesEqual(updated.EntityProperties, existing.EntityProperties))
				{
					palettes[backendName] = updated;
					Save();
					return true;
				}
				return false;
			}
		}

		/// <summary>
		/// True the first time this exact outcome is seen, so a per-join fact can be reported once.
		///
		/// <para>Everything the palette does happens on every login of every player. Logging it per join
		/// would bury the lines that need acting on under thousands of identical ones. Keying on the
		/// outcome means a genuine change still speaks up.</para>
		/// </summary>
		public bool FirstReportOf(string key)
		{
			lock (mutex)
			{
				return reported.Add(key);
			}
		}

		/// <summary>One line describing what is known, for the startup banner.</summary>
		public string Describe()
		{
			lock (mutex)
			{
				if (!enabled)
				{
					return "cross-backend palette off";
				}
				if (palettes.Count == 0)
				{
					return "no backend palettes learned yet";
				}
				System.Text.StringBuilder builder = new();
				foreach (string backendName in palettes.Keys.OrderBy(k => k, StringComparer.Ordinal))
				{
					BackendPalette palette = palettes[backendName];
					if (builder.Length > 0)
					{
						builder.Append(", ");
					}
					builder.Append(backendName)
						.Append(": ")
						.Append(palette.Items.Count)
						.Append(" items, ")
						.Append(EntityPalettes.IdList(palette.EntityIdentifiers).Count)
						.Append(" entities");
				}
				return builder.ToString();
			}
		}

		private void Save()
		{
			if (cacheFile == null)
			{
				return;
			}
			string temporary = cacheFile + ".tmp";
			try
			{
				string? parent = Path.GetDirectoryName(Path.GetFullPath(cacheFile));
				if (parent != null)
				{
					Directory.CreateDirectory(parent);
				}
				using (FileStream output = File.Create(temporary))
				using (GZipStream gzip = new(output, CompressionLevel.Optimal))
				using (MemoryStream buffer = new())
				{
					WriteTo().Write(new global::Protocol.Utility.IO.MemoryStreamWriter(buffer));
					buffer.Position = 0;
					buffer.CopyTo(gzip);
				}
				// Replace in one step: a half-written cache read back at the next start would be a
				// wrong union, which is worse than no cache at all.
				File.Move(temporary, cacheFile, overwrite: true);
			}
			catch (IOException e)
			{
				Logger.Info($"Could not write the backend palette cache {cacheFile}: {e.Message}");
				try
				{
					File.Delete(temporary);
				}
				catch (IOException)
				{
				}
			}
			catch (UnauthorizedAccessException e)
			{
				Logger.Info($"Could not write the backend palette cache {cacheFile}: {e.Message}");
				try
				{
					File.Delete(temporary);
				}
				catch (IOException)
				{
				}
			}
		}

		private CompoundTag WriteTo()
		{
			CompoundTag backends = new();
			foreach (KeyValuePair<string, BackendPalette> entry in palettes)
			{
				BackendPalette palette = entry.Value;
				List<CompoundTag> items = new(palette.Items.Count);
				foreach (ItemData item in palette.Items)
				{
					CompoundTag builder = new();
					builder.PutString(NAME, item.ItemName);
					builder.PutInt(RUNTIME_ID, item.ItemId);
					builder.PutBoolean(COMPONENT_BASED, item.IsComponentBased);
					builder.PutInt(VERSION, (int)item.ItemVersion);
					if (item.ItemComponentData != null)
					{
						builder.PutCompound(COMPONENT_DATA, item.ItemComponentData);
					}
					items.Add(builder);
				}
				List<CompoundTag> blocks = new(palette.BlockProperties.Count);
				foreach (ServerBlockProperty block in palette.BlockProperties)
				{
					CompoundTag blockTag = new();
					blockTag.PutString(NAME, block.BlockName ?? "");
					blockTag.PutCompound(BLOCK_PROPERTIES_DATA, block.BlockDefinition ?? new CompoundTag());
					blocks.Add(blockTag);
				}
				CompoundTag backend = new();
				backend.PutCompoundList(ITEMS, items);
				backend.PutCompoundList(BLOCK_PROPERTIES, blocks);
				backend.PutCompoundList(ENTITY_PROPERTIES, palette.EntityProperties.ToList());
				// Written only once seen, so "never visited" stays distinguishable from "visited and
				// does not hash" - the switcher treats those two cases differently.
				if (palette.BlockIdsHashed != null)
				{
					backend.PutBoolean(BLOCK_IDS_HASHED, palette.BlockIdsHashed.Value);
				}
				if (palette.EntityIdentifiers != null)
				{
					backend.PutCompound(ENTITY_IDENTIFIERS, palette.EntityIdentifiers);
				}
				backends.PutCompound(entry.Key, backend);
			}
			CompoundTag root = new();
			root.PutCompound(BACKENDS, backends);
			return root;
		}

		private void ReadFrom(CompoundTag? root)
		{
			if (root == null)
			{
				return;
			}
			CompoundTag backends = root.GetCompound(BACKENDS);
			if (backends.Value.Count == 0)
			{
				return;
			}
			global::Protocol.ItemVersion[] versions = (global::Protocol.ItemVersion[])Enum.GetValues(typeof(global::Protocol.ItemVersion));
			foreach (KeyValuePair<string, TagVariant> pair in backends.Value)
			{
				if (pair.Value.Type != TagType.Compound || !pair.Value.Value.TryPickT10(out CompoundTag? backend))
				{
					continue;
				}
				string backendName = pair.Key;
				List<ItemData> items = new();
				foreach (CompoundTag item in backend.GetCompoundList(ITEMS))
				{
					int versionOrdinal = item.GetInt(VERSION);
					ItemData data = new()
					{
						ItemName = item.GetString(NAME),
						ItemId = (short)item.GetInt(RUNTIME_ID),
						IsComponentBased = item.GetBoolean(COMPONENT_BASED),
						ItemVersion = versionOrdinal >= 0 && versionOrdinal < versions.Length
							? versions[versionOrdinal]
							: DefaultLegacyVersion(),
					};
					if (item.Value.ContainsKey(COMPONENT_DATA))
					{
						data.ItemComponentData = item.GetCompound(COMPONENT_DATA);
					}
					else
					{
						data.ItemComponentData = new CompoundTag();
					}
					items.Add(data);
				}
				List<ServerBlockProperty> blocks = new();
				foreach (CompoundTag block in backend.GetCompoundList(BLOCK_PROPERTIES))
				{
					blocks.Add(new ServerBlockProperty
					{
						BlockName = block.GetString(NAME),
						BlockDefinition = block.Value.ContainsKey(BLOCK_PROPERTIES_DATA)
							? block.GetCompound(BLOCK_PROPERTIES_DATA)
							: new CompoundTag(),
					});
				}
				bool? blockIdsHashed = backend.Value.ContainsKey(BLOCK_IDS_HASHED)
					? backend.GetBoolean(BLOCK_IDS_HASHED)
					: null;
				CompoundTag? entityIdentifiers = backend.Value.ContainsKey(ENTITY_IDENTIFIERS)
					? backend.GetCompound(ENTITY_IDENTIFIERS)
					: null;
				palettes[backendName] = new BackendPalette(
					backendName,
					items,
					entityIdentifiers,
					backend.GetCompoundList(ENTITY_PROPERTIES),
					blocks,
					blockIdsHashed
				);
			}
		}

		/// <summary>Java falls back to ItemVersion.LEGACY for an unknown ordinal; use ordinal 0 here.</summary>
		private static global::Protocol.ItemVersion DefaultLegacyVersion()
		{
			return global::Protocol.ItemVersion.Legacy;
		}

		private static bool SameBlockProperties(List<ServerBlockProperty> a, List<ServerBlockProperty> b)
		{
			if (a.Count != b.Count)
			{
				return false;
			}
			for (int i = 0; i < a.Count; i++)
			{
				if (!string.Equals(a[i].BlockName, b[i].BlockName, StringComparison.Ordinal)
					|| !a[i].BlockDefinition.NbtEquals(b[i].BlockDefinition))
				{
					return false;
				}
			}
			return true;
		}

		private static bool EntityPropertiesEqual(List<CompoundTag> a, List<CompoundTag> b)
		{
			if (a.Count != b.Count)
			{
				return false;
			}
			for (int i = 0; i < a.Count; i++)
			{
				if (!a[i].NbtEquals(b[i]))
				{
					return false;
				}
			}
			return true;
		}
	}
}
