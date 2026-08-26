using System;
using System.Collections.Generic;
using Protocol.Types;

namespace EnderPearl.Palette
{
	/// <summary>
	/// Translates item network ids between one backend's registry and the union registry the client was
	/// given at login.
	///
	/// <para>In the Java original the translation rides on the codec's decode step: each session is given
	/// an intentionally lopsided <c>DefinitionRegistry</c> (<see cref="BackendSide"/> on the backend,
	/// <see cref="ClientSide"/> on the client) so re-encoding emits the other side's ids. This build's
	/// protocol library has no per-session definition layer to install into — item network ids pass
	/// through raw — so the registries are computed exactly as Java computes them and handed to
	/// <see cref="Codec.CodecDefinitionState.InstallItemMapping"/>, which is a documented no-op here.
	/// The mapping itself still decides identity and reports unmapped items.</para>
	///
	/// <para>An id with no counterpart passes through unchanged rather than becoming air: the item is real
	/// on the side that sent it, and a wrong texture is a far smaller failure than a slot the two ends
	/// disagree about. <see cref="UnmappedFromBackend"/> counts those so the cause can be reported once.</para>
	/// </summary>
	public sealed class ItemPaletteMapping
	{
		private readonly IItemDefinitionRegistry backendSide;
		private readonly IItemDefinitionRegistry clientSide;
		private readonly List<string> itemsMissingFromClient;
		private readonly bool identity;

		private ItemPaletteMapping(
			IItemDefinitionRegistry backendSide,
			IItemDefinitionRegistry clientSide,
			List<string> itemsMissingFromClient,
			bool identity)
		{
			this.backendSide = backendSide;
			this.clientSide = clientSide;
			this.itemsMissingFromClient = itemsMissingFromClient;
			this.identity = identity;
		}

		/// <param name="backendItems">the backend's own registry, as it sent it</param>
		/// <param name="clientItems">the union registry the client was given at login</param>
		public static ItemPaletteMapping Between(List<ItemData> backendItems, List<ItemData> clientItems)
		{
			Dictionary<int, ItemData> toClient = new();
			Dictionary<int, ItemData> toBackend = new();
			Dictionary<string, ItemData> toClientByName = new(StringComparer.Ordinal);
			Dictionary<string, ItemData> toBackendByName = new(StringComparer.Ordinal);
			Dictionary<string, ItemData> clientByIdentifier = new(StringComparer.Ordinal);
			foreach (ItemData clientItem in clientItems)
			{
				clientByIdentifier[clientItem.ItemName] = clientItem;
			}

			List<string> missing = new();
			bool identity = true;
			foreach (ItemData backendItem in backendItems)
			{
				if (!clientByIdentifier.TryGetValue(backendItem.ItemName, out ItemData? clientItem))
				{
					// The client's registry predates this backend learning the item: it was not in the
					// union when this player logged in. Nothing can be done for them until they rejoin.
					missing.Add(backendItem.ItemName);
					continue;
				}
				if (clientItem.ItemId != backendItem.ItemId)
				{
					identity = false;
				}
				ItemData clientNumbered = Rebrand(backendItem, clientItem.ItemId);
				ItemData backendNumbered = Rebrand(clientItem, backendItem.ItemId);
				toClient[backendItem.ItemId] = clientNumbered;
				toBackend[clientItem.ItemId] = backendNumbered;
				// Recipes name their ingredients rather than numbering them, so both sides need the same
				// translation reachable by identifier.
				toClientByName[backendItem.ItemName] = clientNumbered;
				toBackendByName[clientItem.ItemName] = backendNumbered;
			}

			return new ItemPaletteMapping(
				new MappedRegistry(toClient, toClientByName),
				new MappedRegistry(toBackend, toBackendByName),
				missing,
				identity && missing.Count == 0
			);
		}

		private static ItemData Rebrand(ItemData source, short runtimeId)
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

		/// <summary>Install on the backend session: backend id in, client id out.</summary>
		public IItemDefinitionRegistry BackendSide => backendSide;

		/// <summary>Install on the client session: client id in, backend id out.</summary>
		public IItemDefinitionRegistry ClientSide => clientSide;

		/// <summary>Items this backend has that the client's registry does not, and so cannot render correctly.</summary>
		public List<string> UnmappedFromBackend => itemsMissingFromClient;

		/// <summary>True when every id already agrees, so installing this mapping would change nothing.</summary>
		public bool IsIdentity => identity;

		/// <summary>Mirror of cloudburst's DefinitionRegistry&lt;ItemDefinition&gt;, for a future codec swap.</summary>
		public interface IItemDefinitionRegistry
		{
			ItemData GetDefinition(int runtimeId);

			ItemData GetDefinition(string identifier);

			bool IsRegistered(ItemData definition);
		}

		private sealed class PassthroughDefinition : ItemData
		{
			internal static ItemData Of(string identifier, int runtimeId)
			{
				return new ItemData { ItemName = identifier, ItemId = (short)runtimeId, IsComponentBased = false };
			}
		}

		private sealed class MappedRegistry : IItemDefinitionRegistry
		{
			private readonly Dictionary<int, ItemData> byRuntimeId;
			private readonly Dictionary<string, ItemData> byIdentifier;

			public MappedRegistry(Dictionary<int, ItemData> byRuntimeId, Dictionary<string, ItemData> byIdentifier)
			{
				this.byRuntimeId = byRuntimeId;
				this.byIdentifier = byIdentifier;
			}

			public ItemData GetDefinition(int runtimeId)
			{
				if (runtimeId == 0)
				{
					return Air;
				}
				if (byRuntimeId.TryGetValue(runtimeId, out ItemData? mapped))
				{
					return mapped;
				}
				return PassthroughDefinition.Of("minecraft:unmapped_" + runtimeId, runtimeId);
			}

			/// <summary>
			/// Recipes reference items by name. Never null and never throwing: the encoder writes the
			/// identifier straight back out, so an unknown name has to survive as itself or the whole
			/// CraftingData packet fails to re-encode.
			/// </summary>
			public ItemData GetDefinition(string identifier)
			{
				if (identifier != null && byIdentifier.TryGetValue(identifier, out ItemData? mapped))
				{
					return mapped;
				}
				return PassthroughDefinition.Of(identifier ?? "", 0);
			}

			public bool IsRegistered(ItemData definition)
			{
				return definition != null;
			}
		}

		/// <summary>The shared air definition every registry resolves id 0 to.</summary>
		internal static ItemData Air { get; } = new ItemData { ItemName = "minecraft:air", ItemId = 0, IsComponentBased = false };
	}
}
