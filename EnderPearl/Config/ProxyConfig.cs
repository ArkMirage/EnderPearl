using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Protocol;
using EnderPearl.Protocol;
using EnderPearl.Logging;

namespace EnderPearl.Config
{
	/// <summary>
	/// The whole proxy configuration, loaded from <c>config.json</c>.
	/// </summary>
	/// <remarks>
	/// <para>Each section of the file belongs to one class in this package, which owns three things:
	/// a <c>From(JsonConfig)</c> that reads it, the defaults those reads fall back to
	/// (<c>Defaults()</c> or constants), and a <c>DefaultSection()</c> that writes them into the file
	/// generated on first start. To add a setting, touch only the owning section's file - parse it,
	/// give it a default, add it to the template - and this class composes the rest.</para>
	///
	/// <para><see cref="ProxyConfig"/> itself owns only the top-level scalars (<c>listener</c>,
	/// <c>motd</c>, compression, pack paths...) and the composition.</para>
	/// </remarks>
	public sealed class ProxyConfig
	{
		private const string DEFAULT_LISTEN_HOST = "0.0.0.0";
		private const int DEFAULT_LISTEN_PORT = 19132;

		public IPEndPoint ListenAddress { get; }

		public BackendConfig Backend { get; }

		public LinkedHashMap<string, BackendConfig> Backends { get; }

		public string HubBackendName { get; }

		public BedrockCodecInfo? BackendProtocol { get; }

		public ProxyPolicy Policy { get; }

		public string Motd { get; }

		public string SubMotd { get; }

		public string GameType { get; }

		public int MaxPlayers { get; }
		public int KeyForgePort { get; }

		public PacketCompressionAlgorithm CompressionAlgorithm { get; }

		public int CompressionThreshold { get; }

		public string? BackendPackCacheDir { get; }

		public string PublicAddress { get; }

		public ProxyConfig(
			IPEndPoint listenAddress,
			BackendConfig backend,
			LinkedHashMap<string, BackendConfig> backends,
			string hubBackendName,
			BedrockCodecInfo? backendProtocol,
			ProxyPolicy policy,
			string motd,
			string subMotd,
			string gameType,
			int maxPlayers,
			PacketCompressionAlgorithm compressionAlgorithm,
			int compressionThreshold,
			int keyForgePort)
			: this(listenAddress, backend, backends, hubBackendName, backendProtocol, policy,
				motd, subMotd, gameType, maxPlayers, compressionAlgorithm, compressionThreshold,
				 backendPackCacheDir: null, publicAddress: "", keyForgePort)
		{
		}

		public ProxyConfig(
			IPEndPoint listenAddress,
			BackendConfig backend,
			LinkedHashMap<string, BackendConfig> backends,
			string hubBackendName,
			BedrockCodecInfo? backendProtocol,
			ProxyPolicy policy,
			string motd,
			string subMotd,
			string gameType,
			int maxPlayers,
			PacketCompressionAlgorithm compressionAlgorithm,
			int compressionThreshold,
			string? backendPackCacheDir,
			string publicAddress,
			int keyForgePort)
		{
			if (listenAddress == null)
			{
				throw new ArgumentNullException(nameof(listenAddress));
			}
			if (backend == null)
			{
				throw new ArgumentNullException(nameof(backend));
			}
			if (backends == null || backends.Count == 0)
			{
				throw new ArgumentException("backends cannot be empty");
			}
			if (string.IsNullOrWhiteSpace(hubBackendName))
			{
				throw new ArgumentException("hubBackendName cannot be blank");
			}
			if (policy == null)
			{
				throw new ArgumentNullException(nameof(policy));
			}
			if (string.IsNullOrWhiteSpace(motd))
			{
				throw new ArgumentException("motd cannot be blank");
			}
			if (subMotd == null)
			{
				throw new ArgumentNullException(nameof(subMotd));
			}
			if (string.IsNullOrWhiteSpace(gameType))
			{
				throw new ArgumentException("gameType cannot be blank");
			}
			if (maxPlayers < 1)
			{
				throw new ArgumentException("maxPlayers must be positive");
			}
			if (compressionThreshold < 0)
			{
				throw new ArgumentException("compressionThreshold cannot be negative");
			}
			ListenAddress = listenAddress;
			Backend = backend;
			Backends = backends;
			HubBackendName = hubBackendName;
			BackendProtocol = backendProtocol;
			Policy = policy;
			Motd = motd;
			SubMotd = subMotd;
			GameType = gameType;
			MaxPlayers = maxPlayers;
			KeyForgePort = keyForgePort;
			CompressionAlgorithm = compressionAlgorithm;
			CompressionThreshold = compressionThreshold;
			BackendPackCacheDir = backendPackCacheDir;
			PublicAddress = publicAddress;
		}

