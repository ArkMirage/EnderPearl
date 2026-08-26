using System;
using System.Collections.Generic;

namespace EnderPearl.Config
{
	/// <summary>
	/// Shared normalization for configured names and lists.
	///
	/// <p>List-valued settings are JSON arrays whose <em>order</em> is meaningful - it is the try
	/// order - while the <em>spelling</em> is not: backend names match case-insensitively everywhere,
	/// and a name repeated in a list is not a second try.</p>
	/// </summary>
	public static class ConfigValues
	{
		/// <summary>Order is meaningful (it is the try order); a name repeated in the config is not a second try.</summary>
		public static List<string> NormalizedList(IEnumerable<string?> raw)
		{
			var unique = new LinkedHashSet<string>();
			foreach (string? name in raw)
			{
				if (!string.IsNullOrWhiteSpace(name))
				{
					unique.Add(Normalize(name));
				}
			}
			return new List<string>(unique);
		}

		public static string Normalize(string? name)
		{
			return name?.Trim().ToLowerInvariant() ?? "";
		}

		/// <summary>An insertion-ordered set, like Java's LinkedHashSet.</summary>
		public sealed class LinkedHashSet<T> : IEnumerable<T> where T : notnull
		{
			private readonly LinkedHashMap<T, byte> map = new();

			public int Count => map.Count;

			public bool Add(T item)
			{
				if (map.ContainsKey(item))
				{
					return false;
				}
				map.Add(item, 0);
				return true;
			}

			public bool Contains(T item) => map.ContainsKey(item);

			public IEnumerator<T> GetEnumerator() => map.Keys.GetEnumerator();

			System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
		}
	}
}
