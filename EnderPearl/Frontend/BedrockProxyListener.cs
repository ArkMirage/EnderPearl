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
using RakNet;
using EnderPearl.Relay;
using EnderPearl.Server;

namespace EnderPearl.Frontend
{
	/// <summary>
	/// EnderPearl's front door: binds the RakNet listeners, advertises the server list entry, throttles
	/// connections, and hands each accepted client to <see cref="ClientLoginHandler"/>.
	///
	/// <p>This is the C# port of the Java original onto the plain RakNet listener/accept model: there
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
		private readonly long serverId = Random.Shared.NextInt64() & 0x7FFFFFFFFFFFFFFF;
		private readonly ConnectionThrottle connectionThrottle;
		private ProxyConsole? console;
		private RakNet.Listener? listener;
		private volatile bool shuttingDown;

		public BedrockProxyListener()
		{
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

			StartAcceptLoop(listener!, backendConnector, onlineLoginForge, security);

			Logger.Info(
				$"Security: connectionCookie={(security.SendConnectionCookie ? "on" : "OFF")} maxConnectionsPerAddress={security.MaxConnectionsPerAddress} "
				+ $"maxConnectionAttempts={security.MaxConnectionAttempts}/{security.ConnectionAttemptWindowMillis}ms requireXuid={security.RequireXuid} commandCooldownMillis={security.CommandCooldownMillis}."
				+ " Packet rate limiting is not enforced.");
			Logger.Info(
				"Diagnostics: verifyReencode=" + (EnderPearl.Core.PacketSession.VerifyReencode ? "on" : "off")
				+ " verifyEncode=off strictEncode=off maxBatchBytes=0 traceBatches=off logPackets="
				+ (ProxyConnection.IsContinuousPacketTracingConfigured() ? "on" : "off")
				+ $" traceMillis={ProxyConnection.ConfiguredPacketTraceMillis()} forceChunkRadius=0 "
				+ $"{BackendRelayPacketHandler.DiagnosticSuppressionSummary()} {ClientRelayPacketHandler.MovementSampleSummary()}.");
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
				+ $"backend protocol auto-detected. "
				+ $"Backend placeholder: {ProxyServer.Config.Backend.Name} {ProxyServer.Config.Backend.Address}.");
			console.Start();
		}

		private RakNet.Listener BindListener(
			IPEndPoint address,
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
						AcceptConnection(conn, backendConnector, onlineLoginForge, security);
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
				LoginForge = onlineLoginForge,
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
			RakNet.Listener? current = listener;
			if (current != null)
			{
				current.SetPongDataFunc(_ => Advertisement().ToByteArray());
			}
		}

		private PongBuilder Advertisement()
		{
			int port = ProxyServer.Config.ListenAddress.Port;
			int advertisedProtocol = (int)global::Protocol.ProtocolVersion.VERSION;
			return new PongBuilder()
				.Field("MCPE")
				.Field(ProxyServer.Config.Motd)
				.Field(advertisedProtocol.ToString())
				.Field(advertisedProtocol.ToString())
				.Field(ProxyServer.ConnectedPlayers.Size().ToString())
				.Field(ProxyServer.Config.MaxPlayers.ToString())
				.Field(serverId.ToString())
				.Field(ProxyServer.Config.SubMotd)
				.Field(ProxyServer.Config.GameType)
				.Field("1")
				.Field(port.ToString())
				.Field(port.ToString())
				.Field("0")
				.Field("");
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