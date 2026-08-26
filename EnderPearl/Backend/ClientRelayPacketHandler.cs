using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using EnderPearl.Command;
using EnderPearl.Net;
using global::Protocol.Packets;
using global::Protocol.Types;
using ResourcePackClientResponsePacketPayload = global::Protocol.Types.ResourcePackClientResponsePacketPayload;
using TextPacketPayload = global::Protocol.Types.TextPacketPayload;
using InteractAction = global::Protocol.InteractPacketPayload.Action;
using InputFlags = global::Protocol.PlayerAuthInputPacketPayload.InputData;
// ItemStackRequestCereal and ItemStackRequestPacketData are namespaces (not classes), so plain
// using-namespace directives do not bring their names into scope; alias them explicitly.
using ItemStackRequestCereal = global::Protocol.Types.ItemStackRequestCereal;
using ItemStackRequestPacketData = global::Protocol.Types.ItemStackRequestPacketData;
using PlayerActionType = global::Protocol.PlayerActionType;
using ResourcePackResponse = global::Protocol.ResourcePackResponse;
using ServerboundLoadingScreenPacketType = global::Protocol.ServerboundLoadingScreenPacketType;
using EnderPearl.Logging;

namespace EnderPearl.Backend
{
	/// <summary>
	/// The client-facing half of the relay: everything a connected player sends arrives here on its way
	/// to the backend. Owns command interception, movement/chunk-radius bookkeeping, the client side of
	/// backend switch resets, proxy resource-pack serving and the identity/normalization passes every
	/// forwarded packet goes through.
	///
	/// <p>The backend-facing half is <see cref="BackendRelayPacketHandler"/>; anything rewritten here
	/// must be rewritten back there.</p>
	/// </summary>
	public sealed class ClientRelayPacketHandler : IPacketHandler, IDisconnectNotifier
	{
		private readonly ProxyConnection connection;
		private readonly EnderPearl.Command.ProxyCommandInterceptor commandInterceptor;
		private readonly BackendCommandRouter commandRouter;

		public ClientRelayPacketHandler(
			ProxyConnection connection,
			EnderPearl.Command.ProxyCommandInterceptor commandInterceptor,
			BackendCommandRouter commandRouter
		)
		{
			this.connection = connection ?? throw new ArgumentNullException(nameof(connection));
			this.commandInterceptor = commandInterceptor ?? throw new ArgumentNullException(nameof(commandInterceptor));
			this.commandRouter = commandRouter ?? throw new ArgumentNullException(nameof(commandRouter));
		}