		public FailoverConfig Failover => Policy.Failover;

		public BackendSwitchConfig BackendSwitch => Policy.BackendSwitch;

		public PermissionsConfig Permissions => Policy.Permissions;

		public SecurityConfig Security => Policy.Security;

		public ForcedHostsConfig ForcedHosts => Policy.ForcedHosts;

		public JoinConfig Join => Policy.Join;

		public CommandsConfig Commands => Policy.Commands;

		// No packaged config template exists, so a generated on-disk config is the only configuration
		// documentation an operator ever sees there. It has to be a working default rather than nothing.
		public static ProxyConfig LoadOrCreate(string path)
		{
			if (!File.Exists(path))
			{
				string? parent = Path.GetDirectoryName(Path.GetFullPath(path));
				if (!string.IsNullOrEmpty(parent))
				{
					Directory.CreateDirectory(parent);
				}
				WriteDefaultConfig(path);
			}

			// Deliberately re-read the file even on the run that just created it, so what is on disk and
			// what the proxy is running can never disagree.
			JsonConfig json = JsonConfig.LoadFromFile(path);
			return From(json, Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".");
		}

		private static void WriteDefaultConfig(string path)
		{
			File.WriteAllText(path, JsonConfig.Serialize(DefaultConfig()), new UTF8Encoding(false));
		}

		public static ProxyConfig From(JsonConfig config) => From(config, ".");

		public static ProxyConfig From(JsonConfig config, string configDir)
		{
			string listenHost = config.GetString("listener.host", DEFAULT_LISTEN_HOST);
			int listenPort = config.GetInt("listener.port", DEFAULT_LISTEN_PORT);

			// Document order is try order: the first entry in "backends" doubles as the default join target.
			LinkedHashMap<string, BackendConfig> backends = BackendConfig.LoadAll(config);
			(string defaultBackendName, BackendConfig defaultBackend) = FirstBackend(backends);

			string hubBackendName = config.GetString("hubBackend", defaultBackendName);
			// The global protocol pin applies to every backend without its own "protocol"; null ("auto")
			// lets each connection be probed at startup instead.
			BedrockCodecInfo? backendProtocol =
				CanonicalProtocol.FromConfig(config.GetString("protocol", "auto"));
			string defaultSubMotd = "Bedrock " + CanonicalProtocol.Newest().MinecraftVersion;

			

			FailoverConfig failover = FailoverConfig.From(config, hubBackendName);
			return new ProxyConfig(
				InetEndpoints.Resolve(listenHost, listenPort),
				defaultBackend,
				backends,
				hubBackendName,
				backendProtocol,
				new ProxyPolicy(
					failover,
					BackendSwitchConfig.From(config),
					PermissionsConfig.From(config),
					SecurityConfig.From(config),
					ForcedHostsConfig.From(config, backends),
					JoinConfig.From(config, failover),
					CommandsConfig.From(config)
				),
				config.GetString("motd", "Endstone Proxy"),
				config.GetString("subMotd", defaultSubMotd),
				config.GetString("gameType", "Survival"),
				config.GetInt("maxPlayers", 20),
				Compression(config.GetString("compression", "zlib")),
				config.GetInt("compressionThreshold", 0),
				Path.GetFullPath(Path.Combine(configDir, "cache", "packs")),
				config.GetString("publicAddress", "").Trim(),
				config.GetInt("keyForge.port", 19139)
			);
		}

