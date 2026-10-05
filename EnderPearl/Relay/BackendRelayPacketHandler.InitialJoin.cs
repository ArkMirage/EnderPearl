using System;
using EnderPearl.Config;
using global::Protocol.Packets;
using EnderPearl.Core;
using EnderPearl.Backend;
using EnderPearl.Server;

namespace EnderPearl.Relay;

/// <summary>Internal transfer interception, input-lock capture and proxy-verified XUID injection.</summary>
public sealed partial class BackendRelayPacketHandler
{
	private bool CaptureSwitchInputLocks(UpdateClientInputLocksPacket inputLocks)
	{
		backendInputLockData = inputLocks.InputLockComponentData;
		if (ReferenceEquals(Backend, Connection.PendingBackend()))
		{
			return true;
		}
		BackendSwitchReset? switchReset = Connection.BackendSwitchResetRef();
		if (switchReset == null || !switchReset.IsActive() || !ReferenceEquals(Backend, Connection.Backend()))
		{
			return false;
		}
		switchReset.RememberTargetInputLocks(backendInputLockData);
		return true;
	}

	/// <summary>
	/// Turns a backend's transfer to another configured backend into an in-proxy handoff. External
	/// destinations return <c>false</c> and continue through the ordinary relay path unchanged.
	/// </summary>
	private bool InterceptInternalTransfer(TransferPacket transfer)
	{
		// The switch path preserves an existing client world. Before the first StartGame there is no world
		// to reset, so retain vanilla transfer behaviour for early-login redirects.
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
		// Once an endpoint is known to this proxy, never tell the client to reconnect to it directly.
		return true;
	}

	private bool SendRewrittenClientbound(IPacket packet)
	{
		InjectVerifiedXuids(packet);
		Connection.Client.SendPacket(packet);
		return true;
	}

	/// <summary>
	/// Substitutes proxy-verified XUIDs into outgoing PlayerListPacket entries. BDS 1.26.10+ in offline mode
	/// does not trust self-signed OIDC <c>xid</c> claims, so the backend's outgoing PlayerListPacket has empty
	/// xuid fields. The proxy has the real XUID for every connected client (from their Mojang-signed login
	/// chain) and injects it so the client-side friends tab and any xuid-keyed lookups still work.
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
