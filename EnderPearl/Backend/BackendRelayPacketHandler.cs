using System;
using System.Collections.Generic;
using EnderPearl.Command;
using EnderPearl.Net;
using global::Protocol.Packets;
using global::Protocol.Types;
using EnderPearl.Logging;

namespace EnderPearl.Backend
{
	/// <summary>
	/// The clientbound leg of an established backend session: every packet the backend sends the
	/// player flows through here on its way out. Split across partial class files:
	///
	/// <list type="bullet">
	/// <item>BackendRelayPacketHandler.Diagnostics.cs - diagnostic drop/neuter switches</item>
	/// <item>BackendRelayPacketHandler.InitialJoin.cs - internal transfers and XUID injection</item>
	/// <item>BackendRelayPacketHandler.SwitchState.cs - switch-reset world-state capture, respawn acks</item>
	/// <item>BackendRelayPacketHandler.KickLogging.cs - kick interception, runtime-id rewrites, tracing details</item>
	/// </list>
	/// </summary>
	public sealed partial class BackendRelayPacketHandler : IPacketHandler, IDisconnectNotifier
	{
		/// <summary>
		/// Bisect switch: forward the backend's command tree untouched, without the proxy's /server and
		/// /hub entries. Diagnostic only; the proxy commands stop working while it is on.
		/// </summary>
		private static readonly bool NO_COMMAND_INJECTION =
			AppContext.TryGetSwitch("proxy.noCommandInjection", out bool noCmd) && noCmd;

		/// <summary>
		/// How long a switching player may be held while their new backend's packs are downloaded. Long
		/// enough for a large pack on a local link, short enough that a silent backend is not mistaken
		/// for a slow one.
		/// </summary>
		private const long PACK_FETCH_TIMEOUT_MILLIS = 20_000;

		private readonly ProxyConnection connection;
		private readonly BackendSession backend;
		private readonly string backendName;
		private readonly BackendActivation activation;
		private readonly AvailableCommandsInjector commandsInjector;
		private readonly Func<string, string> verifiedXuidLookup;
		private readonly BackendFailover failover;
		private readonly JoinFailover joinFailover;
		private readonly BackendDirectory backendDirectory;
		private readonly BackendSwitcher backendSwitcher;

		// Set once this backend's kick has been claimed by failover. Its socket stays open for a short
		// while afterwards, and anything else it sends in that window belongs to a world the player is
		// already leaving.
		private bool kickIntercepted;
		private uint backendInputLockData;
		/// <summary>Packs being assembled from bytes on their way to the client; see CaptureBackendPackBytes.</summary>

		public BackendRelayPacketHandler(
			ProxyConnection connection,
			BackendSession backend,
			string backendName,
			BackendActivation activation,
			AvailableCommandsInjector commandsInjector,
			Func<string, string>? verifiedXuidLookup,
			BackendFailover failover,
			JoinFailover joinFailover,
			BackendDirectory backendDirectory,
			BackendSwitcher backendSwitcher
		)
		{
			this.connection = connection;
			this.backend = backend;
			this.backendName = backendName;
			this.activation = activation;
			this.commandsInjector = commandsInjector;
			this.verifiedXuidLookup = verifiedXuidLookup ?? (_ => "");
			this.failover = failover;
			this.joinFailover = joinFailover;
			this.backendDirectory = backendDirectory;
			this.backendSwitcher = backendSwitcher;
		}

