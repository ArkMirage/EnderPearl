using System;
using System.Collections.Generic;
using System.Threading;
using EnderPearl.Config;
using EnderPearl.Logging;

namespace EnderPearl.Backend
{
	/// <summary>
	/// Walks the join try-list when the backend a player is being connected to will not have them.
	///
	/// <p>Distinct from <see cref="BackendFailover"/>, which moves a client that is already in a world.
	/// Before StartGame there is nothing to move - the client has never been given a world - so the only
	/// thing that can be done is to start the connection again against the next candidate.</p>
	///
	/// <p>A single dead backend is reported through several paths at once. All of them route here, and
	/// <see cref="ProxyConnection.ClaimJoinFailure"/> makes sure exactly one of them advances the
	/// sequence.</p>
	/// </summary>
	public sealed class JoinFailover
	{
		private readonly BackendConnector backendConnector;

		internal JoinFailover(BackendConnector backendConnector)
		{
			this.backendConnector = backendConnector;
		}

		/// <summary>
		/// Returns true when the caller must NOT disconnect the client: either the next candidate is
		/// being tried, or the player has already been told the network is down.
		/// </summary>
		internal bool HandleJoinFailure(ProxyConnection connection, string failedBackendName, string reason)
		{
			if (!connection.IsJoinSequenceActive() || connection.HasClientJoinedWorld())
			{
				// Not a join failure. A backend dying under a player who is already in a world belongs
				// to BackendFailover, and the caller's own handling is correct.
				return false;
			}
			if (!connection.Client().IsConnected)
			{
				connection.EndJoinSequence();
				return true;
			}
			if (!connection.ClaimJoinFailure())
			{
				// Another path got here first for this same attempt.
				return true;
			}

			BackendConfig? next = connection.NextJoinCandidate();
			if (next == null)
			{
				connection.EndJoinSequence();
				Logger.Info(
					$"No backend accepted {connection.ClientLogin.AuthData.DisplayName} at join; last was {failedBackendName} ({reason}).");
				connection.Client().Disconnect("All servers are offline. Please try again shortly.");
				return true;
			}

			Logger.Info(
				$"Backend {failedBackendName} would not take {connection.ClientLogin.AuthData.DisplayName} at join ({reason}); trying {next.Name}.");
			// connectInternal blocks on its dial-out; never run that on a packet-reading thread.
			var thread = new Thread(() => Attempt(connection, next))
			{
				Name = "join-failover-" + next.Name,
				IsBackground = true
			};
			thread.Start();
			return true;
		}

		private void Attempt(ProxyConnection connection, BackendConfig backend)
		{
			try
			{
				backendConnector.Connect(connection, backend);
			}
			catch (Exception exception)
			{
				// connect() reports through the activation, which comes back here; this only covers a
				// throw that never reached it.
				HandleJoinFailure(connection, backend.Name, exception.Message);
			}
		}
	}
}
