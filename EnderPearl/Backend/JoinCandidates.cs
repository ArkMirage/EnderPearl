using System.Collections.Generic;
using EnderPearl.Config;

namespace EnderPearl.Backend
{
	/// <summary>
	/// Expands the join try-list into the flat sequence of attempts a joining player gets.
	///
	/// <p>Pure: the routed backend has to come first even when the try-list does not mention it, a
	/// try-list that repeats it must not give it two turns, and "give up" has to mean nothing more than
	/// "the list ran out".</p>
	/// </summary>
	public static class JoinCandidates
	{
		public static List<BackendConfig> Expand(
			BackendConfig routed,
			JoinConfig join,
			BackendDirectory backendDirectory
		)
		{
			var seen = new ConfigValues.LinkedHashSet<string>();
			var ordered = new List<BackendConfig>();
			// Where the player was actually routed always leads, whether by forced host or by default.
			// The try-list says where to go next, not where to start.
			ordered.Add(routed);
			seen.Add(Normalize(routed.Name));

			foreach (string name in join.TryOrder)
			{
				BackendConfig? backend = backendDirectory.Find(name);
				// An unknown name is a config typo and costs one candidate, never the session.
				if (backend != null && seen.Add(Normalize(backend.Name)))
				{
					ordered.Add(backend);
				}
			}

			var candidates = new List<BackendConfig>();
			foreach (BackendConfig backend in ordered)
			{
				for (int attempt = 0; attempt < join.AttemptsPerBackend; attempt++)
				{
					candidates.Add(backend);
				}
			}
			return candidates;
		}

		private static string Normalize(string? name)
		{
			return name?.Trim().ToLowerInvariant() ?? "";
		}
	}
}
