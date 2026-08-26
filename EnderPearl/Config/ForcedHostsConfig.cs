using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using EnderPearl.Logging;

namespace EnderPearl.Config
{
	/// <summary>
	/// Routes a joining player to a backend based on the address they typed into their server list.
	///
	/// <p>Configured as a hostname-to-backend map, so hostnames containing dots need no escaping:</p>
	///
	/// <pre>
	/// "forcedHosts": {
	///   "play.example.com": "survival",
	///   "creative.example.com": "creative"
	/// }
	/// </pre>
	///
	/// <p><b>Not a security boundary.</b> The hostname arrives in the client's <c>ServerAddress</c>
	/// claim, which is signed by the client's own key rather than Mojang's - anyone can edit it and
	/// arrive claiming any hostname. Forced hosts decide which door a player walks through by default;
	/// whether they are allowed through it is <see cref="PermissionsConfig"/>'s job, and a backend that must
	/// stay staff-only needs to enforce that itself.</p>
	///
	/// <p>An unknown hostname is not an error: the player goes to the default backend, exactly as if no
	/// forced host were configured.</p>
	/// </summary>
	public sealed class ForcedHostsConfig
	{
		public IReadOnlyDictionary<string, string> ByHostname { get; }

		public ForcedHostsConfig(IDictionary<string, string>? byHostname)
		{
			var normalized = new LinkedHashMap<string, string>();
			if (byHostname != null)
			{
				foreach (KeyValuePair<string, string> entry in byHostname)
				{
					string hostname = NormalizeHostname(entry.Key);
					string backend = ConfigValues.Normalize(entry.Value);
					if (hostname.Length > 0 && backend.Length > 0)
					{
						normalized.Add(hostname, backend);
					}
				}
			}
			var asDictionary = new Dictionary<string, string>();
			foreach (KeyValuePair<string, string> entry in normalized)
			{
				asDictionary[entry.Key] = entry.Value;
			}
			ByHostname = asDictionary;
		}

		public static ForcedHostsConfig Empty() => new(new Dictionary<string, string>());

		public static ForcedHostsConfig From(JsonConfig config, LinkedHashMap<string, BackendConfig> backends)
		{
			var byHostname = new Dictionary<string, string>();
			foreach (KeyValuePair<string, JsonConfig> entry in config.Members("forcedHosts"))
			{
				byHostname[entry.Key] = entry.Value.SelfString() ?? "";
			}
			// A forced host pointing at a backend that does not exist is a configuration mistake, so it
			// is dropped loudly at load rather than looked up per join.
			var known = new Dictionary<string, string>();
			foreach (KeyValuePair<string, string> entry in new ForcedHostsConfig(byHostname).ByHostname)
			{
				if (backends.ContainsKey(ConfigValues.Normalize(entry.Value)))
				{
					known[entry.Key] = entry.Value;
				}
				else
				{
					Logger.Info(
						$"WARNING: ignoring forcedHosts[\"{entry.Key}\"]=\"{entry.Value}\" because no backend named '{entry.Value}' is configured.");
				}
			}
			return new ForcedHostsConfig(known);
		}

		/// <summary>The empty <c>"forcedHosts"</c> object of the generated default configuration.</summary>
		public static JsonObject DefaultSection() => new JsonObject();

		public bool IsEmpty() => ByHostname.Count == 0;

		/// <summary>The backend name configured for the address a client connected with, if any.</summary>
		public bool TryBackendFor(string? serverAddress, out string? backendName)
		{
			string hostname = NormalizeHostname(HostPart(serverAddress));
			if (hostname.Length == 0)
			{
				backendName = null;
				return false;
			}
			return ByHostname.TryGetValue(hostname, out backendName);
		}

		/// <summary>Strips the port, leaving an IPv6 literal's colons alone.</summary>
		private static string HostPart(string? serverAddress)
		{
			if (serverAddress == null)
			{
				return "";
			}
			string address = serverAddress.Trim();
			if (address.StartsWith("["))
			{
				int end = address.IndexOf(']');
				return end < 0 ? address : address[..(end + 1)];
			}
			int colon = address.IndexOf(':');
			if (colon >= 0 && address.IndexOf(':', colon + 1) < 0)
			{
				return address[..colon];
			}
			return address;
		}

		private static string NormalizeHostname(string? hostname)
		{
			if (hostname == null)
			{
				return "";
			}
			// A fully-qualified name may arrive with the root dot; "PLAY.Example.com." is the same host.
			string normalized = hostname.Trim().ToLowerInvariant();
			while (normalized.EndsWith("."))
			{
				normalized = normalized[..^1];
			}
			return normalized;
		}
	}
}