		public PacketSignal Handle(IPacket packet)
		{
			bool pendingStartGame = packet is StartGamePacket && ReferenceEquals(backend, connection.PendingBackend());
			if (connection.PendingBackend() != null && ReferenceEquals(backend, connection.Backend()))
			{
				if (connection.IsPacketTraceActive())
				{
					Logger.Info(
						$"Dropping old-backend packet from {backendName} during pending switch: {packet.GetType().Name}.");
				}
				return PacketSignal.Handled;
			}
			if (!IsCurrentBackend())
			{
				if (connection.IsPacketTraceActive())
				{
					Logger.Info(
						$"Dropping stale packet from backend {backendName} after handoff: {packet.GetType().Name}.");
				}
				return PacketSignal.Handled;
			}
			if (ReferenceEquals(backend, connection.PendingBackend()) && AcknowledgePendingSwitchLoginPacket(packet))
			{
				return PacketSignal.Handled;
			}
			if (packet is UpdateClientInputLocksPacket inputLocks
				&& CaptureSwitchInputLocks(inputLocks))
			{
				return PacketSignal.Handled;
			}
			if (packet is TransferPacket transfer && InterceptInternalTransfer(transfer))
			{
				return PacketSignal.Handled;
			}
			if (IsSuppressedForDiagnostics(packet))
			{
				return PacketSignal.Handled;
			}
			NeuterForDiagnostics(packet);
			// Must come before anything that could forward the packet: a backend's disconnect reaching
			// the client is an immediate kick with no way back, so failover has to claim it first.
			if (ReferenceEquals(backend, connection.Backend()) && InterceptBackendKick(packet))
			{
				return PacketSignal.Handled;
			}
			
			long traceSequence = -1;
			if (connection.IsPacketTraceActive())
			{
				traceSequence = connection.NextClientboundTraceSequence();
				Logger.Info(
					$"Trace clientbound #{traceSequence} +{connection.ElapsedMillis()}ms from backend {backendName}: {packet.GetType().Name} current={ReferenceEquals(backend, connection.Backend())} pending={ReferenceEquals(backend, connection.PendingBackend())} switchReset={connection.BackendSwitchResetRef() != null}.");
				LogClientboundDetails(packet);
			}
			int sourceDimension = connection.PlayerDimensionId();
			if (pendingStartGame)
			{
				ClearPreviousClientWorldState();
			}
			if (packet is StartGamePacket schemeStartGame)
			{
				// The client keeps whichever scheme its first StartGame carried - a session fact that
				// decides how they can be moved from now on; the per-backend half feeds scheme-aware
				// reconnect routing.
				connection.RememberClientBlockIdsHashed(schemeStartGame.BlockNetworkIdsAreHashes);
				BackendBlockSchemes.Remember(backendName, schemeStartGame.BlockNetworkIdsAreHashes);
			}
			SyncDefinitionState(packet);
			// Single-version build: client and backend codecs are identical, so there is no
			// cross-protocol drop list to consult (isCrossProtocol() is always false).
			if (packet is DeathInfoPacket)
			{
				connection.TracePacketsForMillis(ProxyConnection.ConfiguredPacketTraceMillis());
				if (ProxyConnection.ConfiguredPacketTraceMillis() > 0)
				{
					Logger.Info(
						$"Enabled detailed packet trace for {connection.Client().RemoteEndPoint} for {ProxyConnection.ConfiguredPacketTraceMillis()}ms after DeathInfoPacket at +{connection.ElapsedMillis()}ms.");
				}
			}
			if (packet is global::Protocol.Packets.PacketViolationWarningPacket violation)
			{
				// The single most informative packet BDS sends: which of our packets it could not read,
				// and its own parser error. Print unconditionally - a violation always means we broke.
				// Java has no branch for this type, so after logging it is relayed onward like any
				// other clientbound packet.
				Logger.Error(
					$"[S2] PACKET VIOLATION from {backendName}: type={violation.ViolationType} severity={violation.ViolationSeverity} offendingPacketId={violation.ViolationPacketId} context={violation.ViolationContext}");
			}
			if (packet is AvailableCommandsPacket availableCommands)
			{
				int before = availableCommands.Commands.Count;
				if (!NO_COMMAND_INJECTION)
				{
					commandsInjector.Inject(availableCommands);
				}
				int after = availableCommands.Commands.Count;
				if (connection.IsPacketTraceActive())
				{
					Logger.Info(
						$"Forwarding command tree from backend {backendName}: {before} native commands, {after} total after proxy injection.");
				}
				connection.TracePacketsForMillis(ProxyConnection.ConfiguredPacketTraceMillis());
				if (ProxyConnection.ConfiguredPacketTraceMillis() > 0)
				{
					Logger.Info(
						$"Enabled detailed packet trace for {connection.Client().RemoteEndPoint} for {ProxyConnection.ConfiguredPacketTraceMillis()}ms after AvailableCommands at +{connection.ElapsedMillis()}ms.");
				}
			}
			if (packet is CommandOutputPacket commandOutput)
			{
				if (connection.IsPacketTraceActive())
				{
					Logger.Info(
						$"Forwarding backend command output from {backendName}: successCount={commandOutput.Output?.SuccessCount ?? 0} messages={commandOutput.Output?.OutputMessages?.Count ?? 0}.");
				}
			}
			// Note: this codec's framing layer drops undecodable packet ids before they reach a handler,
			// so the Java handler's UnknownPacket branch has no counterpart here.
			if (packet is UpdatePlayerGameTypePacket updateGameType)
			{
				if (connection.IsPacketTraceActive())
				{
					Logger.Info(
						$"Forwarding UpdatePlayerGameType from backend {backendName}: gameType={updateGameType.PlayerGameType} tick={updateGameType.Tick?.InputTick ?? 0}.");
				}
			}
			else if (packet is SetPlayerGameTypePacket setGameType)
			{
				if (connection.IsPacketTraceActive())
				{
					Logger.Info(
						$"Forwarding SetPlayerGameType from backend {backendName}: gamemode={setGameType.PlayerGameType}.");
				}
			}
			else if (packet is DisconnectPacket disconnect)
			{
				if (connection.IsPacketTraceActive())
				{
					Logger.Info(
						$"Forwarding backend disconnect packet from {backendName}: reason={disconnect.Reason} skipped={disconnect.Messages.Index == 1} message={Messages.DisconnectMessage(disconnect)} filtered={Messages.DisconnectFilteredMessage(disconnect)}.");
				}
			}
			BackendSwitchReset? switchReset = connection.BackendSwitchResetRef();
			if (!pendingStartGame
				&& switchReset != null
				&& switchReset.IsActive()
				&& ReferenceEquals(backend, connection.Backend())
				&& SuppressWorldStateDuringSwitchReset(packet))
			{
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
					IPacket? addTranslated = connection.SessionProfile.Translator.TranslateClientbound(
						RewriteClientboundRuntimeIds(packet)!,
						connection.SessionProfile.TranslationContext());
					if (addTranslated != null)
					{
						connection.AddDeferredSwitchWorldState(addTranslated);
					}
					// Same bookkeeping as the forward path so a future switch's world cleanup knows
					// about these entities: entity unique ids above stay pre-rewrite, while link
					// endpoints arrive already swapped to client-facing ids - exactly what the
					// cleanup packets sent straight to the client must name (WaterdogPE tracks
					// after its rewrite for the same reason).
					connection.ClientWorldState.Track(packet);
					if (connection.IsPacketTraceActive())
					{
						Logger.Info(
							$"Deferring clientbound entity spawn from backend {backendName} during switch reset: {packet.GetType().Name}.");
					}
					return PacketSignal.Handled;
				}
				CaptureSwitchResetPlayerState(packet);
				bool deferred = CaptureSwitchResetWorldState(packet);
				if (connection.IsPacketTraceActive())
				{
					Logger.Info(
						$"{(deferred ? "Deferring" : "Suppressing")} clientbound packet from backend {backendName} during switch reset: {packet.GetType().Name}.");
				}
				return PacketSignal.Handled;
			}
			long unknownRuntimeEntityId = UnknownRuntimeEntityUpdate(packet);
			if (unknownRuntimeEntityId > 0)
			{
				if (connection.IsPacketTraceActive())
				{
					Logger.Info(
						$"Dropping clientbound entity update from backend {backendName} for unknown runtimeEntityId={unknownRuntimeEntityId}: {packet.GetType().Name}.");
				}
				return PacketSignal.Handled;
			}
			// Identity translation in this single-version build; rewrite runtime ids first either way.
			IPacket translated = RewriteClientboundRuntimeIds(packet)!;
			bool sent = SendTranslatedClientbound(translated, packet.GetType().Name, traceSequence);
			if (sent && translated is StartGamePacket)
			{
				// From here on an unexpected backend loss can be turned into a switch rather than a kick.
				connection.MarkClientJoinedWorld();
			}
			connection.ClientWorldState.Track(packet);
			if (pendingStartGame && packet is StartGamePacket startGame)
			{
				SendSwitchWorldReadyPackets(startGame, sourceDimension);
			}
			return PacketSignal.Handled;
		}

