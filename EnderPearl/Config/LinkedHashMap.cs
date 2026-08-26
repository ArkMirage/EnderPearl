using System;
using System.Collections.Generic;

namespace EnderPearl.Config
{
	/// <summary>A dictionary that preserves insertion order, like Java's LinkedHashMap.</summary>
	public sealed class LinkedHashMap<TKey, TValue> : IEnumerable<KeyValuePair<TKey, TValue>> where TKey : notnull
	{
		private readonly Dictionary<TKey, LinkedListNode<KeyValuePair<TKey, TValue>>> byKey = new();
		private readonly LinkedList<KeyValuePair<TKey, TValue>> order = new();

		public int Count => order.Count;

		public IEnumerable<TKey> Keys
		{
			get
			{
				foreach (var pair in order)
				{
					yield return pair.Key;
				}
			}
		}

		public IEnumerable<TValue> Values
		{
			get
			{
				foreach (var pair in order)
				{
					yield return pair.Value;
				}
			}
		}

		public TValue this[TKey key]
		{
			get => TryGetValue(key, out TValue? value) ? value : throw new KeyNotFoundException(key.ToString());
			set => Add(key, value);
		}

		public void Add(TKey key, TValue value)
		{
			if (byKey.TryGetValue(key, out var node))
			{
				node.Value = new KeyValuePair<TKey, TValue>(key, value);
				return;
			}
			node = order.AddLast(new KeyValuePair<TKey, TValue>(key, value));
			byKey[key] = node;
		}

		public bool Remove(TKey key)
		{
			if (!byKey.TryGetValue(key, out var node))
			{
				return false;
			}
			order.Remove(node);
			byKey.Remove(key);
			return true;
		}

		public bool TryGetValue(TKey key, [System.Diagnostics.CodeAnalysis.MaybeNullWhen(false)] out TValue value)
		{
			if (byKey.TryGetValue(key, out var node))
			{
				value = node.Value.Value;
				return true;
			}
			value = default;
			return false;
		}

		public bool ContainsKey(TKey key) => byKey.ContainsKey(key);

		public void Clear()
		{
			order.Clear();
			byKey.Clear();
		}

		public IEnumerator<KeyValuePair<TKey, TValue>> GetEnumerator() => order.GetEnumerator();

		System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
	}
}
