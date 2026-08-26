using System.Collections.Generic;
using Protocol.Types;

namespace EnderPearl.Palette
{
	/// <summary>
	/// Typed accessors and structural equality over <see cref="CompoundTag"/>/<see cref="ListTag"/> NBT,
	/// standing in for cloudburst's <c>NbtMap</c> API (<c>getString/getInt/getList/...</c> and its
	/// value-based <c>equals</c>). The generated library's tags are plain mutable classes compared by
	/// reference, which is wrong for every palette lookup this package performs.
	/// </summary>
	public static class Nbt
	{
		public static string GetString(this CompoundTag tag, string key, string fallback = "")
		{
			if (tag != null && tag.Value.TryGetValue(key, out TagVariant? variant)
				&& variant.Type == TagType.String
				&& variant.Value.TryPickT8(out StringTag? str))
			{
				return str.Data ?? fallback;
			}
			return fallback;
		}

		public static int GetInt(this CompoundTag tag, string key, int fallback = 0)
		{
			if (tag != null && tag.Value.TryGetValue(key, out TagVariant? variant)
				&& variant.Type == TagType.Int
				&& variant.Value.TryPickT3(out IntTag? intTag))
			{
				return intTag.Data;
			}
			return fallback;
		}

		public static bool GetBoolean(this CompoundTag tag, string key, bool fallback = false)
		{
			if (tag != null && tag.Value.TryGetValue(key, out TagVariant? variant)
				&& variant.Type == TagType.Byte
				&& variant.Value.TryPickT1(out ByteTag? byteTag))
			{
				return byteTag.Data != 0;
			}
			return fallback;
		}

		public static CompoundTag GetCompound(this CompoundTag tag, string key)
		{
			if (tag != null && tag.Value.TryGetValue(key, out TagVariant? variant)
				&& variant.Type == TagType.Compound
				&& variant.Value.TryPickT10(out CompoundTag? compound))
			{
				return compound;
			}
			return new CompoundTag();
		}

		/// <summary>The compounds of a list-of-compound tag, or an empty list when absent or mistyped.</summary>
		public static List<CompoundTag> GetCompoundList(this CompoundTag tag, string key)
		{
			List<CompoundTag> result = new();
			if (tag != null && tag.Value.TryGetValue(key, out TagVariant? variant)
				&& variant.Type == TagType.List
				&& variant.Value.TryPickT9(out ListTag? list))
			{
				foreach (TagVariant element in list.Value)
				{
					if (element.Type == TagType.Compound && element.Value.TryPickT10(out CompoundTag? compound))
					{
						result.Add(compound);
					}
				}
			}
			return result;
		}

		public static void PutString(this CompoundTag tag, string key, string value)
		{
			tag.Value[key] = new TagVariant { Value = new StringTag { Data = value } };
		}

		public static void PutInt(this CompoundTag tag, string key, int value)
		{
			tag.Value[key] = new TagVariant { Value = new IntTag { Data = value } };
		}

		public static void PutBoolean(this CompoundTag tag, string key, bool value)
		{
			tag.Value[key] = new TagVariant { Value = new ByteTag { Data = value ? (byte)1 : (byte)0 } };
		}

		public static void PutCompound(this CompoundTag tag, string key, CompoundTag value)
		{
			tag.Value[key] = new TagVariant { Value = value };
		}

		public static void PutCompoundList(this CompoundTag tag, string key, List<CompoundTag> value)
		{
			ListTag list = new() { Type = TagType.Compound };
			foreach (CompoundTag compound in value)
			{
				list.Value.Add(new TagVariant { Value = compound });
			}
			tag.Value[key] = new TagVariant { Value = list };
		}

		/// <summary>A shallow copy with a fresh dictionary, the counterpart of NbtMap.toBuilder().build().</summary>
		public static CompoundTag Copy(this CompoundTag tag)
		{
			CompoundTag copy = new();
			foreach (KeyValuePair<string, TagVariant> pair in tag.Value)
			{
				copy.Value[pair.Key] = pair.Value;
			}
			return copy;
		}

		/// <summary>NbtMap.isEmpty(): no entries at all.</summary>
		public static bool IsEmpty(this CompoundTag tag)
		{
			return tag == null || tag.Value.Count == 0;
		}

		public static bool NbtEquals(this CompoundTag? a, CompoundTag? b)
		{
			if (ReferenceEquals(a, b))
			{
				return true;
			}
			if (a == null || b == null || a.Value.Count != b.Value.Count)
			{
				return false;
			}
			foreach (KeyValuePair<string, TagVariant> pair in a.Value)
			{
				if (!b.Value.TryGetValue(pair.Key, out TagVariant? other)
					|| pair.Value.Type != other.Type
					|| !TagEquals(pair.Value, other))
				{
					return false;
				}
			}
			return true;
		}

		private static bool TagEquals(TagVariant a, TagVariant b)
		{
			return a.Type switch
			{
				TagType.Byte => a.Value.AsT1.Data == b.Value.AsT1.Data,
				TagType.Short => a.Value.AsT2.Data == b.Value.AsT2.Data,
				TagType.Int => a.Value.AsT3.Data == b.Value.AsT3.Data,
				TagType.Long => a.Value.AsT4.Data == b.Value.AsT4.Data,
				TagType.Float => a.Value.AsT5.Data.Equals(b.Value.AsT5.Data),
				TagType.Double => a.Value.AsT6.Data.Equals(b.Value.AsT6.Data),
				TagType.String => string.Equals(a.Value.AsT8.Data, b.Value.AsT8.Data, System.StringComparison.Ordinal),
				TagType.List => ListEquals(a.Value.AsT9, b.Value.AsT9),
				TagType.Compound => a.Value.AsT10.NbtEquals(b.Value.AsT10),
				_ => true,
			};
		}

		private static bool ListEquals(ListTag a, ListTag b)
		{
			if (a.Type != b.Type || a.Value.Count != b.Value.Count)
			{
				return false;
			}
			for (int i = 0; i < a.Value.Count; i++)
			{
				if (a.Value[i].Type != b.Value[i].Type || !TagEquals(a.Value[i], b.Value[i]))
				{
					return false;
				}
			}
			return true;
		}
	}
}