		public PacketSignal Handle(IPacket packet)
		{
			long traceSequence = -1;
			if (connection.IsPacketTraceActive())
			{
				traceSequence = connection.NextServerboundTraceSequence();
				Logger.Info(
					$"Trace serverbound #{traceSequence} +{connection.ElapsedMillis()}ms from {connection.Client().RemoteEndPoint}: {packet.GetType().Name} " +
					$"backend={connection.BackendName()} pending={(connection.PendingBackend() != null ? "true" : "false")} " +
					$"switchReset={(connection.BackendSwitchResetRef() != null ? "true" : "false")}.");
				LogServerboundDetails(packet);
				LogMovementStateChange(packet);
			}

			BackendSession? backend = connection.Backend();
			if (backend == null || !backend.IsConnected)
			{
				if (connection.IsFailingOver() || connection.IsJoinSequenceActive())
				{
					// The backend died and the proxy is moving the player to a fallback. Their client
					// keeps sending input at ~20/s in the meantime; those packets have nowhere to go,
					// but dropping them is the whole point - kicking here is what failover exists to
					// avoid. Forwarding resumes once the fallback's StartGame swaps in a live backend.
					return PacketSignal.Handled;
				}
				connection.Client().Disconnect("Backend is not connected");
				return PacketSignal.Handled;
			}

			// Java intercepted serverbound CameraAimAssistInstructionPacket here during cross-protocol
			// joins and answered it locally with a CameraAimAssistPacket. Protocol 2168 (1.26.40) has no
			// serverbound aim-assist instruction packet at all - the type postdates this codec and only
			// existed for older cross-version clients - so no equivalent branch exists here: client and
			// backend always speak 2168.

			if (packet is PacketViolationWarningPacket violation)
			{
				Logger.Error(
					$"Client packet violation from {connection.Client().RemoteEndPoint}: type={violation.ViolationType} severity={violation.ViolationSeverity} " +
					$"packetId={violation.ViolationPacketId} message={violation.ViolationContext}.");
			}

			if (packet is RequestChunkRadiusPacket requestChunkRadius)
			{
				NormalizeChunkRadiusRequest(requestChunkRadius);
				connection.RememberChunkRadius(requestChunkRadius.ChunkRadius, requestChunkRadius.MaxChunkRadius);
			}

			BackendSwitchReset? switchReset = connection.BackendSwitchResetRef();
			if (switchReset != null && switchReset.IsActive())
			{
				// Java matched PlayerActionType.DIMENSION_CHANGE_SUCCESS; protocol 2168 spells wire value
				// 14 ChangeDimensionAck - same ack, different enum member name.
				if (packet is PlayerActionPacket action && action.Action == PlayerActionType.ChangeDimensionAck)
				{
					if (connection.IsPacketTraceActive())
					{
						Logger.Info(
							$"Received client dimension-change ack during backend switch to {connection.BackendName()}.");
					}
					switchReset.HandleDimensionChangeSuccess(connection);
					return PacketSignal.Handled;
				}
				if (packet is SetLocalPlayerAsInitializedPacket initialized)
				{
					if (connection.IsPacketTraceActive())
					{
						Logger.Info(
							$"Suppressing early client initialization during backend switch to {connection.BackendName()}.");
					}
					return PacketSignal.Handled;
				}
				if (packet is ServerboundLoadingScreenPacket loadingScreen)
				{
					if (connection.IsPacketTraceActive())
					{
						Logger.Info(
							$"Received client loading-screen ack during backend switch to {connection.BackendName()}: type={loadingScreen.LoadingScreenPacketType}.");
					}
					switchReset.HandleLoadingScreen(connection, loadingScreen);
					return PacketSignal.Handled;
				}
				if (packet is SubChunkRequestPacket request)
				{
					switchReset.HandleTargetWorldRequest(connection, request.DimensionType?.Value ?? 0);
				}
			}

			// This build only ever speaks to protocol 2168 backends, so Java's legacy death-respawn
			// translation (loading-screen packets to RespawnPacket/PlayerAction handshakes for older
			// protocols) has no equivalent branch here; loading-screen packets forward untouched.

			BackendSession? pendingBackend = PendingSwitchBackend();
			if (packet is ClientCacheStatusPacket cacheStatus)
			{
				BackendSession targetBackend = pendingBackend == null ? backend : pendingBackend;
				var plainDisabledCache = new ClientCacheStatusPacket();
				plainDisabledCache.IsCacheSupported = false;
				targetBackend.SendPacket(plainDisabledCache);
				return PacketSignal.Handled;
			}

			if (pendingBackend != null && IsBackendLoginResponse(packet))
			{
				SendToBackend(pendingBackend, packet);
				return PacketSignal.Handled;
			}

			

			if (packet is CommandRequestPacket commandRequest)
			{
				if (connection.IsPacketTraceActive())
				{
					LogCommandRequest(commandRequest);
				}
				if (IsClientSideCommandPreview(commandRequest))
				{
					Logger.Info(
						$"Suppressing client-side command preview from {connection.Client().RemoteEndPoint}: {commandRequest.Command}");
					return PacketSignal.Handled;
				}
				CommandInterception interception = commandInterceptor.Intercept(commandRequest);
				if (interception is CommandInterception.Consumed consumed)
				{
					commandRouter.Execute(connection, consumed);
					return PacketSignal.Handled;
				}
				if (connection.IsPacketTraceActive())
				{
					Logger.Info(
						$"Forwarding native command from {connection.Client().RemoteEndPoint} to backend {connection.BackendName()}: {commandRequest.Command}");
				}
				connection.TracePacketsForMillis(5_000);
				SendToBackend(backend, commandRequest, traceSequence);
				return PacketSignal.Handled;
			}

			SendToBackend(backend, packet, traceSequence);
			return PacketSignal.Handled;
		}

		/// <summary>
		/// Caps the chunk view distance the backend is asked for, from <c>-Dproxy.forceChunkRadius=2</c>.
		/// Zero (the default) leaves the client's request alone.
		///
		/// <para><b>Why this exists.</b> Every drop-based experiment on the disconnect is confounded,
		/// and the terrain one worst of all: dropping <c>LevelChunk</c> kept the session alive for
		/// minutes, but it also left the client with no chunks, and <b>a Bedrock client will not move the
		/// player until the chunk under them is loaded</b>. The tester confirmed it - zombies visible,
		/// movement input doing nothing, teleport working. So that run silently reproduced "standing
		/// still", which the symptom table has always listed as fine. It proved nothing.</para>
		///
		/// <para>A radius cap changes the same variable - how much terrain is streamed - while leaving the
		/// player able to fly, which is the only activity that reproduces the bug in seconds. It is the
		/// first terrain experiment that holds activity constant.</para>
		///
		/// <para>It is also a candidate mitigation rather than only a diagnostic: if radius 2-4 is stable and
		/// radius 8 is not, that is shippable while the root cause is still open.</para>
		/// </summary>
		private static readonly int FORCED_CHUNK_RADIUS = ReadIntProperty("proxy.forceChunkRadius", 0);

		private void NormalizeChunkRadiusRequest(RequestChunkRadiusPacket request)
		{
			if (FORCED_CHUNK_RADIUS > 0 && request.ChunkRadius > FORCED_CHUNK_RADIUS)
			{
				Logger.Info(
					$"Diagnostics: forcing chunk radius for {connection.Client().RemoteEndPoint}: radius={request.ChunkRadius} maxRadius={request.MaxChunkRadius} -> {FORCED_CHUNK_RADIUS}.");
				request.ChunkRadius = FORCED_CHUNK_RADIUS;
				if (request.MaxChunkRadius > FORCED_CHUNK_RADIUS)
				{
					request.MaxChunkRadius = (byte)Math.Min(FORCED_CHUNK_RADIUS, byte.MaxValue);
				}
			}
		}

		public void OnDisconnected(string reason)
		{
			connection.CloseBackend(reason);
		}

