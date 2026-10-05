using System;
using System.Collections.Generic;
using System.Threading;
using EnderPearl.Config;
using global::Protocol.Packets;
using EnderPearl.Core;
using EnderPearl.Player;

namespace EnderPearl.Backend;
/// <summary>
/// Velocity-style failover: when the backend a player is on goes away, walk their configured
/// fallback chain and move them to the first backend that accepts them, instead of dropping them
/// off the proxy.
///
/// <p>This deliberately reuses the ordinary backend-switch path, so an unexpected backend loss looks
/// to the client exactly like /hub - a loading screen and then the fallback world.</p>
/// </summary>
public sealed class BackendFailover
{
	/// <summary>How long a single fallback attempt may take before it is abandoned.</summary>
	private const long ATTEMPT_TIMEOUT_MILLIS = 20_000;

	private readonly BackendDirectory backendDirectory;
	private readonly BackendConnector backendConnector;
	private readonly FailoverConfig failoverConfig;

	public BackendFailover(
		BackendDirectory backendDirectory,
		BackendConnector backendConnector,
		FailoverConfig? failoverConfig
	)
	{
		this.backendDirectory = backendDirectory;
		this.backendConnector = backendConnector;
		this.failoverConfig = failoverConfig ?? FailoverConfig.Disabled();
		WarnAboutUnknownTargets();
	}

	/// <summary>
	/// Whether a backend that disconnects a player should be treated as an outage to rescue them from.
	/// Under the default auto policy this is decided per disconnect, by whether the backend bothered to
	/// write a message.
	/// </summary>
	public bool FailsOverOnBackendDisconnect(bool backendSuppliedMessage)
	{
		return failoverConfig.OnBackendDisconnect.FailsOver(backendSuppliedMessage);
	}

	/// <summary>
	/// Resolves the ordered fallback backends for a player who has just lost a backend. Names that
	/// are not configured backends are skipped rather than failing the whole chain.
	/// </summary>
	public static List<BackendConfig> Targets(
		FailoverConfig failoverConfig,
		BackendDirectory backendDirectory,
		string lostBackendName
	)
	{
		var targets = new List<BackendConfig>();
		var seen = new ConfigValues.LinkedHashSet<string>();
		foreach (string name in failoverConfig.FallbacksFor(lostBackendName))
		{
			BackendConfig? backend = backendDirectory.Find(name);
			if (backend != null && seen.Add(backend.Name.ToLowerInvariant()))
			{
				targets.Add(backend);
			}
		}
		return targets;
	}

	/// <summary>Takes over an unexpected backend loss, if failover applies to it.</summary>
	/// <returns>true when this method has taken responsibility for the client.</returns>
	public bool Begin(ProxyConnection connection, string lostBackendName, string reason)
	{
		if (!failoverConfig.Enabled || !connection.Client.IsConnected)
		{
			return false;
		}
		if (!connection.HasClientJoinedWorld())
		{
			// The client has no world to be moved out of yet; a switch cannot represent this.
			return false;
		}
		if (connection.IsSwitchingBackend() || connection.PendingBackend() != null)
		{
			// A switch is already in flight and will replace this backend on its own.
			return false;
		}
		List<BackendConfig> targets = Targets(failoverConfig, backendDirectory, lostBackendName);
		if (targets.Count == 0)
		{
			Logger.Info(
				$"No failover target configured for backend {lostBackendName}; disconnecting {connection.Client.RemoteEndPoint}.");
			return false;
		}
		ProxyConnection.FailoverStart start = connection.BeginFailover();
		if (start != ProxyConnection.FailoverStart.STARTED)
		{
			Logger.Info(
				$"Not failing {connection.Client.RemoteEndPoint} over from backend {lostBackendName}: " +
				(start == ProxyConnection.FailoverStart.TOO_MANY
					? "too many failovers in a row, the fallbacks are dropping the player as fast as they arrive"
					: "a failover is already running") + ".");
			return false;
		}

		// A switch reset still driving the backend that just died must not complete: it would hand
		// its post-switch initialization token to a dead session and strand the player.
		BackendSwitchReset? staleReset = connection.BackendSwitchResetRef();
		staleReset?.Abandon(connection);

		// The dial-out blocks; this runs on the dead backend's read thread.
		var thread = new Thread(() => Run(connection, lostBackendName, reason, targets))
		{
			Name = "backend-failover-" + lostBackendName,
			IsBackground = true
		};
		thread.Start();
		return true;
	}

	private void Run(ProxyConnection connection, string lostBackendName, string reason, List<BackendConfig> targets)
	{
		try
		{
			var names = new List<string>();
			foreach (BackendConfig target in targets)
			{
				names.Add(target.Name);
			}
			Logger.Info(
				$"Backend {lostBackendName} died under {connection.Client.RemoteEndPoint} ({reason}); failing over through [{string.Join(", ", names)}].");
			BackendSwitcher.SendMessage(connection, "Lost connection to " + lostBackendName + ".");
			foreach (BackendConfig target in targets)
			{
				if (!connection.Client.IsConnected)
				{
					return;
				}
				BackendSwitcher.SendMessage(connection, "Moving you to " + target.Name + "...");
				if (Attempt(connection, target))
				{
					Logger.Info(
						$"Failed over {connection.Client.RemoteEndPoint} from backend {lostBackendName} to {target.Name}.");
					return;
				}
			}
			Logger.Info(
				$"Failover exhausted for {connection.Client.RemoteEndPoint} after losing backend {lostBackendName}; no fallback of [{string.Join(", ", names)}] accepted the player.");
			if (connection.Client.IsConnected)
			{
				connection.Client.Disconnect(reason);
			}
		}
		catch (Exception exception)
		{
			Logger.Error(
				$"Failover for {connection.Client.RemoteEndPoint} after losing backend {lostBackendName} failed unexpectedly: {exception}.");
			if (connection.Client.IsConnected)
			{
				connection.Client.Disconnect(reason);
			}
		}
		finally
		{
			connection.FinishFailover();
		}
	}

	/// <summary>
	/// Tries one candidate. The switch lock is taken and released per candidate rather than held for
	/// the whole chain, because each candidate is a switch to a different backend.
	/// </summary>
	private bool Attempt(ProxyConnection connection, BackendConfig target)
	{
		if (connection.BeginBackendSwitch(target.Name) != ProxyConnection.SwitchStart.STARTED)
		{
			Logger.Info(
				$"Skipping failover target {target.Name}: a switch to {connection.BackendSwitchTarget()} is already in progress.");
			return false;
		}
		if (BackendSwitchAttempt.Run(backendConnector, connection, target, ATTEMPT_TIMEOUT_MILLIS))
		{
			return true;
		}
		connection.FinishBackendSwitch();
		return false;
	}

	private void WarnAboutUnknownTargets()
	{
		if (!failoverConfig.Enabled)
		{
			Logger.Info("Backend failover is disabled; players are disconnected when their backend dies.");
			return;
		}
		var configured = new ConfigValues.LinkedHashSet<string>();
		foreach (string name in failoverConfig.Fallbacks)
		{
			configured.Add(name);
		}
		foreach (List<string> overrides in failoverConfig.BackendFallbacks.Values)
		{
			foreach (string name in overrides)
			{
				configured.Add(name);
			}
		}
		foreach (string name in configured)
		{
			if (backendDirectory.Find(name) == null)
			{
				Logger.Info($"WARNING: failover target '{name}' is not a configured backend and will be skipped.");
			}
		}
	}
}
