using System;
using System.Collections.Generic;
using EnderPearl.Command;
using EnderPearl.Core;
using global::Protocol.Packets;
using global::Protocol.Types;
using EnderPearl.Backend;
using EnderPearl.Player;

namespace EnderPearl.Relay
{
	/// <summary>
	/// The clientbound leg of an established backend session: every packet the backend sends the
	/// player flows through here on its way out. Split across partial class files:
	///
	/// <list type="bullet">
	/// <item>BackendRelayPacketHandler.Diagnostics.cs - diagnostic drop/neuter switches</item>
	/// <item>BackendRelayPacketHandler.InitialJoin.cs - internal transfers and XUID injection</item>
	/// <item>BackendRelayPacketHandler.SwitchState.cs - switch-reset world-state capture, respawn acks</item>
	/// <item>BackendRelayPacketHandler.Disconnect.cs - backend disconnect interception</item>
	/// </list>
	/// </summary>
	public sealed partial class BackendRelayPacketHandler : PacketHandler
	{
		/// <summary>
		/// Bisect switch: forward the backend's command tree untouched, without the proxy's /server and
		/// /hub entries. Diagnostic only; the proxy commands stop working while it is on.
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
		/// <summary>Packs being assembled from bytes on their way to the client; see CaptureBackendPackBytes.</summary>

		/// <summary>
		/// The clientbound pipeline: every packet the backend sends the player. The stages below run in
		/// a fixed order and each one documents the only thing it is allowed to do, so a new concern has
		/// exactly one place to go:
		///
		/// <list type="number">
		/// <item><see cref="Gate"/> - decide the packet never reaches the client at all (stale backend,
		/// pending-switch acks, disconnect interception, diagnostics).</item>
		/// <item><see cref="Annotate"/> - tracing, and the in-place edits + log lines that ride along
		/// with specific relayed packets (command-tree injection, definition sync, block-scheme capture).</item>
		/// <item><see cref="CaptureDuringSwitchReset"/> - hold world state back while a switch reset
		/// bounces the client through a fake dimension.</item>
		/// <item><see cref="ForwardToClient"/> - the rewrite-and-send step every surviving packet takes.</item>
		/// </list>
		/// </summary>
		public override PacketSignal Handle(IPacket packet)
		{
			bool pendingStartGame = packet is StartGamePacket && ReferenceEquals(Backend, Connection.PendingBackend());
			if (Gate(packet))
			{
				return PacketSignal.Handled;
			}

			long traceSequence = -1;
			if (Connection.IsPacketTraceActive())
			{
				traceSequence = Connection.NextClientboundTraceSequence();
				Logger.Info(
					$"Trace clientbound #{traceSequence} +{Connection.ElapsedMillis()}ms from backend {BackendName}: {packet.GetType().Name} current={ReferenceEquals(Backend, Connection.Backend())} pending={ReferenceEquals(Backend, Connection.PendingBackend())} switchReset={Connection.BackendSwitchResetRef() != null}.");
				LogClientboundDetails(packet);
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
				if (Connection.IsPacketTraceActive())
				{
					Logger.Info(
						$"Dropping clientbound entity update from backend {BackendName} for unknown runtimeEntityId={unknownRuntimeEntityId}: {packet.GetType().Name}.");
				}
				return PacketSignal.Handled;
			}
			ForwardToClient(packet, traceSequence, pendingStartGame, sourceDimension);
			return PacketSignal.Handled;
		}