		private void LogCommandRequest(CommandRequestPacket commandRequest)
		{
			global::Protocol.Types.CommandOriginData origin = commandRequest.Origin;
			// Java logged a numeric version and the named command version separately; protocol 2168
			// carries only the named one (CurrentCmdVersion), so one field covers both.
			Logger.Info(
				$"Command request from {connection.Client().RemoteEndPoint}: command={commandRequest.Command} internal={commandRequest.IsInternal} version={commandRequest.Version} " +
				$"origin={(origin == null ? "null" : origin.Type_.ToString())} requestId={(origin == null ? "null" : origin.RequestId)} playerId={(origin == null ? -1L : origin.PlayerId)}.");
		}

		private static bool IsClientSideCommandPreview(CommandRequestPacket commandRequest)
		{
			string command = commandRequest.Command;
			if (command == null || command.Trim().Length == 0 || "/".Equals(command.Trim(), StringComparison.Ordinal))
			{
				return true;
			}
			return commandRequest.IsInternal;
		}

		private BackendSession? PendingSwitchBackend()
		{
			BackendSession? pendingBackend = connection.PendingBackend();
			if (!connection.IsSwitchingBackend() || pendingBackend == null || !pendingBackend.IsConnected)
			{
				return null;
			}
			return pendingBackend;
		}

	

		private static Guid? ExtractPackUuid(string packId)
		{
			if (packId == null || packId.Length == 0)
			{
				return null;
			}
			// Format is "uuid_version" or just "uuid"; UUIDs use hyphens, not underscores.
			int underscore = packId.IndexOf('_');
			string uuidPart = underscore >= 0 ? packId.Substring(0, underscore) : packId;
			if (Guid.TryParseExact(uuidPart, "D", out Guid uuid))
			{
				return uuid;
			}
			return null;
		}

		private static bool IsBackendLoginResponse(IPacket packet)
		{
			return packet is ResourcePackClientResponsePacket
				|| packet is ResourcePackChunkRequestPacket;
		}

		private void SendToBackend(BackendSession? backend, IPacket packet)
		{
			SendToBackend(backend, packet, connection.IsPacketTraceActive() ? 0 : -1);
		}

		private void SendToBackend(BackendSession? backend, IPacket packet, long traceSequence)
		{
			// The connectivity gate at the top of Handle guarantees a live backend; re-check anyway so
			// a backend dying mid-packet degrades to a dropped packet instead of a relay crash (the
			// Java original would have thrown NPE out of the handler).
			if (backend == null || !backend.IsConnected)
			{
				return;
			}
			// Sub-chunk mode belongs to the client's session, not to one backend, so it survives a
			// switch: a client taught to request terrain a sub-chunk at a time by a BDS backend goes on
			// doing it after the handoff. A backend that never advertised the system then receives
			// requests it cannot answer. Withheld here rather than translated away because there is
			// nothing to translate to — the request simply has no meaning there.
			if (backend != null && backend.DropSubChunkRequests() && packet is SubChunkRequestPacket)
			{
				if (traceSequence >= 0 || connection.IsPacketTraceActive())
				{
					Logger.Info(
						$"Withholding SubChunkRequest from backend {connection.BackendName()}: it does not implement the sub-chunk system.");
				}
				return;
			}
			// WaterdogPE's ConnectedUpstreamHandler: the client confirming a container close retires
			// the tracked entry, so the next backend switch does not emit closes for windows that are
			// already shut.
			if (packet is ContainerClosePacket clientContainerClose)
			{
				connection.ClientWorldState.TrackClientContainerClose(clientContainerClose.ContainerId);
			}
			NormalizePlayerRuntimeId(packet);
			NormalizeChatIdentity(packet);
			// Translation is identity in this single-version build: the translator returns the packet
			// unchanged unless the logic above mutated it. A null return means "drop".
			IPacket? translated = connection.SessionProfile.Translator.TranslateServerbound(
				packet,
				connection.SessionProfile.TranslationContext());
			if (translated == null)
			{
				if (traceSequence >= 0 || connection.IsPacketTraceActive())
				{
					Logger.Info(
						$"Dropping serverbound packet after protocol translation for backend protocol {connection.SessionProfile.BackendCodec.ProtocolVersion}: {packet.GetType().Name}.");
				}
				return;
			}
			// Java retained the translated buffer for the peer; these packets are plain managed
			// objects, so sending them as-is is the whole job.
			backend.SendPacket(translated);
			if (traceSequence >= 0 && connection.IsPacketTraceActive())
			{
				string id = traceSequence > 0 ? "#" + traceSequence + " " : "";
				Logger.Info(
					$"Forwarded serverbound {id}+{connection.ElapsedMillis()}ms to backend {connection.BackendName()} original={packet.GetType().Name} translated={translated.GetType().Name} " +
					$"clientConnected={connection.Client().IsConnected} backendConnected={backend.IsConnected}.");
			}
		}

