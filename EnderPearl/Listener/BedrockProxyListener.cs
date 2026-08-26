using System;
using System.Collections.Generic;
using System.Net;
using System.Text;
using System.Threading;
using EnderPearl.Auth;
using EnderPearl.Backend;
using EnderPearl.Command;
using EnderPearl.Crypto;
using EnderPearl.Config;
using EnderPearl.Net;
using EnderPearl.Network;
using EnderPearl.Permission;
using EnderPearl.Protocol;

using EnderPearl.Resource;
using EnderPearl.Security;
using EnderPearl.Session;
using RakNet;
using EnderPearl.Logging;

namespace EnderPearl.Listener
{
	/// <summary>
	/// EnderPearl's front door: binds the RakNet listeners, advertises the server list entry, throttles
	/// connections, and hands each accepted client to <see cref="InitialClientPacketHandler"/>.
	///
	/// <p>This is the C# port of the Java original onto the plain RakNet listener/accept model: there
	/// is no Netty pipeline here, so per-connection work (throttle, pre-auth batch limit, handler
	/// wiring) happens at accept time instead of in channel initializers.</p>
	/// </summary>
	public sealed class BedrockProxyListener
	{
		private readonly ProxyConfig config;
		private readonly ProtocolRegistry protocolRegistry;
		private readonly HashSet<ListenerSession> sessions = new();
		private readonly ManualResetEvent stopped = new(false);
		private readonly ProxyCommandRegistry commandRegistry = ProxyCommandRegistry.Defaults();
		private readonly long serverId = Random.Shared.NextInt64() & 0x7FFFFFFFFFFFFFFF;
		private readonly ConnectedPlayerRegistry connectedPlayers;
		private readonly ConnectionThrottle connectionThrottle;
		private readonly ProxyPlayerEnum playerEnum;
		private readonly ProxyPermissions permissions;
		private readonly MojangMimicIdentity? mimicIdentity;
		private ProxyConsole? console;
		private RakNet.Listener? listener;
		private Palette.BackendPaletteStore backendPaletteStore = Palette.BackendPaletteStore.Disabled();
		private BackendPackCache backendPackCache = BackendPackCache.Disabled();
		private volatile bool shuttingDown;

		public BedrockProxyListener(ProxyConfig config, ProxyPermissions permissions, MojangMimicIdentity? mimicIdentity)
		{
			this.config = config ?? throw new ArgumentNullException(nameof(config));
			this.mimicIdentity = mimicIdentity;
			this.protocolRegistry = ProtocolRegistry.CreateDefault();
			this.permissions = permissions ?? ProxyPermissions.InMemory(config.Permissions);
			connectedPlayers = new ConnectedPlayerRegistry(config.MaxPlayers);
			connectionThrottle = new ConnectionThrottle(config.Security);
			playerEnum = new ProxyPlayerEnum(connectedPlayers, this.permissions);
		}