		private bool IsCurrentBackend()
		{
			return ReferenceEquals(backend, connection.Backend()) || ReferenceEquals(backend, connection.PendingBackend());
		}

		public void OnDisconnected(string reason)
		{
			if (connection.Client().IsConnected)
			{
				Logger.Error(
					$"Backend {backendName} disconnected player {connection.ClientLogin.AuthData.DisplayName} unexpectedly: {reason}.");
			}
			// A pending leg dying mid-handshake is reported straight back to whoever is waiting on the
			// switch/join future; failover does not own that leg yet, and letting it run here would
			// leave BackendSwitchAttempt waiting out its whole timeout on an outcome already known.
			if (ReferenceEquals(backend, connection.PendingBackend()))
			{
				activation.OnFailure(backend, new InvalidOperationException(reason));
				return;
			}
			if (ReferenceEquals(backend, connection.Backend()) && connection.Client().IsConnected)
			{
				if (connection.IsFailingOver())
				{
					// The kick interception already started this; the socket closing behind it is the
					// expected next step, not a second failure to react to.
					backend.SetDisconnectClientOnClose(false);
					return;
				}
				if (joinFailover != null && joinFailover.HandleJoinFailure(connection, backendName, reason))
				{
					// Dropped before StartGame: the player has no world to be moved out of, so the join
					// try-list owns this, not mid-session failover.
					backend.SetDisconnectClientOnClose(false);
					return;
				}
				if (kickPassedThrough)
				{
					// The client already has the backend's kick and is on its way out. Anything here
					// would be undoing a decision the backend deliberately made.
					connection.Client().Disconnect(reason);
					return;
				}
				if (failover.Begin(connection, backendName, reason, pendingProtocolFault))
				{
					// BackendSession.OnTransportClosed kicks the client right after this returns unless
					// the flag is cleared, which would defeat the failover before it has connected
					// anywhere.
					backend.SetDisconnectClientOnClose(false);
					return;
				}
				connection.Client().Disconnect(reason);
			}
		}
	}
}