		/// <summary>
		/// Stamps the authenticated identity onto anything the client says.
		///
		/// <para><c>TextPacket</c> carries the author's name and XUID as plain fields the client fills in,
		/// and nothing downstream re-derives them from the session. A modified client can therefore send
		/// chat as any name it likes - an owner's, a staff member's - and every backend, plugin and chat
		/// log that trusts those fields repeats it. The values here come from the Mojang-signed login
		/// chain, so overwriting them costs an honest client nothing and closes the impersonation.</para>
		///
		/// <para>The packet type is deliberately left alone: dropping a non-CHAT type would be a behaviour
		/// change for backends that use them, and with the name and XUID corrected the remaining
		/// capability is sending oneself an odd-looking message. Protocol 2168 stores the author inside
		/// the chat body variant (<c>AuthorAndMessage.PlayerName</c>) and the XUID beside the body.</para>
		/// </summary>
		private void NormalizeChatIdentity(IPacket packet)
		{
			if (packet is not TextPacket text)
			{
				return;
			}
			if (text.Body.Index != 1 || text.Body.Value is not TextPacketPayload.AuthorAndMessage body)
			{
				// No author field outside the chat body variant; nothing to stamp.
				return;
			}
			string displayName = connection.ClientLogin.AuthData.DisplayName;
			string xuid = connection.ClientLogin.AuthData.Xuid;
			// A vanilla client sends its XUID blank and lets the server fill it in, so a blank one is
			// normal and only a populated-but-wrong value is worth reporting.
			bool forgedXuid = !string.IsNullOrWhiteSpace(text.SenderSXUID)
				&& !string.Equals(xuid, text.SenderSXUID, StringComparison.Ordinal);
			bool forgedName = !string.Equals(displayName, body.PlayerName, StringComparison.Ordinal);
			if (forgedName || forgedXuid)
			{
				Logger.Info(
					$"Rewrote serverbound chat identity from {connection.Client().RemoteEndPoint}: sourceName={body.PlayerName} xuid={text.SenderSXUID} -> {displayName}/{xuid}.");
				body.PlayerName = displayName;
				text.SenderSXUID = forgedXuid ? xuid : text.SenderSXUID;
				text.InvalidateWireCache();
			}
		}

		/// <summary>
		/// Rewrites the local player's runtime id on packets the client addresses to itself.
		///
		/// <para>The client keeps the id from its first StartGame for the whole proxy session, while every
		/// backend assigns its own - after a switch they differ, and a packet still carrying the client's
		/// id names an entity the backend does not associate with this player. It is dropped silently:
		/// nothing errors, the action simply never happens.</para>
		///
		/// <para>Anything here must also be remapped in the other direction by
		/// <c>BackendRelayPacketHandler.RewriteClientboundRuntimeIds</c>; a packet handled on one side
		/// only is the shape of bug this list exists to prevent.</para>
		///
		/// <para>In protocol 2168 the inventory transaction's runtime id moved inside the
		/// use-on-actor transaction variant, so that is the only transaction shape remapped here.</para>
		/// </summary>
		internal void NormalizePlayerRuntimeId(IPacket packet)
		{
			long backendPlayerRuntimeEntityId = connection.BackendPlayerRuntimeEntityId();
			if (backendPlayerRuntimeEntityId <= 0)
			{
				return;
			}
			switch (packet)
			{
				case PlayerActionPacket playerAction:
					playerAction.PlayerRuntimeID = Rid(backendPlayerRuntimeEntityId);
					playerAction.InvalidateWireCache();
					break;
				case RespawnPacket respawn:
					// The client's answer to the death screen. Addressed to the wrong entity the backend
					// never replies SERVER_READY, and the player sits on "Respawning..." forever while the
					// client retries. Only ever sent about the local player.
					respawn.PlayerRuntimeId = Rid(backendPlayerRuntimeEntityId);
					respawn.InvalidateWireCache();
					break;
				case MobEquipmentPacket mobEquipment:
					mobEquipment.TargetRuntimeID = Rid(backendPlayerRuntimeEntityId);
					mobEquipment.InvalidateWireCache();
					break;
				case AnimatePacket animate:
					animate.TargetActorRuntimeID = Rid(backendPlayerRuntimeEntityId);
					animate.InvalidateWireCache();
					break;
				case SetLocalPlayerAsInitializedPacket initialized:
					initialized.PlayerID = Rid(backendPlayerRuntimeEntityId);
					initialized.InvalidateWireCache();
					break;
				case InteractPacket interact when interact.TargetRuntimeID != null:
					long interactOriginalTarget = (long)interact.TargetRuntimeID.Value;
					interact.TargetRuntimeID = Rid(connection.ToBackendRuntimeEntityId(interactOriginalTarget));
					if (interact.Action == InteractAction.OpenInventory)
					{
						interact.TargetRuntimeID = Rid(backendPlayerRuntimeEntityId);
						Logger.Info(
							$"Interact(OpenInventory) from {connection.Client().RemoteEndPoint}: clientTarget={interactOriginalTarget} mappedTarget={connection.ToBackendRuntimeEntityId(interactOriginalTarget)} finalTarget={backendPlayerRuntimeEntityId}.");
					}
					interact.InvalidateWireCache();
					break;
				case InventoryTransactionPacket transaction
						when transaction.Transaction.Index == 3
						&& transaction.Transaction.Value is ItemUseOnActorInventoryTransaction onActor
						&& onActor.RuntimeId != null:
					onActor.RuntimeId.Value = unchecked((ulong)connection.ToBackendRuntimeEntityId((long)onActor.RuntimeId.Value));
					transaction.InvalidateWireCache();
					break;
			}
		}

