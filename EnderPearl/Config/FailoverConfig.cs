using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;

namespace EnderPearl.Config
{
	/// <summary>
	/// Where a player is sent when the backend they are on goes away.
	///
	/// <p>Velocity-style: an ordered global try-list, overridable per backend. A per-backend entry always
	/// wins over the global list, <em>including when it is configured empty</em> - that means "never fail
	/// over from this backend, disconnect the player instead". A backend with no entry of its own uses
	/// the global list.</p>
	/// </summary>
	public sealed class FailoverConfig
	{
		public bool Enabled { get; }

		public IReadOnlyList<string> Fallbacks { get; }

		public IReadOnlyDictionary<string, List<string>> BackendFallbacks { get; }

		public ProtocolFaultPolicy ProtocolFault { get; }

		public BackendKickAction OnBackendKick { get; }

		public FailoverConfig(
			bool enabled,
			IEnumerable<string> fallbacks,
			IDictionary<string, List<string>> backendFallbacks,
			ProtocolFaultPolicy? protocolFault = null,
			BackendKickAction onBackendKick = BackendKickAction.AUTO)
		{
			if (fallbacks == null)
			{
				throw new ArgumentNullException(nameof(fallbacks));
			}
			if (backendFallbacks == null)
			{
				throw new ArgumentNullException(nameof(backendFallbacks));
			}
			Enabled = enabled;
			Fallbacks = ConfigValues.NormalizedList(fallbacks).AsReadOnly();

			var normalizedOverrides = new LinkedHashMap<string, List<string>>();
			foreach (KeyValuePair<string, List<string>> entry in backendFallbacks)
			{
				normalizedOverrides.Add(ConfigValues.Normalize(entry.Key), ConfigValues.NormalizedList(entry.Value));
			}
			var asDictionary = new Dictionary<string, List<string>>();
			foreach (KeyValuePair<string, List<string>> entry in normalizedOverrides)
			{
				asDictionary[entry.Key] = entry.Value;
			}
			BackendFallbacks = asDictionary;

			ProtocolFault = protocolFault ?? ProtocolFaultPolicy.Defaults();
			OnBackendKick = onBackendKick;
		}

		/// <summary>Keeps the many callers that predate the later components on their defaults.</summary>
		public static FailoverConfig Disabled()
		{
			return new FailoverConfig(
				false,
				new List<string>(),
				new Dictionary<string, List<string>>(),
				ProtocolFaultPolicy.Defaults(),
				BackendKickAction.AUTO);
		}

		/// <summary>
		/// The ordered backend names to try for a player who has just lost <c>backendName</c>, already
		/// filtered so a player is never sent straight back to the backend that just died.
		/// </summary>
		public List<string> FallbacksFor(string? backendName)
		{
			if (!Enabled)
			{
				return new List<string>();
			}
			string lost = ConfigValues.Normalize(backendName);
			List<string> chain = BackendFallbacks.TryGetValue(lost, out List<string>? overrideChain)
				? overrideChain
				: new List<string>(Fallbacks);
			var filtered = new List<string>();
			foreach (string name in chain)
			{
				if (!name.Equals(lost, StringComparison.Ordinal))
				{
					filtered.Add(name);
				}
			}
			return filtered;
		}

		// ------------------------------------------------------------------ config

		/// <summary>
		/// Reads the <c>"failover"</c> section plus each backend's own <c>"fallback"</c> list.
		///
		/// <p>Targets default to the hub backend, so a proxy that has never heard of <c>"failover"</c>
		/// still returns players to the hub instead of kicking them when their backend dies. Has rather
		/// than a plain read on the global list and per-backend entries alike, because an explicitly
		/// empty list is meaningful: it turns failover off.</p>
		///
		/// <p>A backend that kicks a player has made a decision about that player - a ban, a whitelist,
		/// a moderation action - which is why <c>onBackendKick</c> exists; see <see cref="BackendKickAction"/>.</p>
		/// </summary>
		public static FailoverConfig From(JsonConfig config, string hubBackendName)
		{
			bool enabled = config.GetBool("failover.enabled", true);
			List<string> fallbacks = config.Has("failover.fallbacks")
				? ConfigValues.NormalizedList(config.GetStringList("failover.fallbacks"))
				: new List<string> { hubBackendName };

			var backendFallbacks = new Dictionary<string, List<string>>();
			foreach (KeyValuePair<string, JsonConfig> entry in config.Members("backends"))
			{
				if (entry.Value.Has("fallback"))
				{
					backendFallbacks[entry.Key] =
						ConfigValues.NormalizedList(entry.Value.GetStringList("fallback"));
				}
			}
			return new FailoverConfig(
				enabled,
				fallbacks,
				backendFallbacks,
				ProtocolFaultPolicy.From(config),
				BackendKickActions.Parse(config.GetString("failover.onBackendKick"))
			);
		}

		/// <summary>The <c>"failover"</c> section of the generated default configuration.</summary>
		public static JsonObject DefaultSection()
		{
			return new JsonObject
			{
				["enabled"] = true,
				["fallbacks"] = new JsonArray(BackendConfig.DEFAULT_NAME),
				["onBackendKick"] = BackendKickAction.AUTO.ToString().ToLowerInvariant()
			};
		}
	}
}
