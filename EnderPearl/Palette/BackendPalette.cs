using System.Collections.Generic;
using Protocol.Types;

namespace EnderPearl.Palette
{
	/// <summary>
	/// What one backend told a client about its content: the item registry it assigns network ids from,
	/// the entity identifiers it can spawn, and the entity property lists that go with them.
	///
	/// <para>All three are read by the client exactly once, at level init, and never again — which is the
	/// whole reason this class exists. A seamless backend switch does not re-run level init, so the
	/// client keeps whatever the <em>first</em> backend sent. Collecting each backend's palette lets the
	/// proxy hand a joining client the union of all of them.</para>
	/// </summary>
	public sealed class BackendPalette
	{
		public string BackendName { get; }

		public List<ItemData> Items { get; }

		/// <summary>The AvailableActorIdentifiers list, or null while this backend has never sent one.</summary>
		public CompoundTag? EntityIdentifiers { get; }

		public List<CompoundTag> EntityProperties { get; }

		/// <summary>Custom block definitions as StartGame carries them; read by the client at level init.</summary>
		public List<ServerBlockProperty> BlockProperties { get; }

		/// <summary>
		/// Whether this backend hashes block network ids, or null while it has never been seen.
		///
		/// <para>The one fact about a backend that decides whether a player can be handed to it seamlessly.
		/// A client reads its block-id scheme from the StartGame it logs in with and never again, so a
		/// session that started on a hashing backend renders nothing on a palette-indexed one, and the
		/// reverse. Persisted because the decision has to be made <em>before</em> the switch — the first
		/// player to move after a restart cannot be the one who discovers it.</para>
		/// </summary>
		public bool? BlockIdsHashed { get; }

		public BackendPalette(
			string backendName,
			List<ItemData>? items,
			CompoundTag? entityIdentifiers,
			List<CompoundTag>? entityProperties,
			List<ServerBlockProperty>? blockProperties,
			bool? blockIdsHashed)
		{
			BackendName = backendName;
			Items = items ?? new List<ItemData>();
			EntityIdentifiers = entityIdentifiers;
			EntityProperties = entityProperties ?? new List<CompoundTag>();
			BlockProperties = blockProperties ?? new List<ServerBlockProperty>();
			BlockIdsHashed = blockIdsHashed;
		}

		public static BackendPalette Empty(string backendName)
		{
			return new BackendPalette(backendName, null, null, null, null, null);
		}

		public BackendPalette WithItems(List<ItemData> items)
		{
			return new BackendPalette(BackendName, items, EntityIdentifiers, EntityProperties, BlockProperties, BlockIdsHashed);
		}

		public BackendPalette WithEntityIdentifiers(CompoundTag entityIdentifiers)
		{
			return new BackendPalette(BackendName, Items, entityIdentifiers, EntityProperties, BlockProperties, BlockIdsHashed);
		}

		public BackendPalette WithBlockIdsHashed(bool blockIdsHashed)
		{
			return new BackendPalette(BackendName, Items, EntityIdentifiers, EntityProperties, BlockProperties, blockIdsHashed);
		}

		public BackendPalette WithBlockProperties(List<ServerBlockProperty> blockProperties)
		{
			return new BackendPalette(BackendName, Items, EntityIdentifiers, EntityProperties, blockProperties, BlockIdsHashed);
		}

		/// <summary>Adds one entity property list, replacing any earlier list for the same entity type.</summary>
		public BackendPalette WithEntityProperty(CompoundTag? property)
		{
			if (property == null)
			{
				return this;
			}
			string type = EntityPalettes.EntityPropertyType(property);
			List<CompoundTag> merged = new(EntityProperties.Count + 1);
			foreach (CompoundTag existing in EntityProperties)
			{
				if (!EntityPalettes.EntityPropertyType(existing).Equals(type))
				{
					merged.Add(existing);
				}
			}
			merged.Add(property);
			return new BackendPalette(BackendName, Items, EntityIdentifiers, merged, BlockProperties, BlockIdsHashed);
		}

		public bool IsEmpty()
		{
			return Items.Count == 0 && EntityIdentifiers == null && EntityProperties.Count == 0
				&& BlockProperties.Count == 0;
		}

		/// <summary>Structural equality against another palette's item registry (name + id per entry).</summary>
		public static bool SameItems(List<ItemData> a, List<ItemData> b)
		{
			if (a.Count != b.Count)
			{
				return false;
			}
			for (int i = 0; i < a.Count; i++)
			{
				ItemData left = a[i];
				ItemData right = b[i];
				if (left.ItemId != right.ItemId
					|| !string.Equals(left.ItemName, right.ItemName, System.StringComparison.Ordinal))
				{
					return false;
				}
			}
			return true;
		}
	}
}