		private static ActorRuntimeID Rid(long runtimeEntityId)
		{
			return new ActorRuntimeID { Value = unchecked((ulong)runtimeEntityId) };
		}

		private HashSet<InputFlags>? lastLoggedInputData;
		private long lastLoggedInputTick = long.MinValue;
		private long lastInputStateLogMillis = long.MinValue;

		/// <summary>
		/// Movement sample interval while packet tracing is enabled, from
		/// <c>-Dproxy.movementSampleMillis=N</c>. Zero records every <c>PlayerAuthInput</c>.
		/// </summary>
		private static readonly long MOVEMENT_SAMPLE_MILLIS = ReadLongProperty("proxy.movementSampleMillis", 1000L);

		/// <summary>Rendered into the startup <c>Diagnostics:</c> line so a run's posture is always visible.</summary>
		public static string MovementSampleSummary()
		{
			return "movementSampleMillis=" + MOVEMENT_SAMPLE_MILLIS + " (packet trace only)"
				+ (MOVEMENT_SAMPLE_MILLIS <= 0 ? " (every PlayerAuthInput)" : "");
		}

		private void LogMovementStateChange(IPacket packet)
		{
			if (packet is not PlayerAuthInputPacket authInput)
			{
				return;
			}
			long now = connection.ElapsedMillis();
			List<InputFlags> inputData = authInput.InputData ?? new List<InputFlags>();
			bool changed = lastLoggedInputData == null || !lastLoggedInputData.SetEquals(inputData);
			long tick = unchecked((long)(authInput.ClientTick?.InputTick ?? 0UL));
			bool tickWentBackwards = tick < lastLoggedInputTick;
			if (!changed && !tickWentBackwards && now - lastInputStateLogMillis < MOVEMENT_SAMPLE_MILLIS)
			{
				return;
			}
			Logger.Info(
				$"Movement +{now}ms tick={tick}{(tickWentBackwards ? " TICK-WENT-BACKWARDS(prev=" + lastLoggedInputTick + ")" : "")} pos={FormatVec3(authInput.Position)} delta={FormatVec3(authInput.PosDelta)} " +
				$"rotation=({(authInput.PlayerRotation == null ? "?" : authInput.PlayerRotation.X.ToString(CultureInfo.InvariantCulture))},{(authInput.PlayerRotation == null ? "?" : authInput.PlayerRotation.Y.ToString(CultureInfo.InvariantCulture))}) " +
				$"input=[{string.Join(", ", inputData)}] inputMode={authInput.InputMode} playMode={authInput.PlayMode}.");
			// Java copied into an EnumSet so later comparisons see a stable snapshot; the HashSet copy
			// plays that role here (and tolerates an empty input list).
			lastLoggedInputData = new HashSet<InputFlags>(inputData);
			lastLoggedInputTick = tick;
			lastInputStateLogMillis = now;
		}

