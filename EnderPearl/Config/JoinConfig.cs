using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;

namespace EnderPearl.Config
{
	/// <summary>
	/// The ordered list of backends to try when a player first joins the proxy, Velocity's <c>try</c>.
	///
	/// <p>Without it a player whose backend is already down is simply kicked - mid-session failover only
	/// covers a backend that dies <em>under</em> someone, because it works by moving a client that is
	/// already in a world.</p>
	/// </summary>
	public sealed class JoinConfig
	{
		public IReadOnlyList<string> TryOrder { get; }

		public int AttemptsPerBackend { get; }

		public JoinConfig(IEnumerable<string>? tryOrder, int attemptsPerBackend)
		{
			if (tryOrder == null)
			{
				throw new ArgumentNullException(nameof(tryOrder));
			}
			if (attemptsPerBackend < 1)
			{
				throw new ArgumentException("attemptsPerBackend must be positive");
			}
			TryOrder = new List<string>(tryOrder);
			AttemptsPerBackend = attemptsPerBackend;
		}

		public static JoinConfig Defaults() => new(Array.Empty<string>(), 1);

		/// <summary>
		/// Reads <c>join.try</c>, defaulting to the failover chain.
		///
		/// <p>Sharing the default is deliberate: "where does a player go when a backend is not available"
		/// is one question, and an operator who has already answered it for a backend dying should not
		/// have to answer it again for a backend that was never up.</p>
		/// </summary>
		public static JoinConfig From(JsonConfig config, FailoverConfig failover)
		{
			List<string> tryOrder = config.Has("join.try")
				? ConfigValues.NormalizedList(config.GetStringList("join.try"))
				: new List<string>(failover.Fallbacks);
			return new JoinConfig(tryOrder, Math.Max(1, config.GetInt("join.attemptsPerBackend", 1)));
		}

		/// <summary>
		/// The <c>"join"</c> section of the generated default configuration. <c>"try"</c> is deliberately
		/// left out: unset it inherits the failover chain, which is the documented default.
		/// </summary>
		public static JsonObject DefaultSection()
		{
			return new JsonObject
			{
				["attemptsPerBackend"] = 1
			};
		}
	}
}
