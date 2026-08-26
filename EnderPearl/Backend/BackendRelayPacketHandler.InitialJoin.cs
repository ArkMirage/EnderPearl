using System;
using EnderPearl.Config;
using global::Protocol.Packets;
using EnderPearl.Logging;

namespace EnderPearl.Backend
{
	/// <summary>
	/// Internal transfer interception and proxy-verified XUID injection
	/// (port of BackendRelayPacketHandler.java lines 816-1270).
	/// </summary>
	public sealed partial class BackendRelayPacketHandler
	{
		private bool CaptureSwitchInputLocks(UpdateClientInputLocksPacket inputLocks)
		{
			backendInputLockData = inputLocks.InputLockComponentData;
			if (ReferenceEquals(backend, connection.PendingBackend()))
			{
				if (connection.IsPacketTraceActive())
				{
					Logger.Info(
						$"Captured pre-StartGame input locks for pending backend {backendName}: mask={backendInputLockData}.");
				}
				return true;
			}
			BackendSwitchReset? switchReset = connection.BackendSwitchResetRef();
			if (switchReset == null || !switchReset.IsActive() || !ReferenceEquals(backend, connection.Backend()))
			{
				return false;
			}
			switchReset.RememberTargetInputLocks(backendInputLockData);
			if (connection.IsPacketTraceActive())
			{
				Logger.Info(
					$"Captured input locks for backend {backendName} during switch reset: mask={backendInputLockData}.");
			}
			return true;
		}

		/// <summary>
		/// Turns a backend's transfer to another configured backend into an in-proxy handoff. External
		/// destinations return <c>false</c> and continue through the ordinary relay path unchanged.
		/// </summary>
		private bool InterceptInternalTransfer(TransferPacket transfer)
		{
			// The switch path preserves an existing client world. Before the first StartGame there is
			// no world to reset, so retain vanilla transfer behaviour for early-login redirects.
			if (!ReferenceEquals(backend, connection.Backend())
				|| !connection.HasClientJoinedWorld()
				|| backendDirectory == null
				|| backendSwitcher == null)
			{
				return false;
			}
			BackendConfig? target = backendDirectory.FindByAddress(transfer.ServerAddress, transfer.ServerPort);
			if (target == null)
			{
				return false;
			}

			Logger.Info(
				$"Intercepting backend transfer for {connection.ClientLogin.AuthData.DisplayName} from {backendName} to configured backend {target.Name} ({transfer.ServerAddress}:{transfer.ServerPort}).");
			if (!backendSwitcher.SwitchBackend(connection, target))
			{
				Logger.Info(
					$"Internal transfer for {connection.ClientLogin.AuthData.DisplayName} to backend {target.Name} was consumed without starting a new switch; "
						+ $"current={connection.BackendName()} pending={connection.BackendSwitchTarget()}.");
			}
			// Once an endpoint is known to this proxy, never tell the client to reconnect to it
			// directly. Doing so would be slower and could bypass backend verification.
			return true;
		}

		private bool SendTranslatedClientbound(IPacket translated, string originalName, long traceSequence)
		{
			InjectVerifiedXuids(translated);
			// The Java original retained the translated buffer for the peer when it was freshly
			// generated; these packets are plain managed objects, so sending them as-is is the whole job.
			connection.Client().SendPacket(translated);
			if (traceSequence > 0)
			{
				Logger.Info(
					$"Forwarded clientbound #{traceSequence} +{connection.ElapsedMillis()}ms backend={backendName} original={originalName} translated={translated.GetType().Name} clientConnected={connection.Client().IsConnected} backendConnected={backend.IsConnected}.");
			}
			return true;
		}

		/// <summary>
		/// Substitutes proxy-verified XUIDs into outgoing PlayerListPacket entries. BDS
		/// 1.26.10+ in offline mode does not trust self-signed OIDC <c>xid</c> claims, so the
		/// backend's outgoing PlayerListPacket has empty xuid fields. We have the real
		/// XUID for every connected proxy client (from their Mojang-signed login chain)
		/// and inject it here so the client-side friends tab and any xuid-keyed lookups
		/// still work.
		/// </summary>
		private void InjectVerifiedXuids(IPacket packet)
		{
			if (!(packet is PlayerListPacket playerList))
			{
				return;
			}
			if (playerList.Action != global::Protocol.PlayerListPacketType.Add)
			{
				return;
			}
			foreach (var entry in playerList.Entries)
			{
				if (!entry.TryPickT1(out global::Protocol.Types.PlayerListPacketPayload.AddEntry? listEntry)
					|| listEntry == null)
				{
					continue;
				}
				string? existing = listEntry.XBLXUID;
				if (!string.IsNullOrWhiteSpace(existing) && existing != "0")
				{
					continue;
				}
				string? name = listEntry.PlayerName;
				if (name == null)
				{
					continue;
				}
				string? verified;
				try
				{
					verified = verifiedXuidLookup(name);
				}
				catch (Exception)
				{
					verified = null;
				}
				if (!string.IsNullOrWhiteSpace(verified))
				{
					listEntry.XBLXUID = verified;
				}
			}
		}
	}
}