		private void LogServerboundDetails(IPacket packet)
		{
			switch (packet)
			{
				case PlayerAuthInputPacket authInput:
				{
					bool interesting = authInput.InputData.Contains(InputFlags.PerformItemInteraction)
						|| authInput.InputData.Contains(InputFlags.PerformBlockActions)
						|| authInput.InputData.Contains(InputFlags.PerformItemStackRequest)
						|| authInput.InputData.Contains(InputFlags.MissedSwing);
					if (!interesting)
					{
						break;
					}
					Logger.Info(
						$"  PlayerAuthInput tick={authInput.ClientTick?.InputTick ?? 0UL} pos={FormatVec3(authInput.Position)} input=[{string.Join(", ", authInput.InputData)}] itemUse={DescribeItemUse(authInput.ItemUseTransaction)} blockActions={DescribeBlockActions(authInput)} stackRequest={(authInput.ItemStackRequest.HasValue ? authInput.ItemStackRequest.Value.Actions.Count.ToString(CultureInfo.InvariantCulture) : "null")} predictedVehicle={FormatPredictedVehicle(authInput)}.");
					// A vanilla Bedrock client puts its request here rather than sending the standalone
					// packet, so without this the reference case -- what a real client does -- is the
					// one case the trace cannot show.
					LogItemStackRequestCore(authInput.ItemStackRequest);
					break;
				}
				case ItemStackRequestPacket stackRequests:
				{
					foreach (ItemStackRequestPacketData.RequestData request in stackRequests.Requests)
					{
						LogItemStackRequestCore(request);
					}
					break;
				}
				case InventoryTransactionPacket transaction:
				{
					Logger.Info(
						$"  InventoryTransaction {DescribeTransaction(transaction.Transaction)} legacyRequest={transaction.LegacyRequestID?.ID ?? 0}.");
					break;
				}
				case PlayerActionPacket action:
				{
					Logger.Info(
						$"  PlayerAction action={action.Action} runtimeEntityId={action.PlayerRuntimeID?.Value ?? 0UL} block={FormatBlockPos(action.BlockPosition)} result={FormatBlockPos(action.ResultPos)} face={action.Face} normalizedRuntimeEntityId={connection.BackendPlayerRuntimeEntityId()}.");
					break;
				}
				case RespawnPacket respawn:
				{
					Logger.Info(
						$"  Respawn state={respawn.State} runtimeEntityId={respawn.PlayerRuntimeId?.Value ?? 0UL} position={FormatVec3(respawn.Position)} normalizedRuntimeEntityId={connection.BackendPlayerRuntimeEntityId()}.");
					break;
				}
				case InteractPacket interact:
				{
					Logger.Info(
						$"  Interact action={interact.Action} runtimeEntityId={interact.TargetRuntimeID?.Value ?? 0UL} mouse={(interact.Position.HasValue ? FormatVec3(interact.Position.Value) : "null")} playerRuntimeEntityId={connection.BackendPlayerRuntimeEntityId()}.");
					break;
				}
				case SubChunkRequestPacket request:
				{
					int offsets = request.SubChunkPositionOffsetList?.Count ?? 0;
					Logger.Info(
						$"  SubChunkRequest dimension={request.DimensionType?.Value ?? 0} center={FormatSubChunkCenter(request.CenterPos)} offsets={offsets} firstOffsets={FormatSubChunkOffsets(request.SubChunkPositionOffsetList)}.");
					break;
				}
				case RequestChunkRadiusPacket request:
				{
					Logger.Info(
						$"  RequestChunkRadius radius={request.ChunkRadius} maxRadius={request.MaxChunkRadius} rememberedBefore={connection.LastRequestedChunkRadius()}/{connection.LastRequestedMaxChunkRadius()}.");
					break;
				}
				case ClientCacheStatusPacket cacheStatus:
				{
					Logger.Info($"  ClientCacheStatus supported={(cacheStatus.IsCacheSupported ? "true" : "false")}.");
					break;
				}
				case ClientCacheBlobStatusPacket blobStatus:
				{
					Logger.Info(
						$"  ClientCacheBlobStatus acks={blobStatus.FoundIds.Count} naks={blobStatus.MissingIds.Count} firstAcks={FormatFirstUlongs(blobStatus.FoundIds)} firstNaks={FormatFirstUlongs(blobStatus.MissingIds)}.");
					break;
				}
				case ServerboundLoadingScreenPacket loadingScreen:
				{
					Logger.Info(
						$"  ServerboundLoadingScreen type={loadingScreen.LoadingScreenPacketType} id={FormatLoadingScreenId(loadingScreen)}.");
					break;
				}
				case ResourcePackClientResponsePacket response:
				{
					// The status is the whole content of this packet, and the backend's pack handshake is a
					// strict order of them: without it a trace shows two identical lines and no way to tell a
					// correct handshake from one that ends it early. A client that answers COMPLETED before
					// HAVE_ALL_PACKS gets kicked for the trailing packet several seconds later, by which point
					// nothing in the log still points here.
					Logger.Info(
						$"  ResourcePackClientResponse typeIndex={(int)response.Response.Index} packs={CountResponsePacks(response)}.");
					break;
				}
			}
		}

		/// <summary>
		/// Prints an <c>ItemStackRequest</c> in full.
		///
		/// <para>In full because the server's answer is a single word. A request is accepted or rejected as
		/// a whole - <c>FailedToValidateSrcSlot</c>, with nothing about which of its actions or which
		/// slot - so the only way to tell a stale stack network id from a wrongly named container is to
		/// have the request itself sitting beside the response.</para>
		/// </summary>
		private void LogItemStackRequestCore(Optional<ItemStackRequestCereal.RequestData> request)
		{
			if (request == null || !request.HasValue)
			{
				return;
			}
			ItemStackRequestCereal.RequestData data = request.Value;
			StringBuilder line = new StringBuilder("  ItemStackRequest id=").Append(data.ClientRequestId?.ID ?? 0);
			foreach (var action in data.Actions)
			{
				line.Append("\n    ").Append(StackRequestActionName(action.Index)).Append(' ').Append(DescribeStackRequestAction(action.Value));
			}
			Logger.Info(line.Append(".").ToString());
		}

		private void LogItemStackRequestCore(ItemStackRequestPacketData.RequestData request)
		{
			if (request == null)
			{
				return;
			}
			StringBuilder line = new StringBuilder("  ItemStackRequest id=").Append(request.ClientRequestId?.ID ?? 0);
			foreach (var action in request.Actions)
			{
				line.Append("\n    ").Append(StackRequestActionName(action.Index)).Append(' ').Append(DescribeStackRequestAction(action.Value));
			}
			Logger.Info(line.Append(".").ToString());
		}

