using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Protocol;
using EnderPearl.Protocol;
using EnderPearl.Core;

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

		private static T Checked<T>(T value, Func<T, bool> ok, string message)
	{
		if (!ok(value))
		{
			throw new ArgumentException(message);
		}
		return value;
	}

		public IPEndPoint ListenAddress { get; init; } = Checked(InetEndpoints.Resolve(DEFAULT_LISTEN_HOST, DEFAULT_LISTEN_PORT), a => a != null, "listenAddress cannot be null");

		public BackendConfig Backend { get; init; } = BackendConfig.DefaultAll()[BackendConfig.DEFAULT_NAME];

		private LinkedHashMap<string, BackendConfig> backends = BackendConfig.DefaultAll();

		public LinkedHashMap<string, BackendConfig> Backends { get => backends; init => backends = Checked(value, b => b != null && b.Count > 0, "backends cannot be empty"); }

		public string HubBackendName { get; init; } = BackendConfig.DEFAULT_NAME;

		public BedrockCodecInfo? BackendProtocol { get; init; }

		public required ProxyPolicy Policy { get; init; }

		private string motd = "Endstone Proxy";

		public string Motd { get => motd; init => motd = Checked(value, v => !string.IsNullOrWhiteSpace(v), "motd cannot be blank"); }

		public string SubMotd { get; init; } = "Bedrock " + BedrockCodecInfo.Current.MinecraftVersion;

		private string gameType = "Survival";

		public string GameType { get => gameType; init => gameType = Checked(value, v => !string.IsNullOrWhiteSpace(v), "gameType cannot be blank"); }

		private int maxPlayers = 20;

		public int MaxPlayers { get => maxPlayers; init => maxPlayers = Checked(value, v => v >= 1, "maxPlayers must be positive"); }

		public int KeyForgePort { get; init; } = 19139;

		public PacketCompressionAlgorithm CompressionAlgorithm { get; init; } = PacketCompressionAlgorithm.ZLib;

		private int compressionThreshold;

		public int CompressionThreshold { get => compressionThreshold; init => compressionThreshold = Checked(value, v => v >= 0, "compressionThreshold cannot be negative"); }

		public string? BackendPackCacheDir { get; init; }

		public string PublicAddress { get; init; } = "";

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
				BedrockCodecInfo.FromConfig(config.GetString("protocol", "auto"));
			string defaultSubMotd = "Bedrock " + BedrockCodecInfo.Current.MinecraftVersion;

			FailoverConfig failover = FailoverConfig.From(config, hubBackendName);
			return new ProxyConfig
			{
				ListenAddress = InetEndpoints.Resolve(listenHost, listenPort),
				Backend = defaultBackend,
				Backends = backends,
				HubBackendName = hubBackendName,
				BackendProtocol = backendProtocol,
				Policy = new ProxyPolicy
				{
					Failover = failover,
					BackendSwitch = BackendSwitchConfig.From(config),
					Permissions = PermissionsConfig.From(config),
					Security = SecurityConfig.From(config),
					ForcedHosts = ForcedHostsConfig.From(config, backends),
					Join = JoinConfig.From(config, failover),
					Commands = CommandsConfig.From(config)
				},
				Motd = config.GetString("motd", "Endstone Proxy"),
				SubMotd = config.GetString("subMotd", defaultSubMotd),
				GameType = config.GetString("gameType", "Survival"),
				MaxPlayers = config.GetInt("maxPlayers", 20),
				CompressionAlgorithm = Compression(config.GetString("compression", "zlib")),
				CompressionThreshold = config.GetInt("compressionThreshold", 0),
				BackendPackCacheDir = Path.GetFullPath(Path.Combine(configDir, "cache", "packs")),
				PublicAddress = config.GetString("publicAddress", "").Trim(),
				KeyForgePort = config.GetInt("keyForge.port", 19139)
			};
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
			string defaultSubMotd = "Bedrock " + BedrockCodecInfo.Current.MinecraftVersion;
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