		public void Start()
		{
			IPEndPoint listen = config.ListenAddress;
			var backendDirectory = new BackendDirectory(
				config.Backends,
				config.Backend.Name,
				config.HubBackendName
			);
			ProxyResourcePackRegistry resourcePackRegistry = config.CacheBackendPacks
				? ProxyResourcePackRegistry.Load(config.ResourcePacksDir, config.BackendPackCacheDir)
				: ProxyResourcePackRegistry.Load(config.ResourcePacksDir);
			backendPackCache = config.CacheBackendPacks
				? BackendPackCache.Of(config.BackendPackCacheDir!, resourcePackRegistry)
				: BackendPackCache.Disabled();
			if (!resourcePackRegistry.IsEmpty())
			{
				Logger.Info($"Proxy resource pack registry: {resourcePackRegistry.Packs().Count} pack(s) loaded.");
			}
			backendPaletteStore = config.CrossBackendPalette
				? Palette.BackendPaletteStore.Load(config.CrossBackendPaletteCacheFile)
				: Palette.BackendPaletteStore.Disabled();
			if (config.CrossBackendPalette)
			{
				Logger.Info($"Cross-backend item and entity registries on ({backendPaletteStore.Describe()}).");
				// A backend nobody has visited yet contributes nothing to a joining client's registries,
				// so its custom content is wrong for anyone who switches there before it is learned.
				List<string> unlearned = new List<string>();
				foreach (string name in config.Backends.Keys)
				{
					if (!backendPaletteStore.KnownBackends().Contains(name))
					{
						unlearned.Add(name);
					}
				}
				unlearned.Sort(StringComparer.Ordinal);
				if (unlearned.Count > 0)
				{
					Logger.Info(
						$"Backends not learned yet: {string.Join(", ", unlearned)}. Their custom items and entities render correctly only "
						+ "for players who log in after someone has been there once.");
				}
			}
			var onlineLoginForge = new OnlineLoginForge();
			var backendConnector = new BackendConnector(
				backendDirectory,
				commandRegistry,
				mimicIdentity,
				protocolRegistry,
				config.BackendProtocol,
				onlineLoginForge,
				connectedPlayers.XuidByName,
				config.Policy,
				connectedPlayers,
				permissions,
				playerEnum,
				backendPaletteStore,
				config.PublicAddress,
				listen.Port
			);
			var networkCommands = new NetworkCommands(
				connectedPlayers,
				backendDirectory,
				backendConnector.Switcher(),
				permissions,
				commandRegistry,
				playerEnum.Broadcast
			);
			console = new ProxyConsole(networkCommands, Stop);
			backendConnector.SetNetworkCommands(networkCommands);
			SecurityConfig security = config.Security;

			listener = BindListener(listen, backendConnector, onlineLoginForge, resourcePackRegistry, security);

			StartAcceptLoop(listener!, backendConnector, onlineLoginForge, resourcePackRegistry, security);

			Logger.Info(
				$"Security: connectionCookie={(security.SendConnectionCookie ? "on" : "OFF")} maxConnectionsPerAddress={security.MaxConnectionsPerAddress} "
				+ $"maxConnectionAttempts={security.MaxConnectionAttempts}/{security.ConnectionAttemptWindowMillis}ms requireXuid={security.RequireXuid} commandCooldownMillis={security.CommandCooldownMillis}."
				+ " Packet rate limiting is not enforced.");
			Logger.Info(
				"Diagnostics: verifyReencode=" + (EnderPearl.Net.PacketSession.VerifyReencode ? "on" : "off")
				+ " verifyEncode=off strictEncode=off maxBatchBytes=0 traceBatches=off logPackets="
				+ (EnderPearl.Backend.ProxyConnection.IsContinuousPacketTracingConfigured() ? "on" : "off")
				+ $" traceMillis={EnderPearl.Backend.ProxyConnection.ConfiguredPacketTraceMillis()} forceChunkRadius=0 "
				+ $"{BackendRelayPacketHandler.DiagnosticSuppressionSummary()} {ClientRelayPacketHandler.MovementSampleSummary()}.");
			if (config.Permissions.Admins.Count == 0)
			{
				Logger.Info(
					"No proxy administrators configured; /" + string.Join(", /", SortedCopy(config.Permissions.AdminCommands))
					+ " are unavailable to everyone. Set permissions.admins to your XUID to use them.");
			}
			if (!config.ForcedHosts.IsEmpty())
			{
				foreach (KeyValuePair<string, string> entry in config.ForcedHosts.ByHostname)
				{
					Logger.Info($"Forced host {entry.Key} -> backend {entry.Value}.");
				}
			}
			// A command the proxy has given away answers differently depending on where the player is
			// standing, which is impossible to diagnose from a bug report. Say so once at startup.
			if (!config.Commands.IsEmpty())
			{
				foreach (string backendName in BackendsNamesInOrder())
				{
					ICollection<string> passthroughSet = (ICollection<string>)config.Commands.PassthroughFor(backendName);
					List<string> passthrough = new List<string>(passthroughSet);
					passthrough.Sort(StringComparer.Ordinal);
					if (passthrough.Count > 0)
					{
						Logger.Info(
							$"Backend {backendName} handles /{string.Join(", /", passthrough)} itself; the proxy forwards them there and does not"
							+ $" advertise its own. Use /{config.Commands.Qualifier}<name> to reach the proxy's anywhere.");
					}
				}
			}
			Logger.Info(
				$"EnderPearl listening on {listen.Address}:{listen.Port} as '{config.Motd}' for Bedrock {protocolRegistry.AdvertisedClientCodec().MinecraftVersion} "
				+ $"(protocol {protocolRegistry.AdvertisedClientCodec().ProtocolVersion}), backend protocol {BackendProtocolDescription()}. "
				+ $"Backend placeholder: {config.Backend.Name} {config.Backend.Address}.");
			console.Start();
		}

