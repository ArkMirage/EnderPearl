using System;
using System.Threading;
using EnderPearl.Config;
using global::Protocol.Packets;
using EnderPearl.Logging;

namespace EnderPearl.Backend
{
	/// <summary>
	/// Moves a player to a backend and keeps trying for as long as the retry window allows.
	///
	/// <p>Extracted from the /server handler because /send - and the console - move players too, and a
	/// second copy of the retry-and-lock dance is a second place for it to go wrong. Holds nothing
	/// per-connection, so one instance serves the whole proxy.</p>
	/// </summary>
	public sealed class BackendSwitcher
	{
		private readonly BackendConnector backendConnector;
		private readonly BackendSwitchConfig switchConfig;

		public BackendSwitcher(BackendConnector backendConnector, BackendSwitchConfig? switchConfig)
		{
			this.backendConnector = backendConnector;
			this.switchConfig = switchConfig ?? BackendSwitchConfig.Defaults();
		}

		/// <summary>
		/// Starts a switch, reporting to the player as it goes. Returns false when the switch could not
		/// be started at all - already there, or already switching - in which case the player has been
		/// told why.
		/// </summary>
		public bool SwitchBackend(ProxyConnection connection, BackendConfig backend)
		{
			if (backend.Name.Equals(connection.BackendName() ?? "", StringComparison.OrdinalIgnoreCase))
			{
				SendMessage(connection, "You are already connected to " + backend.Name + ".");
				return false;
			}
			if (connection.BeginBackendSwitch(backend.Name) != ProxyConnection.SwitchStart.STARTED)
			{
				SendMessage(connection, "Already connecting to " + connection.BackendSwitchTarget() + ".");
				return false;
			}

			// Nothing is dialled for a reconnect — the client leaves and comes back on its own — so the
			// switch lock must be released here rather than by an attempt that never runs.
			if (backendConnector.NeedsReconnectToReach(connection, backend))
			{
				connection.FinishBackendSwitch();
				return backendConnector.ReconnectTo(connection, backend);
			}

			SendMessage(connection, "Connecting to " + backend.Name + "...");
			// The dial-out blocks, which must not run on a packet-reading thread.
			var thread = new Thread(() => AttemptSwitch(connection, backend))
			{
				Name = "backend-switch-" + backend.Name,
				IsBackground = true
			};
			thread.Start();
			return true;
		}

		/// <summary>
		/// Keeps retrying the same backend in the background until the retry window elapses. Retries are
		/// silent - they hear one message if it eventually works and one if it does not. The switch lock
		/// is held across the whole window and released once, at the end.
		/// </summary>
		private void AttemptSwitch(ProxyConnection connection, BackendConfig backend)
		{
			long startedAtNanos = ProxyConnection.NanoTime();
			long deadlineNanos = startedAtNanos + switchConfig.RetryWindowMillis * 1_000_000L;
			long retryDelayNanos = switchConfig.RetryDelayMillis * 1_000_000L;
			bool switched = false;
			int attempts = 0;
			try
			{
				while (connection.Client().IsConnected)
				{
					attempts++;
					if (BackendSwitchAttempt.Run(backendConnector, connection, backend, switchConfig.TimeoutMillis))
					{
						switched = true;
						return;
					}
					// Include the pause in the check, so we never sleep only to give up on waking.
					if (ProxyConnection.NanoTime() + retryDelayNanos >= deadlineNanos)
					{
						break;
					}
					if (!Sleep(switchConfig.RetryDelayMillis))
					{
						return;
					}
				}
				if (!connection.Client().IsConnected)
				{
					return;
				}
				Logger.Info(
					$"Giving up on switching {connection.Client().RemoteEndPoint} to backend {backend.Name} after {attempts} attempt(s) over {(ProxyConnection.NanoTime() - startedAtNanos) / 1_000_000L}ms.");
				SendMessage(connection,
					$"Could not connect to {backend.Name}. You are still on {connection.BackendName()}.");
			}
			finally
			{
				// On success the lock was already cleared by setBackend, and clearing it again could
				// stamp on a switch the player has started since.
				if (!switched)
				{
					connection.FinishBackendSwitch();
				}
			}
		}

		private static bool Sleep(long millis)
		{
			if (millis <= 0)
			{
				return true;
			}
			try
			{
				Thread.Sleep((int)millis);
				return true;
			}
			catch (ThreadInterruptedException)
			{
				Thread.CurrentThread.Interrupt();
				return false;
			}
		}

		public static void SendMessage(ProxyConnection connection, string message)
		{
			if (!connection.Client().IsConnected)
			{
				return;
			}
			TextPacket packet = Messages.NewSystemText(message);
			connection.Client().SendPacket(packet);
		}
	}

	/// <summary>Shared builders for the few packets the proxy itself originates.</summary>
	internal static class Messages
	{
		/// <summary>
		/// How the proxy talks to a player. Protocol 2168 carries only Raw/Chat/Translate text bodies;
		/// server notices are Raw (the modern encoding of what older protocols called a system message).
		/// </summary>
		public static TextPacket NewSystemText(string message)
		{
			var packet = new TextPacket();
			packet.MessageType = global::Protocol.TextPacketType.Raw;
			packet.Localize = false;
			packet.Body = OneOf.OneOf<global::Protocol.Types.TextPacketPayload.MessageOnly, global::Protocol.Types.TextPacketPayload.AuthorAndMessage, global::Protocol.Types.TextPacketPayload.MessageAndParams>.FromT0(
				new global::Protocol.Types.TextPacketPayload.MessageOnly
				{
					MessageType = global::Protocol.TextPacketType.Raw,
					Message = message
				});
			packet.SenderSXUID = "";
			packet.PlatformId = "";
			return packet;
		}

		/// <summary>The kick text a DisconnectPacket carries, or "" when the message was skipped.</summary>
		public static string DisconnectMessage(DisconnectPacket packet)
		{
			return packet.Messages.Index == 0 && packet.Messages.AsT0 != null
				? packet.Messages.AsT0.Message ?? ""
				: "";
		}

		public static string DisconnectFilteredMessage(DisconnectPacket packet)
		{
			return packet.Messages.Index == 0 && packet.Messages.AsT0 != null
				? packet.Messages.AsT0.FilteredMessage ?? ""
				: "";
		}

		/// <summary>Whether the backend sent kick text of its own (false = host-level disconnect).</summary>
		public static bool DisconnectHasMessage(DisconnectPacket packet)
		{
			// Java tested !message.isBlank(): a whitespace-only message counts as "no message", which
			// decides whether a kick is treated as a deliberate ban (pass through) or a host failure
			// (failover candidate).
			return packet.Messages.Index == 0 && !string.IsNullOrWhiteSpace(packet.Messages.AsT0?.Message);
		}
	}
}
