using System;
using EnderPearl.Config;
using global::Protocol.Packets;
using EnderPearl.Core;
using EnderPearl.Backend;
using EnderPearl.Server;

namespace EnderPearl.Relay
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
			if (ReferenceEquals(Backend, Connection.PendingBackend()))
			{
				if (Connection.IsPacketTraceActive())
				{
					Logger.Info(
						$"Captured pre-StartGame input locks for pending backend {BackendName}: mask={backendInputLockData}.");
				}
				return true;
			}
			BackendSwitchReset? switchReset = Connection.BackendSwitchResetRef();
			if (switchReset == null || !switchReset.IsActive() || !ReferenceEquals(Backend, Connection.Backend()))
			{
				return false;
			}
			switchReset.RememberTargetInputLocks(backendInputLockData);
			if (Connection.IsPacketTraceActive())
			{
				Logger.Info(
					$"Captured input locks for backend {BackendName} during switch reset: mask={backendInputLockData}.");
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
			if (!ReferenceEquals(Backend, Connection.Backend())
				|| !Connection.HasClientJoinedWorld()
				|| BackendSwitcher == null)
			{
				return false;
			}
			BackendConfig? target = ProxyServer.BackendDirectory.FindByAddress(transfer.ServerAddress, transfer.ServerPort);
			if (target == null)
			{
				return false;
			}

			Logger.Info(
				$"Intercepting backend transfer for {Connection.ClientLogin.AuthData.DisplayName} from {BackendName} to configured backend {target.Name} ({transfer.ServerAddress}:{transfer.ServerPort}).");
			if (!BackendSwitcher.SwitchBackend(Connection, target))
			{
				Logger.Info(
					$"Internal transfer for {Connection.ClientLogin.AuthData.DisplayName} to backend {target.Name} was consumed without starting a new switch; "
						+ $"current={Connection.BackendName()} pending={Connection.BackendSwitchTarget()}.");
			}
			// Once an endpoint is known to this proxy, never tell the client to reconnect to it
			// directly. Doing so would be slower and could bypass backend verification.
			return true;
		}

		private bool SendRewrittenClientbound(IPacket packet, long traceSequence)
		{
			InjectVerifiedXuids(packet);
			Connection.Client.SendPacket(packet);
			if (traceSequence > 0)
			{
				Logger.Info(
					$"Forwarded clientbound #{traceSequence} +{Connection.ElapsedMillis()}ms backend={BackendName} {packet.GetType().Name} clientConnected={Connection.Client.IsConnected} backendConnected={Backend.IsConnected}.");
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
					verified = ProxyServer.ConnectedPlayers.XuidByName(name);
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