		private static (string Name, BackendConfig Backend) FirstBackend(LinkedHashMap<string, BackendConfig> backends)
		{
			foreach (KeyValuePair<string, BackendConfig> entry in backends)
			{
				return (entry.Key, entry.Value);
			}
			throw new ArgumentException("backends cannot be empty");
		}

		/// <summary>The configuration written when no config file exists yet: every section's template composed.</summary>
		public static JsonObject DefaultConfig()
		{
			string defaultSubMotd = "Bedrock " + CanonicalProtocol.Newest().MinecraftVersion;
			return new JsonObject
			{
				["listener"] = new JsonObject
				{
					["host"] = DEFAULT_LISTEN_HOST,
					["port"] = DEFAULT_LISTEN_PORT
				},
				["protocol"] = "auto",
				["backends"] = BackendConfig.DefaultSection(),
				["hubBackend"] = BackendConfig.DEFAULT_NAME,
				["failover"] = FailoverConfig.DefaultSection(),
				["protocolFault"] = ProtocolFaultPolicy.DefaultSection(),
				["switch"] = BackendSwitchConfig.DefaultSection(),
				["join"] = JoinConfig.DefaultSection(),
				["permissions"] = PermissionsConfig.DefaultSection(),
				["commands"] = CommandsConfig.DefaultSection(),
				["security"] = SecurityConfig.DefaultSection(),
				["forcedHosts"] = ForcedHostsConfig.DefaultSection(),
				["motd"] = "Endstone Proxy",
				["subMotd"] = defaultSubMotd,
				["gameType"] = "Survival",
				["maxPlayers"] = 20,
				["compression"] = "zlib",
				["compressionThreshold"] = 0,
				["resourcePacks"] = new JsonObject
				{
					["dir"] = "",
					["cacheBackendPacks"] = true
				},
				["publicAddress"] = "",
				["keyForge"] = new JsonObject
				{
					["port"] = 19139
				}
			};
		}

		private static PacketCompressionAlgorithm Compression(string value)
		{
			return value.Trim().ToLowerInvariant() switch
			{
				"none" => PacketCompressionAlgorithm.None,
				"snappy" => PacketCompressionAlgorithm.Snappy,
				"zlib" => PacketCompressionAlgorithm.ZLib,
				_ => throw new ArgumentException("Unsupported compression algorithm: " + value)
			};
		}
	}

	/// <summary>Resolves host names the way Java's InetSocketAddress constructor does.</summary>
	public static class InetEndpoints
	{
		private static readonly HashSet<string> UnresolutionWarnedFor = new();

		public static IPEndPoint Resolve(string host, int port)
		{
			ArgumentNullException.ThrowIfNull(host);
			if (!IPAddress.TryParse(host.Trim('[', ']'), out IPAddress? address))
			{
				try
				{
					address = Dns.GetHostAddresses(host) is { Length: > 0 } addresses ? addresses[0] : null;
				}
				catch (Exception exception)
				{
					address = null;
					WarnUnresolved(host, exception.Message);
				}
				if (address == null)
				{
					WarnUnresolved(host, "no addresses returned");
					// Java's new InetSocketAddress(host, port) leaves the address UNRESOLVED on DNS
					// failure instead of throwing: the proxy still starts, and every dial to this
					// backend fails per attempt so failover can move to the next candidate. Dialling a
					// wildcard endpoint reproduces that per-attempt failure; the configured host name
					// is preserved separately as BackendConfig.HostString.
					address = host.Contains(':') ? IPAddress.IPv6Any : IPAddress.Any;
				}
			}
			return new IPEndPoint(address, port);
		}

		private static void WarnUnresolved(string host, string why)
		{
			if (UnresolutionWarnedFor.Add(host))
			{
				Logger.Info(
					$"WARNING: cannot resolve backend host '{host}' ({why}); joins to it will fail until DNS recovers or the address is fixed.");
			}
		}
	}
}