		/// <summary>
		/// One <c>ItemStackRequest</c> action, with the three fields the server validates it on.
		///
		/// <para>A slot is named by its <em>kind</em> and its index within that kind, and carries the stack
		/// network id the client believes is there - and the server refuses the whole request if any of
		/// the three disagrees with its own view. All three therefore have to be visible; a rejection
		/// reason on its own cannot distinguish them.</para>
		///
		/// <para>Java had one TransferItemStackRequestAction covering take and place; protocol 2168 splits
		/// it into two variants with identical shapes.</para>
		/// </summary>
		private static string DescribeStackRequestAction(object? action)
		{
			switch (action)
			{
				case ItemStackRequestCereal.TakeActionData take:
					return "count=" + take.Amount
						+ " from=" + DescribeStackRequestSlot(take.Source)
						+ " to=" + DescribeStackRequestSlot(take.Destination);
				case ItemStackRequestCereal.PlaceActionData place:
					return "count=" + place.Amount
						+ " from=" + DescribeStackRequestSlot(place.Source)
						+ " to=" + DescribeStackRequestSlot(place.Destination);
				case ItemStackRequestCereal.SwapActionData swap:
					return "from=" + DescribeStackRequestSlot(swap.Source)
						+ " to=" + DescribeStackRequestSlot(swap.Destination);
				case ItemStackRequestCereal.DropActionData drop:
					return "count=" + drop.Amount + " from=" + DescribeStackRequestSlot(drop.Source)
						+ " randomly=" + (drop.Randomly ? "true" : "false");
				case ItemStackRequestCereal.DestroyActionData destroy:
					return "count=" + destroy.Amount + " from=" + DescribeStackRequestSlot(destroy.Source);
				case ItemStackRequestCereal.ConsumeActionData consume:
					return "count=" + consume.Amount + " from=" + DescribeStackRequestSlot(consume.Source);
				case ItemStackRequestCereal.CraftResultsActionData results:
					return "timesCrafted=" + results.NumCrafts
						+ " results=<" + (results.CraftResults?.Count ?? 0) + " item(s)>";
				case ItemStackRequestCereal.CraftRecipeActionData recipe:
					return "recipeNetworkId=" + (recipe.RecipeNetId?.RawId ?? 0u)
						+ " crafts=" + recipe.NumberOfRequestedCrafts;
				case ItemStackRequestCereal.CraftCreativeActionData creative:
					return "creativeItemNetworkId=" + creative.CreativeItemNetId
						+ " crafts=" + creative.NumberOfRequestedCrafts;
				default:
					return action?.ToString() ?? "null";
			}
		}

		private static string StackRequestActionName(int index)
		{
			return index switch
			{
				0 => "Take",
				1 => "Place",
				2 => "Swap",
				3 => "Drop",
				4 => "Destroy",
				5 => "Consume",
				6 => "Create",
				7 => "LabTableCombine",
				8 => "BeaconPayment",
				9 => "MineBlock",
				10 => "CraftRecipe",
				11 => "CraftRecipeAuto",
				12 => "CraftCreative",
				13 => "CraftRecipeOptional",
				14 => "CraftRepairAndDisenchant",
				15 => "CraftLoom",
				16 => "CraftNonImplemented",
				17 => "CraftResults",
				_ => "?" + index
			};
		}

		private static string DescribeStackRequestSlot(ItemStackRequestCereal.SlotInfoData slot)
		{
			if (slot == null)
			{
				return "null";
			}
			return slot.FullContainerName?.ContainerName.ToString() + "[" + slot.Slot + "] netId=" + slot.NetIdVariant;
		}

		private static string DescribeTransaction(
			OneOf.OneOf<NormalTransactionData, InventoryMismatchData, ItemUseInventoryTransaction, ItemUseOnActorInventoryTransaction, ItemReleaseInventoryTransaction> transaction)
		{
			switch (transaction.Index)
			{
				case 2 when transaction.Value is ItemUseInventoryTransaction itemUse:
					return "type=ItemUse actionType=" + itemUse.ActionType
						+ " runtimeEntityId=-"
						+ " block=" + FormatBlockPos(itemUse.Position)
						+ " face=" + itemUse.Face
						+ " hotbar=" + itemUse.Slot
						+ " item=" + FormatDescriptor(itemUse.Item)
						+ " blockDef=" + itemUse.TargetBlockId
						+ " trigger=" + itemUse.TriggerType
						+ " prediction=" + itemUse.ClientInteractPrediction
						+ " actions=" + (itemUse.Actions?.Actions?.Count ?? 0);
				case 3 when transaction.Value is ItemUseOnActorInventoryTransaction onActor:
					return "type=ItemUseOnActor actionType=" + onActor.ActionType
						+ " runtimeEntityId=" + (onActor.RuntimeId?.Value ?? 0UL)
						+ " slot=" + onActor.Slot
						+ " item=" + FormatDescriptor(onActor.Item)
						+ " actions=" + (onActor.Actions?.Actions?.Count ?? 0);
				case 4 when transaction.Value is ItemReleaseInventoryTransaction release:
					return "type=ItemRelease actionType=" + release.ActionType
						+ " slot=" + release.Slot
						+ " item=" + FormatDescriptor(release.Item);
				case 0 when transaction.Value is NormalTransactionData normal:
					return "type=Normal actions=" + (normal.Actions?.Actions?.Count ?? 0);
				default:
					return "type=" + transaction.Index;
			}
		}

		private static string DescribeItemUse(Optional<PackedItemUseLegacyInventoryTransaction> transaction)
		{
			if (transaction == null || !transaction.HasValue || transaction.Value == null)
			{
				return "null";
			}
			ItemUseInventoryTransaction use = transaction.Value.ItemUseTransaction;
			if (use == null)
			{
				return "null";
			}
			return "actionType=" + use.ActionType
				+ " block=" + FormatBlockPos(use.Position)
				+ " face=" + use.Face
				+ " hotbar=" + use.Slot
				+ " item=" + FormatDescriptor(use.Item)
				+ " blockDef=" + use.TargetBlockId
				+ " trigger=" + use.TriggerType
				+ " prediction=" + use.ClientInteractPrediction
				+ " actions=" + (use.Actions?.Actions?.Count ?? 0);
		}

