using System.Collections.Generic;
using Protocol.Types;

namespace EnderPearl.Palette
{
	/// <summary>
	/// Merging for the two entity registries a client reads once at level init.
	///
	/// <para><c>AvailableActorIdentifiersPacket</c> carries <c>idlist</c>, a list of
	/// <c>{id, rid, bid, hasspawnegg, summonable}</c> compounds; an entity whose <c>id</c> is missing
	/// from it has no client-side definition to render and shows up as nothing at all — solid and
	/// clickable, but invisible. <c>SyncActorPropertyPacket</c> carries one <c>{type, properties}</c>
	/// compound per entity type that declares properties.</para>
	///
	/// <para>Entities travel the wire by string identifier, not by <c>rid</c>, so merging two backends'
	/// lists needs no id rewriting on any other packet — only that every <c>rid</c> in the merged list
	/// stays unique.</para>
	/// </summary>
	public static class EntityPalettes
	{
		private const string ID_LIST = "idlist";
		private const string ID = "id";
		private const string RUNTIME_ID = "rid";
		private const string TYPE = "type";

		public static string EntityPropertyType(CompoundTag? property)
		{
			if (property == null)
			{
				return "";
			}
			return property.GetString(TYPE);
		}

		public static List<CompoundTag> IdList(CompoundTag? identifiers)
		{
			if (identifiers == null)
			{
				return new List<CompoundTag>();
			}
			return identifiers.GetCompoundList(ID_LIST);
		}

		public static string EntityId(CompoundTag? entry)
		{
			return entry == null ? "" : entry.GetString(ID);
		}

		/// <summary>
		/// Merges <paramref name="additional"/> into <paramref name="base"/>, keeping base's entries as
		/// they are and giving any newly added entry an unused <c>rid</c>.
		/// </summary>
		/// <returns>the merged identifiers, or <paramref name="base"/> when nothing was added</returns>
		public static CompoundTag MergeIdentifiers(CompoundTag? baseIdentifiers, CompoundTag? additional)
		{
			List<CompoundTag> baseList = IdList(baseIdentifiers);
			List<CompoundTag> additionalList = IdList(additional);
			if (additionalList.Count == 0 || baseIdentifiers == null)
			{
				return baseIdentifiers!;
			}
			if (baseList.Count == 0)
			{
				return additional!;
			}

			HashSet<string> known = new();
			int maxRuntimeId = 0;
			foreach (CompoundTag entry in baseList)
			{
				known.Add(EntityId(entry));
				maxRuntimeId = System.Math.Max(maxRuntimeId, entry.GetInt(RUNTIME_ID));
			}

			List<CompoundTag> merged = new(baseList);
			bool changed = false;
			foreach (CompoundTag entry in additionalList)
			{
				string id = EntityId(entry);
				if (id.Length == 0 || !known.Add(id))
				{
					continue;
				}
				CompoundTag copy = entry.Copy();
				copy.PutInt(RUNTIME_ID, ++maxRuntimeId);
				merged.Add(copy);
				changed = true;
			}
			if (!changed)
			{
				return baseIdentifiers;
			}
			CompoundTag rebuilt = baseIdentifiers.Copy();
			rebuilt.PutCompoundList(ID_LIST, merged);
			return rebuilt;
		}
	}
}