		private RakNet.Listener BindListener(
			IPEndPoint address,
			BackendConnector backendConnector,
			OnlineLoginForge onlineLoginForge,
			ProxyResourcePackRegistry resourcePackRegistry,
			SecurityConfig security
		)
		{
			var listenConfig = new ListenConfig
			{
				ErrorLog = message => Logger.Info($"[RakNet] {message}"),
				// With the cookie on, the handshake proves the client can receive at its claimed
				// address, which makes a spoofed source IP useless for opening sessions.
				DisableCookies = !security.SendConnectionCookie,
			};
			RakNet.Listener bound = listenConfig.Listen(address.ToString());
			bound.SetPongDataFunc(_ => Advertisement().ToByteArray());
			return bound;
		}

		private void StartAcceptLoop(
			RakNet.Listener rakListener,
			BackendConnector backendConnector,
			OnlineLoginForge onlineLoginForge,
			ProxyResourcePackRegistry resourcePackRegistry,
			SecurityConfig security
		)
		{
			var thread = new Thread(() =>
			{
				while (!shuttingDown)
				{
					RakNet.Conn conn;
					try
					{
						conn = rakListener.Accept();
					}
					catch (Exception exception) when (shuttingDown)
					{
						break;
					}
					catch (Exception exception)
					{
						Logger.Error($"Listener accept failed: {exception.Message}");
						continue;
					}
					try
					{
						AcceptConnection(conn, backendConnector, onlineLoginForge, resourcePackRegistry, security);
					}
					catch (Exception exception)
					{
						// One bad accept must never kill this thread: it silently stopped every
						// future join until the next proxy restart, with nothing in the log.
						Logger.Error($"Accepting connection from {conn.RemoteEndPoint} failed: {exception}");
					}
				}
			})
			{
				Name = "enderpearl-accept-" + rakListener.LocalEndPoint,
				IsBackground = true
			};
			thread.Start();
		}

		private void AcceptConnection(
			RakNet.Conn conn,
			BackendConnector backendConnector,
			OnlineLoginForge onlineLoginForge,
			ProxyResourcePackRegistry resourcePackRegistry,
			SecurityConfig security
		)
		{
			// RAK_MAX_CONNECTIONS is one pool shared by every address, so an unthrottled host can hold
			// all of it. Close the raw connection before a session exists - no codec has been
			// negotiated yet, so there is nothing to encode a kick message with.
			if (!connectionThrottle.Accept(conn.RemoteEndPoint))
			{
				conn.Close();
				return;
			}
			var session = new ListenerSession(conn, OnSessionClosed);
			session.SetThrottled(true);
			// Join-attempt visibility: everything between here and "Player X joined" used to be
			// silent, so a join that died early left no trace at all.
			Logger.Info($"Connection opened from {conn.RemoteEndPoint}.");
			// Bound what an anonymous peer can make the proxy allocate; lifts as soon as login succeeds
			// and ProxyConnection runs.
			session.Session.MaxInboundBatchBytesProvider = () => session.ProxyConnection != null
				? 0
				: PreAuthBatchLimiter.MaxPreAuthBatchBytes;
			lock (sessions)
			{
				sessions.Add(session);
			}
			session.SetPacketHandler(new InitialClientPacketHandler(
				session,
				new NetworkSettingsNegotiator(
					new ProtocolNegotiator(protocolRegistry),
					config.CompressionAlgorithm,
					config.CompressionThreshold
				),
				backendConnector,
				new ClientLoginAuthenticator(
					security.RequireXuid
				),
				onlineLoginForge,
				connectedPlayers,
				OnPlayerRosterChanged,
				resourcePackRegistry,
				backendPaletteStore,
				backendPackCache
			));
			// Handler first, read loop second - Java's initSession ordering. Starting the loop before
			// the handler existed silently dropped a client's earliest packets.
			session.StartReading();
			UpdateAdvertisement();
		}

