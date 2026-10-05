using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;

namespace EnderPearl.Config;

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
	public bool Enabled { get; init; }

	private IReadOnlyList<string> fallbacks = new List<string>();

	/// <summary>The global try-list, normalized (trimmed, de-duplicated) on assignment.</summary>
	public IEnumerable<string> Fallbacks { get => fallbacks; init => fallbacks = ConfigValues.NormalizedList(value).AsReadOnly(); }

	private IReadOnlyDictionary<string, List<string>> backendFallbacks = new Dictionary<string, List<string>>();

	/// <summary>Per-backend overrides, keyed by normalized backend name; an explicitly empty list turns failover off.</summary>
	public IReadOnlyDictionary<string, List<string>> BackendFallbacks
	{
		get => backendFallbacks;
		init => backendFallbacks = NormalizeOverrides(value);
	}

	public BackendDisconnectAction OnBackendDisconnect { get; init; } = BackendDisconnectAction.AUTO;

	private static IReadOnlyDictionary<string, List<string>> NormalizeOverrides(IReadOnlyDictionary<string, List<string>> overrides)
	{
		var normalized = new LinkedHashMap<string, List<string>>();
		foreach (KeyValuePair<string, List<string>> entry in overrides)
		{
			normalized.Add(ConfigValues.Normalize(entry.Key), ConfigValues.NormalizedList(entry.Value));
		}
		var asDictionary = new Dictionary<string, List<string>>();
		foreach (KeyValuePair<string, List<string>> entry in normalized)
		{
			asDictionary[entry.Key] = entry.Value;
		}
		return asDictionary;
	}

	/// <summary>Keeps the many callers that predate the later components on their defaults.</summary>
	public static FailoverConfig Disabled()
	{
		return new FailoverConfig
		{
			Enabled = false,
			Fallbacks = new List<string>(),
			BackendFallbacks = new Dictionary<string, List<string>>(),
			OnBackendDisconnect = BackendDisconnectAction.AUTO
		};
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
	/// <p>A backend that disconnects a player has made a decision about that player - a ban, a whitelist,
	/// a moderation action - which is why <c>onBackendDisconnect</c> exists; see
	/// <see cref="BackendDisconnectAction"/>.</p>
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
		return new FailoverConfig
		{
			Enabled = enabled,
			Fallbacks = fallbacks,
			BackendFallbacks = backendFallbacks,
			OnBackendDisconnect = BackendDisconnectActions.Parse(config.GetString("failover.onBackendDisconnect"))
		};
	}

	/// <summary>The <c>"failover"</c> section of the generated default configuration.</summary>
	public static JsonObject DefaultSection()
	{
		return new JsonObject
		{
			["enabled"] = true,
			["fallbacks"] = new JsonArray(BackendConfig.DEFAULT_NAME),
			["onBackendDisconnect"] = BackendDisconnectAction.AUTO.ToString().ToLowerInvariant()
		};
	}
}