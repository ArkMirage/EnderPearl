using System;
using System.Collections.Generic;
using System.Net;
using System.Text;
using System.Threading;
using EnderPearl.Auth;
using EnderPearl.Backend;
using EnderPearl.Command;
using EnderPearl.Config;
using EnderPearl.Core;
using EnderPearl.Protocol;
using EnderPearl.Permission;
using EnderPearl.Security;
using EnderPearl.Player;
using NetherNet.Endpoint;
using EnderPearl.Transport;
using EnderPearl.Relay;
using EnderPearl.Server;

namespace EnderPearl.Frontend;
/// <summary>
/// EnderPearl's front door: binds the NetherNet endpoint, advertises the server-list status, throttles
/// connections, and hands each accepted client to <see cref="ClientLoginHandler"/>.
///
/// <p>This is the C# port of the Java original onto the plain NetherNet listener/accept model: there
/// is no Netty pipeline here, so per-connection work (throttle, pre-auth batch limit, handler
/// wiring) happens at accept time instead of in channel initializers.</p>
///
/// <p>What it owns is the listener, the session table, the throttle, the command manager and the
/// services it assembles - the backend connector, the login forge, the console. The proxy-wide state
/// those services and the handlers need (players, permissions, backends, config)
/// is read from <see cref="ProxyServer"/> rather than held here.</p>
/// </summary>
public sealed class BedrockProxyListener
{
	private readonly HashSet<ListenerSession> sessions = new();
	private readonly ManualResetEvent stopped = new(false);
	private readonly ProxyCommandManager commandManager = new();
	private readonly ConnectionThrottle connectionThrottle;
	private readonly string configDirectory;
	private ProxyConsole? console;
	private NetherNet.Listener? listener;
	private Handler? handler;
	private volatile bool shuttingDown;

	public BedrockProxyListener(string configDirectory)
	{
		this.configDirectory = configDirectory ?? throw new ArgumentNullException(nameof(configDirectory));
		// Reading the security config here is also the check that ProxyServer.Initialize ran first: a
		// listener built before the proxy-wide state exists fails loudly instead of accepting peers.
		connectionThrottle = new ConnectionThrottle(ProxyServer.Policy.Security);
	}

	public void Start()
	{
		IPEndPoint listen = ProxyServer.Config.ListenAddress;
		var onlineLoginForge = new OnlineLoginForge();
		var backendConnector = new BackendConnector
		{
			CommandManager = commandManager,
			OnlineLoginForge = onlineLoginForge
		}.Prepare();
		var networkCommands = new NetworkCommands
		{
			Switcher = backendConnector.Switcher,
			CommandManager = commandManager
		};
		// The manager is built bare and filled from outside: SystemCommands registers the proxy's own
		// commands for the terminal and for chat, and a plugin loaded later registers its commands
		// exactly the same way.
		SystemCommands.Register(commandManager, networkCommands, Stop);
		console = new ProxyConsole(commandManager);
		SecurityConfig security = ProxyServer.Policy.Security;

		listener = BindListener(listen,security);

		StartAcceptLoop(listener!, backendConnector, security);

		Logger.Info(
			$"Security: maxConnectionsPerAddress={security.MaxConnectionsPerAddress} "
			+ $"maxConnectionAttempts={security.MaxConnectionAttempts}/{security.ConnectionAttemptWindowMillis}ms requireXuid={security.RequireXuid} commandCooldownMillis={security.CommandCooldownMillis}."
			+ " Packet rate limiting is not enforced.");
		Logger.Info(
			"Diagnostics: verifyReencode=" + (EnderPearl.Core.PacketSession.VerifyReencode ? "on" : "off")
			+ " maxBatchBytes=0.");
		if (ProxyServer.Policy.Permissions.Admins.Count == 0)
		{
			Logger.Info(
				"No proxy administrators configured; /" + string.Join(", /", SortedCopy(ProxyServer.Policy.Permissions.AdminCommands))
				+ " are unavailable to everyone. Set permissions.admins to your XUID to use them.");
		}
		if (!ProxyServer.Policy.ForcedHosts.IsEmpty())
		{
			foreach (KeyValuePair<string, string> entry in ProxyServer.Policy.ForcedHosts.ByHostname)
			{
				Logger.Info($"Forced host {entry.Key} -> backend {entry.Value}.");
			}
		}
		// A command the proxy has given away answers differently depending on where the player is
		// standing, which is impossible to diagnose from a bug report. Say so once at startup.
		if (!ProxyServer.Policy.Commands.IsEmpty())
		{
			foreach (string backendName in BackendsNamesInOrder())
			{
				ICollection<string> passthroughSet = (ICollection<string>)ProxyServer.Policy.Commands.PassthroughFor(backendName);
				List<string> passthrough = new List<string>(passthroughSet);
				passthrough.Sort(StringComparer.Ordinal);
				if (passthrough.Count > 0)
				{
					Logger.Info(
						$"Backend {backendName} handles /{string.Join(", /", passthrough)} itself; the proxy forwards them there and does not"
						+ $" advertise its own. Use /{ProxyServer.Policy.Commands.Qualifier}<name> to reach the proxy's anywhere.");
				}
			}
		}
		int advertisedProtocol = (int)global::Protocol.ProtocolVersion.VERSION;
		Logger.Info(
			$"EnderPearl listening on {listen.Address}:{listen.Port} as '{ProxyServer.Config.Motd}' for Bedrock protocol {advertisedProtocol}, "
			+ $"Backend placeholder: {ProxyServer.Config.Backend.Name} {ProxyServer.Config.Backend.Address}.");
		console.Start();
	}