		private static string DescribeBlockActions(PlayerAuthInputPacket authInput)
		{
			if (authInput.PlayerBlockActions == null || !authInput.PlayerBlockActions.HasValue
					|| authInput.PlayerBlockActions.Value == null || authInput.PlayerBlockActions.Value.Count == 0)
			{
				return "[]";
			}
			List<PlayerBlockActionData> actions = authInput.PlayerBlockActions.Value;
			StringBuilder builder = new StringBuilder("[");
			for (int i = 0; i < actions.Count; i++)
			{
				PlayerBlockActionData action = actions[i];
				if (i > 0)
				{
					builder.Append(", ");
				}
				builder.Append(action.PlayerActionType)
					.Append("@")
					.Append(FormatBlockPos(action.Position))
					.Append("/")
					.Append(action.Facing);
			}
			return builder.Append("]").ToString();
		}

		private static int CountResponsePacks(ResourcePackClientResponsePacket response)
		{
			return response.Response.Index == 1 && response.Response.Value is ResourcePackClientResponsePacketPayload.Downloading payload
				? payload.DownloadingPacks?.Count ?? 0
				: 0;
		}

		private static string FormatPredictedVehicle(PlayerAuthInputPacket authInput)
		{
			return authInput.ClientPredictedVehicle != null && authInput.ClientPredictedVehicle.HasValue
				? authInput.ClientPredictedVehicle.Value.Value.ToString(CultureInfo.InvariantCulture)
				: "null";
		}

		private static string FormatLoadingScreenId(ServerboundLoadingScreenPacket loadingScreen)
		{
			return loadingScreen.LoadingScreenId != null && loadingScreen.LoadingScreenId.HasValue
				? loadingScreen.LoadingScreenId.Value.ToString(CultureInfo.InvariantCulture)
				: "none";
		}

		private static string FormatFirstUlongs(List<ulong> values)
		{
			if (values == null || values.Count == 0)
			{
				return "[]";
			}
			var parts = new List<string>(Math.Min(values.Count, 8));
			for (int i = 0; i < values.Count && i < 8; i++)
			{
				parts.Add(values[i].ToString(CultureInfo.InvariantCulture));
			}
			return "[" + string.Join(", ", parts) + "]";
		}

		private static string FormatVec3(Vec3? value)
		{
			return value == null
				? "null"
				: "(" + value.X.ToString(CultureInfo.InvariantCulture) + ", "
					+ value.Y.ToString(CultureInfo.InvariantCulture) + ", "
					+ value.Z.ToString(CultureInfo.InvariantCulture) + ")";
		}

		private static string FormatBlockPos(BlockPos? value)
		{
			return value == null
				? "null"
				: "(" + value.X.ToString(CultureInfo.InvariantCulture) + ", "
					+ value.Y.ToString(CultureInfo.InvariantCulture) + ", "
					+ value.Z.ToString(CultureInfo.InvariantCulture) + ")";
		}

		private static string FormatSubChunkCenter(SubChunkPos? center)
		{
			return center == null
				? "null"
				: "(" + center.SubchunkPositionX.ToString(CultureInfo.InvariantCulture) + ", "
					+ center.SubchunkPositionY.ToString(CultureInfo.InvariantCulture) + ", "
					+ center.SubchunkPositionZ.ToString(CultureInfo.InvariantCulture) + ")";
		}

		private static string FormatSubChunkOffsets(List<global::Protocol.Types.SubChunkPacketPayload.SubChunkPosOffset>? offsets)
		{
			if (offsets == null || offsets.Count == 0)
			{
				return "[]";
			}
			var parts = new List<string>(Math.Min(offsets.Count, 12));
			for (int i = 0; i < offsets.Count && i < 12; i++)
			{
				parts.Add("(" + offsets[i].SubchunkOffsetX + ", " + offsets[i].SubchunkOffsetY + ", " + offsets[i].SubchunkOffsetZ + ")");
			}
			return "[" + string.Join(", ", parts) + "]";
		}

		private static string FormatDescriptor(global::Protocol.Types.NetworkItemStackDescriptor.NetworkItemStackDescriptor? item)
		{
			if (item == null)
			{
				return "null";
			}
			return "id=" + item.Id + " count=" + item.StackSize + " netId="
				+ (item.NetIdVariant != null && item.NetIdVariant.HasValue ? item.NetIdVariant.Value.ToString(CultureInfo.InvariantCulture) : "-");
		}

		/// <summary>
		/// Java read tunables from system properties (<c>-Dproxy.forceChunkRadius</c>,
		/// <c>-Dproxy.movementSampleMillis</c>); environment variables play that role here.
		/// </summary>
		private static int ReadIntProperty(string name, int fallback)
		{
			string? raw = Environment.GetEnvironmentVariable(name);
			return raw != null && int.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)
				? parsed
				: fallback;
		}

		private static long ReadLongProperty(string name, long fallback)
		{
			string? raw = Environment.GetEnvironmentVariable(name);
			return raw != null && long.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsed)
				? parsed
				: fallback;
		}
	}
}