		/// <summary>
		/// The gates a backend packet can be stopped behind, in priority order. Returns true when the
		/// packet was fully handled here and must not be relayed.
		/// </summary>
		private bool Gate(IPacket packet)
		{
			// An old backend that has already handed the player over must not speak after its
			// replacement; anything it still sends belongs to a world the client has left.
			if (Connection.PendingBackend() != null && ReferenceEquals(Backend, Connection.Backend()))
			{
				if (Connection.IsPacketTraceActive())
				{
					Logger.Info(
						$"Dropping old-backend packet from {BackendName} during pending switch: {packet.GetType().Name}.");
				}
				return true;
			}
			if (!IsCurrentBackend())
			{
				if (Connection.IsPacketTraceActive())
				{
					Logger.Info(
						$"Dropping stale packet from backend {BackendName} after handoff: {packet.GetType().Name}.");
				}
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
			if (IsSuppressedForDiagnostics(packet))
			{
				return true;
			}
			NeuterForDiagnostics(packet);
			// Must come before anything that could forward the packet: a backend's disconnect reaching
			// the client is an immediate disconnect with no way back, so Failover has to claim it first.
			if (ReferenceEquals(Backend, Connection.Backend()) && InterceptBackendDisconnect(packet))
			{
				return true;
			}
			return false;
		}

		/// <summary>
		/// In-place edits and log lines that ride along with specific relayed packets. Runs after the
		/// gates and before the switch-reset capture, exactly where the relay used to inline them.
		/// </summary>
		private void Annotate(IPacket packet)
		{
			if (packet is StartGamePacket schemeStartGame)
			{
				// The client keeps whichever scheme its first StartGame carried - a session fact that
				// decides how they can be moved from now on; the per-backend half feeds scheme-aware
				// reconnect routing.
				Connection.RememberClientBlockIdsHashed(schemeStartGame.BlockNetworkIdsAreHashes);
				BackendBlockSchemes.Remember(BackendName, schemeStartGame.BlockNetworkIdsAreHashes);
			}
			if (packet is LevelEventPacket levelEvent)
			{
				// Weather is the one world property BDS announces only when it changes, so remembering what
				// this backend last said is the only way a client entering later can be put back in sync.
				BackendWeather.Observe(BackendName, levelEvent.EventId);
			}
			SyncDefinitionState(packet);
			// Single-version build: client and backend codecs are identical, so there is no
			// cross-protocol drop list to consult (isCrossProtocol() is always false).
			if (packet is DeathInfoPacket)
			{
				TracePacketsWithReason("DeathInfoPacket");
			}
			if (packet is global::Protocol.Packets.PacketViolationWarningPacket violation)
			{
				// The single most informative packet BDS sends: which of our packets it could not read,
				// and its own parser error. Print unconditionally - a violation always means we broke.
				// Java has no branch for this type, so after logging it is relayed onward like any
				// other clientbound packet.
				Logger.Error(
					$"[S2] PACKET VIOLATION from {BackendName}: type={violation.ViolationType} severity={violation.ViolationSeverity} offendingPacketId={violation.ViolationPacketId} context={violation.ViolationContext}");
			}
			if (packet is AvailableCommandsPacket availableCommands)
			{
				int before = availableCommands.Commands.Count;
				if (!NO_COMMAND_INJECTION)
				{
					CommandsInjector.Inject(availableCommands);
				}
				int after = availableCommands.Commands.Count;
				if (Connection.IsPacketTraceActive())
				{
					Logger.Info(
						$"Forwarding command tree from backend {BackendName}: {before} native commands, {after} total after proxy injection.");
				}
				TracePacketsWithReason("AvailableCommands");
			}
			if (packet is CommandOutputPacket commandOutput)
			{
				if (Connection.IsPacketTraceActive())
				{
					Logger.Info(
						$"Forwarding backend command output from {BackendName}: successCount={commandOutput.Output?.SuccessCount ?? 0} messages={commandOutput.Output?.OutputMessages?.Count ?? 0}.");
				}
			}
			// Note: this codec's framing layer drops undecodable packet ids before they reach a handler,
			// so the Java handler's UnknownPacket branch has no counterpart here.
			if (packet is UpdatePlayerGameTypePacket updateGameType)
			{
				if (Connection.IsPacketTraceActive())
				{
					Logger.Info(
						$"Forwarding UpdatePlayerGameType from backend {BackendName}: gameType={updateGameType.PlayerGameType} tick={updateGameType.Tick?.InputTick ?? 0}.");
				}
			}
			else if (packet is SetPlayerGameTypePacket setGameType)
			{
				if (Connection.IsPacketTraceActive())
				{
					Logger.Info(
						$"Forwarding SetPlayerGameType from backend {BackendName}: gamemode={setGameType.PlayerGameType}.");
				}
			}
			else if (packet is DisconnectPacket disconnect)
			{
				if (Connection.IsPacketTraceActive())
				{
					Logger.Info(
						$"Forwarding backend disconnect packet from {BackendName}: reason={disconnect.Reason} skipped={disconnect.Messages.Index == 1} message={ProxyPackets.DisconnectMessage(disconnect)} filtered={ProxyPackets.DisconnectFilteredMessage(disconnect)}.");
				}
			}
		}

		/// <summary>Opens a detailed-trace window after a packet known to precede interesting traffic.</summary>
		private void TracePacketsWithReason(string afterPacket)
		{
			Connection.TracePacketsForMillis(ProxyConnection.ConfiguredPacketTraceMillis());
			if (ProxyConnection.ConfiguredPacketTraceMillis() > 0)
			{
				Logger.Info(
					$"Enabled detailed packet trace for {Connection.Client.RemoteEndPoint} for {ProxyConnection.ConfiguredPacketTraceMillis()}ms after {afterPacket} at +{Connection.ElapsedMillis()}ms.");
			}
		}

		/// <summary>
		/// Holds world state back while a switch reset has the client in its dimension bounce. Returns
		/// true when the packet was captured (or suppressed) and must not be relayed now.
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
			// Entity spawns arriving inside the reset window must still go through the runtime-id
			// rewrite (which registers their backend ids) and be replayed afterwards - dropping
			// them unregistered left every mob/item near the spawn point permanently unknown, so
			// all their later updates were dropped as "unknown runtimeEntityId" and the whole area
			// was full of invisible entities the client kept interacting with.
			if (packet is AddActorPacket || packet is AddItemActorPacket
				|| packet is AddPlayerPacket || packet is AddPaintingPacket)
			{
				Connection.AddDeferredSwitchWorldState(Rewriter.RewriteClientbound(packet, BackendName));
				// Same bookkeeping as the forward path so a future switch's world cleanup knows
				// about these entities: entity unique ids above stay pre-rewrite, while link
				// endpoints arrive already swapped to client-facing ids - exactly what the
				// cleanup packets sent straight to the client must name (WaterdogPE tracks
				// after its rewrite for the same reason).
				Connection.ClientWorldState.Track(packet);
				if (Connection.IsPacketTraceActive())
				{
					Logger.Info(
						$"Deferring clientbound entity spawn from backend {BackendName} during switch reset: {packet.GetType().Name}.");
				}
				return true;
			}
			CaptureSwitchResetPlayerState(packet);
			bool deferred = CaptureSwitchResetWorldState(packet);
			if (Connection.IsPacketTraceActive())
			{
				Logger.Info(
					$"{(deferred ? "Deferring" : "Suppressing")} clientbound packet from backend {BackendName} during switch reset: {packet.GetType().Name}.");
			}
			return true;
		}

