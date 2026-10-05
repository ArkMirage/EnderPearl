using System;
using System.Collections.Generic;
using EnderPearl.Command;
using EnderPearl.Core;
using global::Protocol.Packets;
using EnderPearl.Backend;
using EnderPearl.Player;

namespace EnderPearl.Relay;

/// <summary>
/// The clientbound leg of an established backend session: every packet the backend sends the player
/// flows through here on its way out. Split across partial class files:
///
/// <list type="bullet">
/// <item>BackendRelayPacketHandler.InitialJoin.cs - internal transfers, input-lock capture and XUID injection</item>
/// <item>BackendRelayPacketHandler.SwitchState.cs - switch-reset world-state capture, respawn acks</item>
/// <item>BackendRelayPacketHandler.Disconnect.cs - backend disconnect interception</item>
/// </list>
/// </summary>
public sealed partial class BackendRelayPacketHandler : PacketHandler
{
	/// <summary>
	/// Bisect switch: forward the backend's command tree untouched, without the proxy's /server and /hub
	/// entries. Diagnostic only; the proxy commands stop working while it is on.
	/// </summary>
	private static readonly bool NO_COMMAND_INJECTION =
		AppContext.TryGetSwitch("proxy.noCommandInjection", out bool noCmd) && noCmd;

	public required ProxyConnection Connection { get; init; }
	public required BackendSession Backend { get; init; }
	public required string BackendName { get; init; }
	public required BackendActivation Activation { get; init; }
	public required AvailableCommandsInjector CommandsInjector { get; init; }
	public required BackendFailover Failover { get; init; }
	public required JoinFailover JoinFailover { get; init; }
	public required BackendSwitcher BackendSwitcher { get; init; }

	private PacketRewriter? rewriter;

	/// <summary>Every clientbound packet rewrite; stateless, so one lazy instance serves this Backend.</summary>
	private PacketRewriter Rewriter => rewriter ??= new PacketRewriter(Connection);

	private uint backendInputLockData;

	/// <summary>
	/// The clientbound pipeline runs in a fixed order: gate, annotate, capture-during-switch-reset, then
	/// forward.
	/// </summary>
	public override PacketSignal Handle(IPacket packet)
	{
		bool pendingStartGame = packet is StartGamePacket && ReferenceEquals(Backend, Connection.PendingBackend());
		if (Gate(packet))
		{
			return PacketSignal.Handled;
		}

		int sourceDimension = Connection.PlayerDimensionId();
		if (pendingStartGame)
		{
			ClearPreviousClientWorldState();
		}
		Annotate(packet);

		if (!pendingStartGame && CaptureDuringSwitchReset(packet))
		{
			return PacketSignal.Handled;
		}
		long unknownRuntimeEntityId = UnknownRuntimeEntityUpdate(packet);
		if (unknownRuntimeEntityId > 0)
		{
			return PacketSignal.Handled;
		}
		ForwardToClient(packet, pendingStartGame, sourceDimension);
		return PacketSignal.Handled;
	}

	/// <summary>
	/// The gates a backend packet can be stopped behind, in priority order. Returns true when the packet was
	/// fully handled here and must not be relayed.
	/// </summary>
	private bool Gate(IPacket packet)
	{
		// An old backend that has already handed the player over must not speak after its replacement.
		if (Connection.PendingBackend() != null && ReferenceEquals(Backend, Connection.Backend()))
		{
			return true;
		}
		if (!IsCurrentBackend())
		{
			return true;
		}
		if (ReferenceEquals(Backend, Connection.PendingBackend()) && AcknowledgePendingSwitchLoginPacket(packet))
		{
			return true;
		}
		if (packet is UpdateClientInputLocksPacket inputLocks
			&& CaptureSwitchInputLocks(inputLocks))
		{
			return true;
		}
		if (packet is TransferPacket transfer && InterceptInternalTransfer(transfer))
		{
			return true;
		}
		// A backend's disconnect reaching the client is an immediate disconnect with no way back, so Failover
		// has to claim it first.
		if (ReferenceEquals(Backend, Connection.Backend()) && InterceptBackendDisconnect(packet))
		{
			return true;
		}
		return false;
	}

