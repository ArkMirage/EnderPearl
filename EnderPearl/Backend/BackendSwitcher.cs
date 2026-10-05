using System;
using System.Threading;
using EnderPearl.Config;
using global::Protocol.Packets;
using EnderPearl.Core;
using EnderPearl.Player;

namespace EnderPearl.Backend;
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
			while (connection.Client.IsConnected)
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
			if (!connection.Client.IsConnected)
			{
				return;
			}
			Logger.Info(
				$"Giving up on switching {connection.Client.RemoteEndPoint} to backend {backend.Name} after {attempts} attempt(s) over {(ProxyConnection.NanoTime() - startedAtNanos) / 1_000_000L}ms.");
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
		if (!connection.Client.IsConnected)
		{
			return;
		}
		TextPacket packet = ProxyPackets.NewSystemText(message);
		connection.Client.SendPacket(packet);
	}
}