	private NetherNet.Listener BindListener(
		IPEndPoint address,
		SecurityConfig security
	)
	{
		handler = new Handler(new HandlerConfig
		{
			Logger = message => Logger.Info($"[NetherNet] {message}"),
			Credentials = _ => Task.FromResult<NetherNet.Credentials?>(ProxyServer.Config.NetherNet.ToCredentials()),
		});
		Logger.Info($"NetherNet server identity: {NetherNetServerIdentity.PathFor(configDirectory)}");
		Logger.Info($"NetherNet ICE servers: {DescribeIceServers(ProxyServer.Config.NetherNet)}");
		NetherNet.Listener bound = NetherNet.ListenConfigExtensions.ListenAsync(new NetherNet.ListenConfig
		{
			// Bedrock clients present the identity assertion they received from the
			// authorization service; offline clients that omit it are still accepted and
			// authenticated by the proxy's own Login handling.
			AllowAnonymous = true,
			DisableTrickleICE = true,
			IssueServerIdentity = NetherNetServerIdentity.Issuer(configDirectory),
			Log = message => Logger.Info($"[NetherNet] {message}"),
		}, handler).GetAwaiter().GetResult();
		handler.Status(Advertisement());
		handler.Start($"{address.Address}:{address.Port}");
		return bound;
	}

	private void StartAcceptLoop(
		NetherNet.Listener netherListener,
		BackendConnector backendConnector,
		SecurityConfig security
	)
	{
		var thread = new Thread(() =>
		{
			while (!shuttingDown)
			{
				NetherNetConnection conn;
				try
				{
					conn = new NetherNetConnection(netherListener.AcceptAsync().GetAwaiter().GetResult());
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
					AcceptConnection(conn, backendConnector, security);
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
			Name = "enderpearl-accept-" + netherListener.Addr(),
			IsBackground = true
		};
		thread.Start();
	}

	private void AcceptConnection(
		NetherNetConnection conn,
		BackendConnector backendConnector,
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
		session.SetPacketHandler(new ClientLoginHandler
		{
			Session = session,
			Negotiator = new NetworkSettingsNegotiator(
				new ProtocolNegotiator(),
				ProxyServer.Config.CompressionAlgorithm,
				ProxyServer.Config.CompressionThreshold
			),
			Connector = backendConnector,
			Authenticator = new ClientLoginAuthenticator(
				security.RequireXuid
			),
			PlayerCountChanged = OnPlayerRosterChanged
		});
		// Handler first, read loop second - Java's initSession ordering. Starting the loop before
		// the handler existed silently dropped a client's earliest packets.
		session.StartReading();
		UpdateAdvertisement();
	}

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
			handler?.Close();
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
		ProxyServer.ConnectedPlayers.Unregister(session.ProxyConnection);
		OnPlayerRosterChanged();
	}

	/// <summary>
	/// Someone joined or left: refresh the server-list count and push the new roster to the clients
	/// that autocomplete against it.
	/// </summary>
	private void OnPlayerRosterChanged()
	{
		UpdateAdvertisement();
		ProxyServer.PlayerEnum.Broadcast();
	}

	private void UpdateAdvertisement()
	{
		handler?.Status(Advertisement());
	}

	private NetherNet.Endpoint.Status Advertisement()
	{
		int advertisedProtocol = (int)global::Protocol.ProtocolVersion.VERSION;
		return new NetherNet.Endpoint.Status
		{
			ServerName = ProxyServer.Config.Motd,
			Protocol = advertisedProtocol,
			Version = advertisedProtocol.ToString(),
			LevelName = ProxyServer.Config.SubMotd,
			PlayerCount = ProxyServer.ConnectedPlayers.Size(),
			MaxPlayerCount = ProxyServer.Config.MaxPlayers,
			GameType = GameTypeFromName(ProxyServer.Config.GameType),
		};
	}

	private static int GameTypeFromName(string value)
	{
		return value.Trim().ToLowerInvariant() switch
		{
			"creative" => NetherNet.Endpoint.GameTypes.Creative,
			"adventure" => NetherNet.Endpoint.GameTypes.Adventure,
			_ => NetherNet.Endpoint.GameTypes.Survival,
		};
	}

	private static string DescribeIceServers(NetherNetConfig netherNet)
	{
		var urls = new List<string>();
		foreach (string url in netherNet.StunServers)
		{
			urls.Add(url);
		}
		foreach (TurnServerConfig turn in netherNet.TurnServers)
		{
			foreach (string url in turn.Urls)
			{
				urls.Add(url);
			}
		}
		return urls.Count == 0 ? "none" : string.Join(", ", urls);
	}

	private List<string> BackendsNamesInOrder()
	{
		var names = new List<string>();
		foreach (BackendConfig backend in ProxyServer.Config.Backends.Values)
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
}
