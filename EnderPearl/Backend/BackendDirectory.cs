using System;
using System.Collections.Generic;
using EnderPearl.Config;

namespace EnderPearl.Backend
{
	/// <summary>
	/// The configured set of backends, keyed by normalized name.
	/// </summary>
	public sealed class BackendDirectory
	{
		private readonly LinkedHashMap<string, BackendConfig> backends;
		private readonly string defaultBackendName;
		private readonly string hubBackendName;

		public BackendDirectory(LinkedHashMap<string, BackendConfig> backends, string defaultBackendName, string hubBackendName)
		{
			if (backends == null || backends.Count == 0)
			{
				throw new ArgumentException("backends cannot be empty");
			}
			this.backends = Normalized(backends);
			this.defaultBackendName = Normalize(defaultBackendName);
			this.hubBackendName = Normalize(hubBackendName);
			if (!this.backends.ContainsKey(this.defaultBackendName))
			{
				throw new ArgumentException("default backend is not configured: " + defaultBackendName);
			}
			if (!this.backends.ContainsKey(this.hubBackendName))
			{
				throw new ArgumentException("hub backend is not configured: " + hubBackendName);
			}
		}

		public BackendConfig DefaultBackend() => backends[defaultBackendName];

		public BackendConfig HubBackend() => backends[hubBackendName];

		public BackendConfig? Find(string? name)
		{
			return backends.TryGetValue(Normalize(name), out BackendConfig? backend) ? backend : null;
		}

		/// <summary>
		/// Finds the configured backend addressed by a Bedrock <c>TransferPacket</c>. Only endpoints
		/// already present in the proxy configuration qualify; resolving an arbitrary host supplied by a
		/// backend here would block the packet thread, so other aliases fall through to the normal
		/// client-side transfer.
		/// </summary>
		public BackendConfig? FindByAddress(string? host, int port)
		{
			if (string.IsNullOrWhiteSpace(host) || port < 1 || port > 65_535)
			{
				return null;
			}
			string normalizedHost = NormalizeHost(host);
			foreach (BackendConfig backend in backends.Values)
			{
				if (backend.Address.Port == port && MatchesHost(backend, normalizedHost))
				{
					return backend;
				}
			}
			return null;
		}

		public IReadOnlyList<BackendConfig> Backends()
		{
			var list = new List<BackendConfig>();
			foreach (BackendConfig backend in backends.Values)
			{
				list.Add(backend);
			}
			return list;
		}

		public List<string> BackendNames()
		{
			var names = new List<string>();
			foreach (BackendConfig backend in backends.Values)
			{
				names.Add(backend.Name);
			}
			return names;
		}

		private static LinkedHashMap<string, BackendConfig> Normalized(LinkedHashMap<string, BackendConfig> input)
		{
			var result = new LinkedHashMap<string, BackendConfig>();
			foreach (BackendConfig backend in input.Values)
			{
				result.Add(Normalize(backend.Name), backend);
			}
			return result;
		}

		private static string Normalize(string? name)
		{
			if (string.IsNullOrWhiteSpace(name))
			{
				throw new ArgumentException("backend name cannot be blank");
			}
			return name.Trim().ToLowerInvariant();
		}

		private static bool MatchesHost(BackendConfig backend, string transferHost)
		{
			// Java compared the host string the backend was configured with first (InetSocketAddress
			// keeps it), then fell back to the already-resolved numeric address. No DNS here either
			// way: this runs on a packet-reading thread, and resolving an arbitrary transfer host was
			// exactly what the Java original refused to do. Hostname aliases are handled by Find(name).
			if (NormalizeHost(backend.HostString).Equals(transferHost, StringComparison.Ordinal))
			{
				return true;
			}
			return NormalizeHost(backend.Address.Address.ToString())
				.Equals(transferHost, StringComparison.Ordinal);
		}

		private static string NormalizeHost(string host)
		{
			string normalized = host.Trim();
			if (normalized.Length > 1 && normalized[0] == '[' && normalized[^1] == ']')
			{
				normalized = normalized[1..^1];
			}
			while (normalized.EndsWith(".") && normalized.Length > 1)
			{
				normalized = normalized[..^1];
			}
			return normalized.ToLowerInvariant();
		}
	}
}