	/// <summary>
	/// In-place edits that ride along with specific relayed packets. Runs after the gates and before the
	/// switch-reset capture.
	/// </summary>
	private void Annotate(IPacket packet)
	{
		if (packet is StartGamePacket schemeStartGame)
		{
			// A session fact that decides how the player can be moved from now on; the per-backend half feeds
			// scheme-aware reconnect routing.
			Connection.RememberClientBlockIdsHashed(schemeStartGame.BlockNetworkIdsAreHashes);
			BackendBlockSchemes.Remember(BackendName, schemeStartGame.BlockNetworkIdsAreHashes);
		}
		if (packet is LevelEventPacket levelEvent)
		{
			BackendWeather.Observe(BackendName, levelEvent.EventId);
		}
		SyncDefinitionState(packet);
		if (packet is global::Protocol.Packets.PacketViolationWarningPacket violation)
		{
			// The most informative packet BDS sends: which of our packets it could not read. Print
			// unconditionally - a violation always means the proxy broke.
			Logger.Error(
				$"[S2] PACKET VIOLATION from {BackendName}: type={violation.ViolationType} severity={violation.ViolationSeverity} offendingPacketId={violation.ViolationPacketId} context={violation.ViolationContext}");
		}
		if (packet is AvailableCommandsPacket availableCommands)
		{
			if (!NO_COMMAND_INJECTION)
			{
				CommandsInjector.Inject(availableCommands);
			}
		}
	}

	/// <summary>
	/// Holds world state back while a switch reset has the client in its dimension bounce. Returns true when
	/// the packet was captured (or suppressed) and must not be relayed now.
	/// </summary>
	private bool CaptureDuringSwitchReset(IPacket packet)
	{
		BackendSwitchReset? switchReset = Connection.BackendSwitchResetRef();
		if (switchReset == null
			|| !switchReset.IsActive()
			|| !ReferenceEquals(Backend, Connection.Backend())
			|| !SuppressWorldStateDuringSwitchReset(packet))
		{
			return false;
		}
		if (packet is RespawnPacket resetRespawn)
		{
			AcknowledgeRespawn(resetRespawn.State, resetRespawn.Position);
		}
		// Entity spawns inside the reset window still need the runtime-id rewrite (registering their backend
		// ids) so they can be replayed afterwards.
		if (packet is AddActorPacket || packet is AddItemActorPacket
			|| packet is AddPlayerPacket || packet is AddPaintingPacket)
		{
			Connection.AddDeferredSwitchWorldState(Rewriter.RewriteClientbound(packet, BackendName));
			Connection.ClientWorldState.Track(packet);
			return true;
		}
		CaptureSwitchResetPlayerState(packet);
		CaptureSwitchResetWorldState(packet);
		return true;
	}

	/// <summary>The rewrite-and-send step every surviving packet takes.</summary>
	private void ForwardToClient(IPacket packet, bool pendingStartGame, int sourceDimension)
	{
		IPacket rewritten = Rewriter.RewriteClientbound(packet, BackendName);
		bool sent = SendRewrittenClientbound(rewritten);
		if (sent && rewritten is StartGamePacket)
		{
			// From here on an unexpected backend loss can be turned into a switch rather than a kick.
			Connection.MarkClientJoinedWorld();
			if (!pendingStartGame)
			{
				ReplayKnownBackendWeather();
			}
		}
		Connection.ClientWorldState.Track(packet);
		if (pendingStartGame && packet is StartGamePacket startGame)
		{
			SendSwitchWorldReadyPackets(startGame, sourceDimension);
		}
	}

	private bool IsCurrentBackend()
	{
		return ReferenceEquals(Backend, Connection.Backend()) || ReferenceEquals(Backend, Connection.PendingBackend());
	}

	public override void OnDisconnected(string reason)
	{
		if (Connection.Client.IsConnected)
		{
			Logger.Error(
				$"Backend {BackendName} disconnected player {Connection.ClientLogin.AuthData.DisplayName} unexpectedly: {reason}.");
		}
		// A pending leg dying mid-handshake is reported straight back to whoever is waiting on the switch /
		// join future; Failover does not own that leg yet.
		if (ReferenceEquals(Backend, Connection.PendingBackend()))
		{
			Activation.OnFailure(Backend, new InvalidOperationException(reason));
			return;
		}
		if (ReferenceEquals(Backend, Connection.Backend()) && Connection.Client.IsConnected)
		{
			if (Connection.IsFailingOver())
			{
				Backend.SetDisconnectClientOnClose(false);
				return;
			}
			if (JoinFailover != null && JoinFailover.HandleJoinFailure(Connection, BackendName, reason))
			{
				Backend.SetDisconnectClientOnClose(false);
				return;
			}
			if (disconnectPassedThrough)
			{
				Connection.Client.Disconnect(reason);
				return;
			}
			if (Failover.Begin(Connection, BackendName, reason))
			{
				Backend.SetDisconnectClientOnClose(false);
				return;
			}
			Connection.Client.Disconnect(reason);
		}
	}
}
