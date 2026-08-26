using System;
using System.Threading;
using System.Threading.Tasks;
using EnderPearl.Config;
using EnderPearl.Logging;

namespace EnderPearl.Backend
{
	/// <summary>
	/// Runs one backend switch to completion and reports whether the player actually arrived.
	///
	/// <p>Shared by /server and by failover so the two cannot drift on cleanup. The outcome of a switch
	/// is asynchronous - success arrives with the target's StartGame on its read thread - so this waits
	/// for it rather than assuming the dial-out result is the answer.</p>
	///
	/// <p>Deliberately does NOT touch the connection's switch lock. Whether a failed attempt ends the
	/// switch or is followed by another one is the caller's decision.</p>
	/// </summary>
	public static class BackendSwitchAttempt
	{
		/// <summary>How often the wait wakes up to notice that the player has quit.</summary>
		private const long POLL_MILLIS = 250;

		public static bool Run(
			BackendConnector connector,
			ProxyConnection connection,
			BackendConfig target,
			long timeoutMillis
		)
		{
			Logger.Info($"Switch attempt to backend {target.Name} started (timeout {timeoutMillis}ms).");
			Task switched = connector.ConnectForSwitch(connection, target);
			long deadline = ProxyConnection.NanoTime() + timeoutMillis * 1_000_000L;
			while (true)
			{
				try
				{
					switched.Wait(TimeSpan.FromMilliseconds(POLL_MILLIS));
					if (switched.IsCompletedSuccessfully)
					{
						return true;
					}
					if (switched.IsFaulted)
					{
						Exception cause = switched.Exception?.InnerException ?? switched.Exception!;
						return Abandon(connection, target, cause.ToString());
					}
					if (switched.IsCanceled)
					{
						return Abandon(connection, target, "the switch was cancelled");
					}
					// Still running: fall through to the poll checks below.
					if (!connection.Client().IsConnected)
					{
						return Abandon(connection, target, "the player disconnected");
					}
					if (ProxyConnection.NanoTime() >= deadline)
					{
						return Abandon(
							connection,
							target,
							"it did not finish its handshake within " + timeoutMillis + "ms"
						);
					}
				}
				catch (AggregateException exception)
				{
					Exception cause = exception.InnerException ?? exception;
					return Abandon(connection, target, cause.ToString());
				}
				catch (ThreadInterruptedException)
				{
					Thread.CurrentThread.Interrupt();
					return Abandon(connection, target, "the switch was interrupted");
				}
			}
		}

		/// <summary>
		/// Tears down a failed attempt. Always called on failure: a target that is simply not listening
		/// never gets far enough for the cleanup inside ConnectForSwitch to run.
		/// </summary>
		private static bool Abandon(ProxyConnection connection, BackendConfig target, string why)
		{
			Logger.Info($"Abandoning switch to backend {target.Name}: {why}.");
			BackendSession? pending = connection.PendingBackend();
			if (pending != null)
			{
				connection.ClearPendingBackend(pending);
				if (pending.IsConnected)
				{
					pending.SetDisconnectClientOnClose(false);
					pending.DiscardInboundPackets();
					pending.Disconnect("Switch to " + target.Name + " abandoned");
				}
			}
			return false;
		}
	}
}
