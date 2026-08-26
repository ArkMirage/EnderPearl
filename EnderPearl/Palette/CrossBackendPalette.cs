using System;
using System.Collections.Generic;
using EnderPearl.Config;
using Protocol.Types;
using EnderPearl.Logging;

namespace EnderPearl.Palette
{
	/// <summary>
	/// One player's view of every backend's content, decided at their login and fixed for their session.
	///
	/// <para>Bedrock builds its item registry and its entity identifier list once, when the level starts,
	/// and ignores both packets afterwards. EnderPearl switches backends without a re-login — that is the
	/// point of it — so whatever the client is told at login is what it will still believe on the fourth
	/// backend it visits. Sending it the union of every backend the proxy knows about is what makes the
	/// seamless switch render correctly, and <see cref="ItemPaletteMapping"/> renumbers ids per backend
	/// where the codec allows.</para>
	///
	/// <para>The union keeps the joining backend's own ids unchanged and appends foreign items above them,
	/// so the common case — one backend, or several with identical content — is an identity mapping that
	/// costs nothing.</para>
	/// </summary>
	public sealed class CrossBackendPalette
	{
		/// <summary>
		/// Item network ids are written as a signed 16-bit little-endian short, so the union cannot
		/// number past this and stay decodable.
		/// </summary>
		private const int MAX_ITEM_RUNTIME_ID = short.MaxValue;

		private readonly BackendPaletteStore store;
		private List<ItemData>? clientItems;
		private CompoundTag? clientEntityIdentifiers;
		private readonly HashSet<string> sentEntityPropertyTypes = new(StringComparer.Ordinal);
		private readonly HashSet<string> reportedMissingItems = new(StringComparer.Ordinal);

		public CrossBackendPalette(BackendPaletteStore? store)
		{
			this.store = store ?? BackendPaletteStore.Disabled();
		}

		public bool IsEnabled()
		{
			return store.IsEnabled();
		}

		/// <summary>True once the client has been sent its item registry; after this it cannot be changed.</summary>
		public bool HasClientItems()
		{
			return clientItems != null;
		}

		public BackendPaletteStore Store => store;

		/// <summary>
		/// Builds the item registry to send this client: <paramref name="backendItems"/> as they are,
		/// plus every item any other known backend has that is missing from them.
		/// </summary>
		public List<ItemData> BuildClientItems(string backendName, List<ItemData> backendItems)
		{
			Dictionary<string, ItemData> union = new(StringComparer.Ordinal);
			int nextRuntimeId = 0;
			foreach (ItemData item in backendItems)
			{
				union[item.ItemName] = item;
				nextRuntimeId = Math.Max(nextRuntimeId, item.ItemId);
			}

			int added = 0;
			int skipped = 0;
			foreach (BackendPalette other in store.OtherPalettes(backendName))
			{
				foreach (ItemData item in other.Items)
				{
					if (union.ContainsKey(item.ItemName))
					{
						continue;
					}
					if (nextRuntimeId >= MAX_ITEM_RUNTIME_ID)
					{
						skipped++;
						continue;
					}
					nextRuntimeId++;
					union[item.ItemName] = WithRuntimeId(item, (short)nextRuntimeId);
					added++;
				}
			}
			if (skipped > 0 && store.FirstReportOf("overflow:" + backendName + ":" + skipped))
			{
				Logger.Info(
					"WARNING: the combined item registry of all backends does not fit in Bedrock's 16-bit "
					+ $"item ids; {skipped} item(s) were left out and will render wrong away from their own backend.");
			}
			if (added > 0 && store.FirstReportOf("items:" + backendName + ":" + added + "/" + union.Count))
			{
				Logger.Info(
					$"Extended the item registry for a client joining {backendName} with {added} item(s) from other backends "
					+ $"({union.Count} total), so switching backends keeps their textures.");
			}
			clientItems = new List<ItemData>(union.Values);
			return clientItems;
		}

		private static ItemData WithRuntimeId(ItemData source, short runtimeId)
		{
			return new ItemData
			{
				ItemName = source.ItemName,
				ItemId = runtimeId,
				IsComponentBased = source.IsComponentBased,
				ItemVersion = source.ItemVersion,
				ItemComponentData = source.ItemComponentData,
			};
		}

		/// <summary>
		/// Applies the block half of the palette to a backend's StartGame before it reaches the client:
		/// learns this backend's custom blocks, replaces the list with the union of every backend's, and
		/// clears the block registry checksum when — and only when — that union added something.
		///
		/// <para>The checksum is why this has to be one operation. StartGame carries a checksum over the
		/// server's block registry and the client verifies its own palette against it; a client given a
		/// deliberately larger palette than the backend described cannot match it and disconnects with
		/// BlockMismatch before a single chunk renders. Zero is the documented opt-out. Left intact when
		/// nothing was added, so a genuinely corrupt palette is still caught on an ordinary join.</para>
		///
		/// <para>Everything here assumes hashed ids; for a backend without them the union would renumber
		/// the world out from under the client. Such a backend's StartGame is left exactly as it sent
		/// it, and its blocks stay out of the shared store — an index means nothing on any other
		/// backend.</para>
		/// </summary>
		/// <returns>true when the packet was changed</returns>
		public bool ApplyToStartGame(string backendName, global::Protocol.Packets.StartGamePacket startGame)
		{
			List<ServerBlockProperty> backendBlocks = new(startGame.BlockProperties);
			// Learned before anything else, and for every backend rather than only the shareable ones:
			// this is what the switcher reads to decide whether a player can be handed over seamlessly.
			store.LearnBlockIdsHashed(backendName, startGame.BlockNetworkIdsAreHashes);
			WarnIfBlockIdsNotHashed(backendName, startGame.BlockNetworkIdsAreHashes);

			if (!startGame.BlockNetworkIdsAreHashes)
			{
				return false;
			}

			store.LearnBlockProperties(backendName, backendBlocks);

			List<ServerBlockProperty> union = BuildClientBlockProperties(backendName, backendBlocks);
			if (union.Count == backendBlocks.Count)
			{
				return false;
			}
			startGame.BlockProperties.Clear();
			startGame.BlockProperties.AddRange(union);
			startGame.ServerBlockTypeRegistryChecksum = 0UL;
			return true;
		}