		/// <summary>Everyone currently past login. Exposed so an end-to-end test can observe a join.</summary>
		public ConnectedPlayerRegistry ConnectedPlayers() => connectedPlayers;

		public void AwaitShutdown()
		{
			stopped.WaitOne();
		}

		public void Stop()
		{
			if (shuttingDown)
			{
				return;
			}
			shuttingDown = true;
			try
			{
				console?.Stop();
				listener?.Close();
				lock (sessions)
				{
					foreach (ListenerSession session in sessions)
					{
						try
						{
							session.CloseTransport();
						}
						catch (Exception)
						{
							// Best effort during shutdown.
						}
					}
				}
			}
			finally
			{
				stopped.Set();
			}
		}

		private void OnSessionClosed(ListenerSession session)
		{
			lock (sessions)
			{
				sessions.Remove(session);
			}
			// Only sessions that got past the throttle were counted, and a refused one never reaches
			// here - releasing that would hand an unclaimed slot away.
			if (session.IsThrottled && session.RemoteEndPoint != null)
			{
				connectionThrottle.Release(session.RemoteEndPoint);
			}
			connectedPlayers.Unregister(session.ProxyConnection);
			OnPlayerRosterChanged();
		}

		/// <summary>
		/// Someone joined or left: refresh the server-list count and push the new roster to the clients
		/// that autocomplete against it.
		/// </summary>
		private void OnPlayerRosterChanged()
		{
			UpdateAdvertisement();
			playerEnum.Broadcast();
		}

		private void UpdateAdvertisement()
		{
			RakNet.Listener? current = listener;
			if (current != null)
			{
				current.SetPongDataFunc(_ => Advertisement().ToByteArray());
			}
		}

		private PongBuilder Advertisement()
		{
			int port = config.ListenAddress.Port;
			BedrockCodecInfo advertisedCodec = protocolRegistry.AdvertisedClientCodec();
			return new PongBuilder()
				.Field("MCPE")
				.Field(config.Motd)
				.Field(advertisedCodec.ProtocolVersion.ToString())
				.Field(advertisedCodec.MinecraftVersion)
				.Field(connectedPlayers.Size().ToString())
				.Field(config.MaxPlayers.ToString())
				.Field(serverId.ToString())
				.Field(config.SubMotd)
				.Field(config.GameType)
				.Field("1")
				.Field(port.ToString())
				.Field(port.ToString())
				.Field("0")
				.Field("");
		}

		private string BackendProtocolDescription()
		{
			return config.BackendProtocol == null
				? "auto"
				: config.BackendProtocol.MinecraftVersion + " (protocol " + config.BackendProtocol.ProtocolVersion + ")";
		}

		private List<string> BackendsNamesInOrder()
		{
			var names = new List<string>();
			foreach (BackendConfig backend in config.Backends.Values)
			{
				names.Add(backend.Name);
			}
			return names;
		}

		private static List<string> SortedCopy(IEnumerable<string> values)
		{
			var sorted = new List<string>(values);
			sorted.Sort(StringComparer.Ordinal);
			return sorted;
		}

		/// <summary>Builds the semicolon-separated RakNet pong advertisement payload.</summary>
		internal sealed class PongBuilder
		{
			private readonly StringBuilder fields = new();

			public PongBuilder Field(string value)
			{
				if (fields.Length > 0)
				{
					fields.Append(';');
				}
				fields.Append(value);
				return this;
			}

			public byte[] ToByteArray()
			{
				fields.Append(';');
				return Encoding.UTF8.GetBytes(fields.ToString());
			}
		}
	}
}