		/// <summary>The rewrite-and-send step every surviving packet takes.</summary>
		private void ForwardToClient(IPacket packet, long traceSequence, bool pendingStartGame, int sourceDimension)
		{
			// Every clientbound rewrite (runtime ids, chat identity) lives in PacketRewriter.
			IPacket rewritten = Rewriter.RewriteClientbound(packet, BackendName);
			bool sent = SendRewrittenClientbound(rewritten, traceSequence);
			if (sent && rewritten is StartGamePacket)
			{
				// From here on an unexpected backend loss can be turned into a switch rather than a kick.
				Connection.MarkClientJoinedWorld();
				if (!pendingStartGame)
				{
					// A first join (or a failover landing) is never told the sky by BDS at all; a switch has
					// already replayed it while clearing the previous world. Unlike a switch there is no
					// previous sky left over on the client to clear, so an unheard-of backend stays silent
					// rather than inventing fair weather.
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
			// A pending leg dying mid-handshake is reported straight back to whoever is waiting on the
			// switch/join future; Failover does not own that leg yet, and letting it run here would
			// leave BackendSwitchAttempt waiting out its whole timeout on an outcome already known.
			if (ReferenceEquals(Backend, Connection.PendingBackend()))
			{
				Activation.OnFailure(Backend, new InvalidOperationException(reason));
				return;
			}
			if (ReferenceEquals(Backend, Connection.Backend()) && Connection.Client.IsConnected)
			{
				if (Connection.IsFailingOver())
				{
					// The disconnect interception already started this; the socket closing behind it is the
					// expected next step, not a second failure to react to.
					Backend.SetDisconnectClientOnClose(false);
					return;
				}
				if (JoinFailover != null && JoinFailover.HandleJoinFailure(Connection, BackendName, reason))
				{
					// Dropped before StartGame: the player has no world to be moved out of, so the join
					// try-list owns this, not mid-session Failover.
					Backend.SetDisconnectClientOnClose(false);
					return;
				}
				if (disconnectPassedThrough)
				{
					// The client already has the backend's disconnect and is on its way out. Anything
					// here would be undoing a decision the backend deliberately made.
					Connection.Client.Disconnect(reason);
					return;
				}
				if (Failover.Begin(Connection, BackendName, reason))
				{
					// BackendSession.OnTransportClosed disconnects the client right after this returns
					// unless the flag is cleared, which would defeat the Failover before it has connected
					// anywhere.
					Backend.SetDisconnectClientOnClose(false);
					return;
				}
				Connection.Client.Disconnect(reason);
			}
		}
	}
}