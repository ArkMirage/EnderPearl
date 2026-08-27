using System.Collections.Concurrent;

namespace EnderPearl.Backend
{
	/// <summary>
	/// What each backend declared about its own block-id scheme at its most recent StartGame.
	///
	/// <para>All that remains of the former cross-backend palette store: <see
	/// cref="BackendConnector.NeedsReconnectToReach"/> still has to steer players around mixed
	/// hashing/indexing schemes, and that only ever needed this one fact per backend.</para>
	/// </summary>
	public static class BackendBlockSchemes
	{
		private static readonly ConcurrentDictionary<string, bool> HashedByBackend = new(StringComparer.Ordinal);

		public static void Remember(string backendName, bool blockIdsAreHashes)
		{
			HashedByBackend[backendName] = blockIdsAreHashes;
		}

		/// <summary>Null while the backend has never been seen - "never visited" stays distinguishable from "does not hash".</summary>
		public static bool? IsHashed(string backendName)
		{
			return HashedByBackend.TryGetValue(backendName, out bool hashed) ? hashed : null;
		}
	}
}