		public List<ServerBlockProperty> BuildClientBlockProperties(string backendName, List<ServerBlockProperty> backendBlocks)
		{
			Dictionary<string, ServerBlockProperty> union = new(StringComparer.Ordinal);
			foreach (ServerBlockProperty block in backendBlocks)
			{
				union[block.BlockName] = block;
			}
			int before = union.Count;
			foreach (BackendPalette other in store.OtherPalettes(backendName))
			{
				foreach (ServerBlockProperty block in other.BlockProperties)
				{
					if (!union.ContainsKey(block.BlockName))
					{
						union[block.BlockName] = block;
					}
				}
			}
			int added = union.Count - before;
			if (added > 0 && store.FirstReportOf("blocks:" + backendName + ":" + added))
			{
				Logger.Info(
					$"Extended the block registry for a client joining {backendName} with {added} custom block(s) from other "
					+ "backends, so they render after a switch.");
			}
			return new List<ServerBlockProperty>(union.Values);
		}

		/// <summary>
		/// Custom blocks can only be shared between backends while their ids are hashed from the block
		/// state. A backend numbering them by palette order gives the same block a different id on every
		/// world, and nothing the proxy can do at login fixes that.
		/// </summary>
		public void WarnIfBlockIdsNotHashed(string backendName, bool blockNetworkIdsHashed)
		{
			if (blockNetworkIdsHashed || !store.FirstReportOf("unhashedBlocks:" + backendName))
			{
				return;
			}
			Logger.Info(
				$"WARNING: backend {backendName} numbers block ids by palette order rather than hashing them. Its custom "
				+ "blocks will render as the wrong block for players who arrived from another backend, "
				+ "and the proxy cannot correct it.");
		}

		/// <summary>The entity identifier list to send this client: the joining backend's, plus every other one's.</summary>
		public CompoundTag BuildClientEntityIdentifiers(string backendName, CompoundTag backendIdentifiers)
		{
			CompoundTag merged = backendIdentifiers;
			int before = EntityPalettes.IdList(backendIdentifiers).Count;
			foreach (BackendPalette other in store.OtherPalettes(backendName))
			{
				merged = EntityPalettes.MergeIdentifiers(merged, other.EntityIdentifiers);
			}
			int added = EntityPalettes.IdList(merged).Count - before;
			if (added > 0 && store.FirstReportOf("entities:" + backendName + ":" + added))
			{
				Logger.Info(
					$"Extended the entity list for a client joining {backendName} with {added} entity type(s) from other "
					+ "backends, so they stay visible after a switch.");
			}
			clientEntityIdentifiers = merged;
			return merged;
		}

		public CompoundTag? ClientEntityIdentifiers => clientEntityIdentifiers;

		/// <summary>Remembers an entity property list already sent to the client; returns false if it is a repeat.</summary>
		public bool MarkEntityPropertySent(CompoundTag property)
		{
			return sentEntityPropertyTypes.Add(EntityPalettes.EntityPropertyType(property));
		}

		/// <summary>The entity property lists from other backends that this client has not been sent yet.</summary>
		public List<CompoundTag> PendingEntityProperties(string backendName)
		{
			List<CompoundTag> pending = new();
			foreach (BackendPalette other in store.OtherPalettes(backendName))
			{
				foreach (CompoundTag property in other.EntityProperties)
				{
					if (sentEntityPropertyTypes.Add(EntityPalettes.EntityPropertyType(property)))
					{
						pending.Add(property);
					}
				}
			}
			return pending;
		}

		/// <summary>
		/// The mapping to install for <paramref name="backendName"/>, or null when the client has no
		/// registry yet. Reports items this backend has that the client's registry does not — once per
		/// session per item.
		/// </summary>
		public ItemPaletteMapping? MappingFor(string backendName, List<ItemData>? backendItems)
		{
			if (clientItems == null || backendItems == null || backendItems.Count == 0)
			{
				return null;
			}
			ItemPaletteMapping mapping = ItemPaletteMapping.Between(backendItems, clientItems);
			List<string> missing = new();
			foreach (string identifier in mapping.UnmappedFromBackend)
			{
				if (reportedMissingItems.Add(identifier))
				{
					missing.Add(identifier);
				}
			}
			if (missing.Count > 0)
			{
				Logger.Info(
					$"WARNING: backend {backendName} has {missing.Count} item(s) the player's client does not know about, because they "
					+ "were not in any backend's cached registry when the player logged in: "
					+ string.Join(", ", missing.GetRange(0, Math.Min(missing.Count, 10)))
					+ ". They will show the wrong texture until the player rejoins; everyone who joins from now on gets them.");
			}
			return mapping;
		}
	}
}
