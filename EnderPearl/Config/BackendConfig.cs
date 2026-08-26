using System;
using System.Collections.Generic;
using System.Net;
using System.Text.Json.Nodes;
using EnderPearl.Protocol;

namespace EnderPearl.Config
{
	/// <summary>
	/// A configured backend: a name plus the address to dial it on.
	/// </summary>
	/// <param name="DropSubChunkRequests">stop forwarding the client's SubChunkRequestPacket to this
	/// backend, set with an entry's <c>"dropSubChunkRequests": true</c>.
	///
	/// <para>A Bedrock client asks for terrain one sub-chunk at a time only because a server told it
	/// to, and BDS does. That mode belongs to the client's session rather than to one backend, so it
	/// survives a switch — and the proxy's handoff is deliberately seamless, so the client is never
	/// told the new server works differently. A backend that does not implement the sub-chunk system
	/// then receives requests it never advertised; Geyser treats them as a protocol violation and drops
	/// the player.</para>
	///
	/// <para>Off by default: every Bedrock server implements this, and silently withholding the requests
	/// from one that does would leave terrain unloaded. Turn it on only for a backend that is not
	/// really a Bedrock server.</para></param>
	public sealed class BackendConfig
	{
		/// <summary>The lone localhost backend a bare install falls back to when "backends" is empty or absent.</summary>
		public const string DEFAULT_NAME = "default";
		public const string DEFAULT_HOST = "127.0.0.1";
		public const int DEFAULT_PORT = 19133;

		public string Name { get; }

		public IPEndPoint Address { get; }

		/// <summary>
		/// The host exactly as configured, before resolution - Java's InetSocketAddress.getHostString().
		/// A TransferPacket naming a hostname-configured backend is matched against this string first.
		/// </summary>
		public string HostString { get; }

		/// <summary>The Minecraft version this backend runs, or null to inherit the global setting.</summary>
		public BedrockCodecInfo? Protocol { get; }

		/// <summary>Whether SubChunkRequests are withheld from this backend; inferred when never configured.</summary>
		public bool DropSubChunkRequests { get; }

		public BackendConfig(
			string name,
			IPEndPoint address,
			BedrockCodecInfo? protocol = null,
			string? hostString = null,
			bool dropSubChunkRequests = false)
		{
			if (string.IsNullOrWhiteSpace(name))
			{
				throw new ArgumentException("name cannot be blank");
			}
			Name = name;
			Address = address ?? throw new ArgumentNullException(nameof(address));
			HostString = hostString ?? address.Address.ToString();
			Protocol = protocol;
			DropSubChunkRequests = dropSubChunkRequests;
		}

		public override string ToString() => Address.ToString();

		// ------------------------------------------------------------------ config

		/// <summary>
		/// Builds every configured backend from its object under <c>"backends"</c>, keeping document
		/// order: the first entry doubles as the default join target. With no backends at all a lone
		/// localhost default keeps a bare install bootable.
		/// </summary>
		public static LinkedHashMap<string, BackendConfig> LoadAll(JsonConfig config)
		{
			var backends = new LinkedHashMap<string, BackendConfig>();
			foreach (KeyValuePair<string, JsonConfig> entry in config.Members("backends"))
			{
				string name = entry.Key.Trim();
				if (name.Length == 0)
				{
					throw new ArgumentException("A backend name cannot be blank.");
				}
				string host = entry.Value.GetString("host", "").Trim();
				if (host.Length == 0)
				{
					throw new ArgumentException("Backend '" + name + "' needs a \"host\".");
				}
				backends.Add(ConfigValues.Normalize(name), new BackendConfig(
					name,
					InetEndpoints.Resolve(host, entry.Value.GetInt("port", DEFAULT_PORT)),
					CanonicalProtocol.FromConfig(entry.Value.GetString("protocol")),
					host,
					entry.Value.GetBool("dropSubChunkRequests", false)
				));
			}
			return backends.Count > 0 ? backends : DefaultAll();
		}

		public static LinkedHashMap<string, BackendConfig> DefaultAll()
		{
			var backends = new LinkedHashMap<string, BackendConfig>();
			backends.Add(DEFAULT_NAME, new BackendConfig(DEFAULT_NAME, InetEndpoints.Resolve(DEFAULT_HOST, DEFAULT_PORT)));
			return backends;
		}

		/// <summary>The <c>"backends"</c> object of the generated default configuration.</summary>
		public static JsonObject DefaultSection()
		{
			return new JsonObject
			{
				[DEFAULT_NAME] = new JsonObject
				{
					["host"] = DEFAULT_HOST,
					["port"] = DEFAULT_PORT
				}
			};
		}
	}
}
