using System;
using EnderPearl.Logging;
using System.Collections.Generic;
using System.Threading;
using EnderPearl.Net;
using EnderPearl.Resource;
using global::Protocol.Packets;
using ResourcePackClientResponsePayload = global::Protocol.Types.ResourcePackClientResponsePacketPayload;

namespace EnderPearl.Backend
{
	/// <summary>
	/// Switch-reset world-state capture, resource-pack merge forwarding, respawn acks and
	/// definition-state sync (port of BackendRelayPacketHandler.java lines 1271-1800 plus
	/// syncDefinitionState at 2366-2543).
	/// </summary>
	public sealed partial class BackendRelayPacketHandler
	{
		private static bool SuppressWorldStateDuringSwitchReset(IPacket packet)
		{
			if (packet is DisconnectPacket || packet is PlayStatusPacket)
			{
				return false;
			}
			if (packet is RespawnPacket
				|| packet is LevelChunkPacket
				|| packet is NetworkChunkPublisherUpdatePacket)
			{
				return true;
			}
			// The Java original matched on class simple names; this codec renames its entity packets
			// (*Entity* -> *Actor*), so the same suppression set is expressed as type patterns.
			// AddHangingEntityPacket has no counterpart at protocol 2168 - hanging entities arrive as
			// ordinary actors via AddActorPacket.
			return packet switch
			{
				AddActorPacket => true,
				AddPaintingPacket => true,
				AddItemActorPacket => true,
				AddPlayerPacket => true,
				AnimatePacket => true,
				BlockEventPacket => true,
				ActorPickRequestPacket => true,
				ChunkRadiusUpdatedPacket => true,
				ClientboundMapItemDataPacket => true,
				CorrectPlayerMovePredictionPacket => true,
				CurrentStructureFeaturePacket => true,
				ActorEventPacket => true,
				LevelEventPacket => true,
				LevelEventGenericPacket => true,
				LevelSoundEventPacket => true,
				MoveActorAbsolutePacket => true,
				MoveActorDeltaPacket => true,
				MovePlayerPacket => true,
				RemoveActorPacket => true,
				SetActorDataPacket => true,
				SetActorLinkPacket => true,
				SetActorMotionPacket => true,
				SetHealthPacket => true,
				SetTitlePacket => true,
				SubChunkPacket => true,
				TakeItemActorPacket => true,
				UpdateAttributesPacket => true,
				UpdateBlockPacket => true,
				UpdateBlockSyncedPacket => true,
				UpdateSubChunkBlocksPacket => true,
				_ => false
			};
		}

		/// <summary>
		/// The backend emits the local player's authoritative state (entity metadata, attributes such
		/// as health/hunger/movement speed, and current health) exactly once, in the join burst right
		/// after StartGame. During a backend switch that burst arrives while <see cref="BackendSwitchReset"/>
		/// is suppressing world-state packets, so without this capture those packets are dropped and
		/// never replayed, leaving the player with stale state after the switch (wrong/zero health,
		/// frozen movement, unable to interact). We translate and stash a client-ready copy here and
		/// replay it once the switch reset completes.
		/// </summary>
		private void CaptureSwitchResetPlayerState(IPacket packet)
		{
			if (!IsLocalPlayerStatePacket(packet))
			{
				return;
			}
			IPacket? translated = connection.SessionProfile.Translator.TranslateClientbound(
				RewriteClientboundRuntimeIds(packet)!,
				connection.SessionProfile.TranslationContext());
			if (translated == null)
			{
				return;
			}
			// ReferenceCountUtil.retain(translated): these packets are plain managed objects, so
			// stashing them as-is is the whole job.
			connection.AddDeferredSwitchPlayerState(translated);
			if (connection.IsPacketTraceActive())
			{
				Logger.Info(
					$"Captured local-player state during switch reset for {backendName} to replay after spawn: {packet.GetType().Name}.");
			}
		}

		/// <summary>
		/// The backend streams each chunk to a player exactly once - once it is in that player's chunk
		/// view it is never re-sent unless it leaves and re-enters the view radius. A backend whose
		/// spawn area is already loaded and cheap to serialize (a skyblock or otherwise mostly-empty
		/// world) can therefore deliver everything around the player within a few hundred ms of
		/// StartGame, well inside the switch reset's dimension-bounce window. Dropping that burst
		/// strands the player in a void the backend will never refill, so buffer it here and let
		/// <see cref="BackendSwitchReset"/> replay it once the client is back in the target dimension.
		///
		/// <p>Returns whether the packet was captured for replay.</p>
		/// </summary>
		private bool CaptureSwitchResetWorldState(IPacket packet)
		{
			if (!IsDeferrableWorldStatePacket(packet))
			{
				return false;
			}
			IPacket? translated = connection.SessionProfile.Translator.TranslateClientbound(
				RewriteClientboundRuntimeIds(packet)!,
				connection.SessionProfile.TranslationContext());
			if (translated == null)
			{
				return false;
			}
			// ReferenceCountUtil.retain/release: nothing to retain or release for managed packets;
			// when the buffer refuses the packet, dropping the reference is the whole job.
			if (connection.AddDeferredSwitchWorldState(translated))
			{
				return true;
			}
			return false;
		}

		/// <summary>
		/// World geometry the backend will not resend on its own. Deliberately excludes entity spawns
		/// and movement - the backend re-announces entities as they tick back into view, so replaying
		/// stale copies of those would fight the live stream rather than fill a gap.
		/// </summary>
		private static bool IsDeferrableWorldStatePacket(IPacket packet)
		{
			return packet is LevelChunkPacket
				|| packet is SubChunkPacket
				|| packet is NetworkChunkPublisherUpdatePacket
				|| packet is UpdateBlockPacket
				|| packet is UpdateBlockSyncedPacket
				|| packet is UpdateSubChunkBlocksPacket;
		}

		private bool IsLocalPlayerStatePacket(IPacket packet)
		{
			long playerRuntimeEntityId = connection.BackendPlayerRuntimeEntityId();
			if (packet is UpdateAttributesPacket attributes)
			{
				return playerRuntimeEntityId > 0 && (long)(attributes.TargetRuntimeID?.Value ?? 0UL) == playerRuntimeEntityId;
			}
			if (packet is SetActorDataPacket entityData)
			{
				return playerRuntimeEntityId > 0 && (long)(entityData.TargetRuntimeID?.Value ?? 0UL) == playerRuntimeEntityId;
			}
			return packet is SetHealthPacket;
		}


		private long UnknownRuntimeEntityUpdate(IPacket packet)
		{
			long runtimeEntityId = RuntimeEntityIdForExistingEntity(packet);
			return runtimeEntityId > 0 && !connection.HasBackendRuntimeEntityId(runtimeEntityId)
				? runtimeEntityId
				: 0;
		}

		private static long RuntimeEntityIdForExistingEntity(IPacket packet)
		{
			if (packet is MoveActorDeltaPacket moveActorDelta)
			{
				return (long)(moveActorDelta.MoveData?.ActorRuntimeID?.Value ?? 0UL);
			}
			if (packet is MoveActorAbsolutePacket moveActorAbsolute)
			{
				return (long)(moveActorAbsolute.MoveData?.ActorRuntimeID?.Value ?? 0UL);
			}
			if (packet is MovePlayerPacket movePlayer)
			{
				return (long)(movePlayer.PlayerRuntimeID?.Value ?? 0UL);
			}
			if (packet is SetActorDataPacket actorData)
			{
				return (long)(actorData.TargetRuntimeID?.Value ?? 0UL);
			}
			if (packet is SetActorMotionPacket actorMotion)
			{
				return (long)(actorMotion.TargetRuntimeID?.Value ?? 0UL);
			}
			if (packet is UpdateAttributesPacket attributes)
			{
				return (long)(attributes.TargetRuntimeID?.Value ?? 0UL);
			}
			if (packet is ActorEventPacket actorEvent)
			{
				return (long)(actorEvent.TargetRuntimeID?.Value ?? 0UL);
			}
			// EntityFallPacket does not exist at protocol 2168, so that branch has no counterpart.
			if (packet is AnimatePacket animate)
			{
				return (long)(animate.TargetActorRuntimeID?.Value ?? 0UL);
			}
			if (packet is MovementEffectPacket movementEffect)
			{
				return (long)(movementEffect.TargetRuntimeID?.Value ?? 0UL);
			}
			// Java read MovementPredictionSyncPacket.getRuntimeEntityId(); protocol 2168's
			// ClientMovementPredictionSyncPacket carries only an ActorUniqueID, whose id space is not
			// the one HasBackendRuntimeEntityId tracks, so this packet cannot be checked here and is
			// always forwarded instead.
			if (packet is UpdateBlockSyncedPacket blockSynced)
			{
				// Java read the packet's runtime entity id; protocol 2168 carries the syncing actor
				// as an unsigned unique id varint.
				return unchecked((long)(blockSynced.UniqueActorId));
			}
			if (packet is TakeItemActorPacket takeItem)
			{
				return (long)(takeItem.ItemRuntimeID?.Value ?? 0UL);
			}
			return 0;
		}

		private void ClearPreviousClientWorldState()
		{
			List<IPacket> cleanupPackets = connection.ClientWorldState.ClearPackets();
			if (cleanupPackets.Count > 0 && connection.IsPacketTraceActive())
			{
				Logger.Info(
					$"Clearing previous backend client-world state before switching to {backendName} with {cleanupPackets.Count} packets.");
			}
			foreach (IPacket cleanupPacket in cleanupPackets)
			{
				IPacket? translated = connection.SessionProfile.Translator.TranslateClientbound(
					cleanupPacket,
					connection.SessionProfile.TranslationContext());
				if (translated == null)
				{
					Logger.Info(
						$"WARNING: Skipping previous-world cleanup packet after protocol translation for client protocol {connection.SessionProfile.ClientCodec.ProtocolVersion}: {cleanupPacket.GetType().Name}.");
					continue;
				}
				// ReferenceCountUtil.retain(translated): managed packets need no retain.
				connection.Client().SendPacket(translated);
			}
			SendForceCloseInventory();
		}

		/// <summary>
		/// WaterdogPE's injectForceCloseInventory: ContainerClosePacket cannot close the player's OWN
		/// inventory window, and a client whose own window survived a switch refuses to open any
		/// inventory afterwards. The SLEEPING flag makes it shut every window including its own; the
		/// first authoritative SetActorData the new backend sends about the player (forwarded, or
		/// captured during the reset window and replayed) carries real flags and clears it again.
		/// </summary>
		private void SendForceCloseInventory()
		{
			long clientRuntimeEntityId = connection.ClientPlayerRuntimeEntityId();
			if (clientRuntimeEntityId <= 0)
			{
				return;
			}
			var poke = new SetActorDataPacket();
			poke.TargetRuntimeID = new global::Protocol.Types.ActorRuntimeID { Value = unchecked((ulong)clientRuntimeEntityId) };
			// SLEEPING is ENTITY_FLAGS typemap id 74 (Bedrock_v340 ".insert(74, ...)"; NOT the enum
			// ordinal, which is 75 in this lib): past bit 63, so it lives in the SECOND flags group -
			// metadata id 91 (EntityDataTypes.FLAGS_2), bit (74 & 63) = 10. Writing it into entry 0
			// would set a meaningless high bit of group 0 and the client would never sleep.
			var flags = new global::Protocol.Types.DataItemInt64Payload
			{
				Type_ = global::Protocol.DataItemType.Int64,
				Value = 1L << (74 & 0x3F) // SLEEPING, group-1 bit position
			};
			var dataEntry = new global::Protocol.Types.DataItemEntry
			{
				ID = 91, // EntityDataTypes.FLAGS_2 (LONG format; ids 0/91 are flag groups 0/1)
				Type_ = global::Protocol.DataItemType.Int64,
				Payload = OneOf.OneOf<
					global::Protocol.Types.DataItemBytePayload,
					global::Protocol.Types.DataItemShortPayload,
					global::Protocol.Types.DataItemIntPayload,
					global::Protocol.Types.DataItemFloatPayload,
					global::Protocol.Types.DataItemStringPayload,
					global::Protocol.Types.DataItemCompoundTagPayload,
					global::Protocol.Types.DataItemPosPayload,
					global::Protocol.Types.DataItemInt64Payload,
					global::Protocol.Types.DataItemVec3Payload>.FromT7(flags)
			};
			poke.ActorData = new global::Protocol.Types.SynchedActorData.CopyableDataList();
			poke.ActorData.Data.Add(dataEntry);
			poke.SynchedProperties = new global::Protocol.Types.PropertySyncData();
			poke.Tick = new global::Protocol.Types.PlayerInputTick();
			connection.Client().SendPacket(poke);
		}

		private bool AcknowledgePendingSwitchLoginPacket(IPacket packet)
		{
			if (packFetch != null && !packFetch.IsFinished() && packFetch.Handle(packet))
			{
				return true;
			}
			if (packet is ResourcePacksInfoPacket packsInfo)
			{
				// Protocol 2168 folds behaviour packs into the single ResourcePacks list, so the
				// Java resource+behaviour count pair becomes one count here.
				int packCount = packsInfo.ResourcePacks?.Count ?? 0;
				if (packCount > 0)
				{
					if (connection.IsPacketTraceActive())
					{
						Logger.Info(
							$"Acknowledging {packCount} resource packs for pending backend {backendName} during switch.");
					}
					WarnAboutUnservedSwitchPacks(packsInfo);
				}
				else if (connection.IsPacketTraceActive())
				{
					Logger.Info($"Acknowledging empty resource-pack info for pending backend {backendName} during switch.");
				}
				// The one moment this backend's packs can be obtained: nobody else will ever ask it for
				// them, because a client only downloads packs during its own login.
				BackendPackFetch? fetch = BackendPackFetch.Start(
					connection.BackendPackCache,
					backendName,
					packsInfo,
					sent => backend.SendPacket(sent),
					() => SendPackResponse(global::Protocol.ResourcePackResponse.DownloadingFinished)
				);
				if (fetch != null)
				{
					packFetch = fetch;
					SchedulePackFetchDeadline(fetch);
					return true;
				}
				// ResourcePackClientResponsePacket.Status.HAVE_ALL_PACKS.
				SendPackResponse(global::Protocol.ResourcePackResponse.DownloadingFinished);
				return true;
			}
			if (packet is ResourcePackStackPacket)
			{
				if (connection.IsPacketTraceActive())
				{
					Logger.Info($"Completing resource-pack stack for pending backend {backendName} during switch.");
				}
				// ResourcePackClientResponsePacket.Status.COMPLETED.
				SendPackResponse(global::Protocol.ResourcePackResponse.ResourcePackStackFinished);
				return true;
			}
			return false;
		}

		/// <summary>
		/// Warns when a backend joined mid-session advertises packs the proxy is not serving itself.
		///
		/// <p>Bedrock runs the resource-pack handshake exactly once, before StartGame. A client already
		/// in a world cannot be made to fetch and apply a new pack stack, so on a switch the proxy has
		/// to answer that handshake on the client's behalf - the packs are acknowledged to the backend
		/// and the client never sees them. If those packs are also in <c>resourcePacks.dir</c> the
		/// client already downloaded and applied them at login and everything renders; if they are not,
		/// the player gets the backend's custom content with no client-side definitions: custom
		/// entities render as nothing (still solid and clickable) and custom items fall back to
		/// arbitrary vanilla textures.</p>
		///
		/// <p>That failure is otherwise completely silent - no kick, no error, just wrong visuals - so
		/// name the specific packs that need copying into <c>resourcePacks.dir</c>.</p>
		/// </summary>
		private void WarnAboutUnservedSwitchPacks(ResourcePacksInfoPacket packsInfo)
		{
			ProxyResourcePackRegistry registry = connection.ProxyResourcePackRegistry;
			List<string> unserved = new List<string>();
			foreach (global::Protocol.Types.PackInfoData entry in packsInfo.ResourcePacks)
			{
				Guid? packUuid = PackUuidOf(entry.PackIdVersion?.PackUUID);
				if (packUuid == null || !registry.IsProxyPack(packUuid.Value))
				{
					unserved.Add($"{packUuid?.ToString() ?? "null"} v{entry.PackIdVersion?.PackVersion?.Version}");
				}
			}
			if (unserved.Count == 0)
			{
				return;
			}
			Logger.Info(
				$"WARNING: backend {backendName} uses {unserved.Count} resource pack(s) the proxy does not serve, and a switched "
					+ $"client cannot be asked to download them: {string.Join(", ", unserved)}. Custom entities will be invisible and "
					+ $"custom items will show wrong textures on this backend. Copy these packs into "
					+ $"resourcePacks.dir so every client gets them at login."
			);
		}

		/// <summary>
		/// Keeps a copy of a pack the client is downloading from the backend, so the next player gets it
		/// from the proxy — including on backends they reach by switching, where no client can download
		/// anything.
		///
		/// <para>Pure observation: the packets carry on to the client untouched. A client that already has
		/// the pack in its own cache never requests it, so nothing is learned from that join; the switch
		/// path (<see cref="BackendPackFetch"/>) is what guarantees a backend is eventually learned.</para>
		/// </summary>
		private void CaptureBackendPackBytes(IPacket packet)
		{
			BackendPackCache cache = connection.BackendPackCache;
			if (!cache.IsEnabled())
			{
				return;
			}
			if (packet is ResourcePackDataInfoPacket dataInfo)
			{
				if (!TryParsePackIdAndVersion(dataInfo.ResourceName, out Guid dataId, out int[] version))
				{
					return;
				}
				observedPacks.Remove(dataId);
				long size = (long)dataInfo.FileSize;
				if (size <= 0 || size > BackendPackCache.MAX_PACK_BYTES || cache.Has(dataId, version))
				{
					return;
				}
				observedPacks[dataId] = new ObservedPack(
					new byte[size], dataInfo.FileHash, Math.Max(1u, dataInfo.ChunkSize));
				return;
			}
			if (!(packet is ResourcePackChunkDataPacket chunkData))
			{
				return;
			}
			// The wire id is "uuid" or "uuid_version" - UUIDs themselves use hyphens, not underscores.
			string chunkIdPart = chunkData.ResourceName;
			int underscore = chunkIdPart.IndexOf('_');
			string chunkUuidPart = underscore >= 0 ? chunkIdPart.Substring(0, underscore) : chunkIdPart;
			if (!Guid.TryParseExact(chunkUuidPart, "D", out Guid chunkId)
				|| !observedPacks.TryGetValue(chunkId, out ObservedPack? observed))
			{
				return;
			}
			byte[]? data = chunkData.ChunkData;
			long offsetLong = Math.Min(observed.Buffer.Length, (long)chunkData.ChunkID * observed.ChunkSize);
			int offset = (int)Math.Max(0, Math.Min(offsetLong, int.MaxValue));
			int length = data == null ? 0 : Math.Min(data.Length, observed.Buffer.Length - offset);
			if (length > 0 && offset < observed.Buffer.Length)
			{
				// Copy: this buffer is on its way to the client and must not be moved.
				Buffer.BlockCopy(data, 0, observed.Buffer, offset, length);
				observed.Filled += length;
			}
			if (observed.Filled >= observed.Buffer.Length)
			{
				observedPacks.Remove(chunkId);
				cache.Store(chunkId, observed.Buffer, observed.Hash);
			}
		}

		private static bool TryParsePackIdAndVersion(string resourceName, out Guid packId, out int[] version)
		{
			// The wire id is "uuid_version"; Java split it into getPackId()+getPackVersion().
			packId = Guid.Empty;
			version = ProxyResourcePackRegistry.ParseVersion(null);
			if (string.IsNullOrEmpty(resourceName))
			{
				return false;
			}
			int underscore = resourceName.IndexOf('_');
			string uuidPart = underscore >= 0 ? resourceName.Substring(0, underscore) : resourceName;
			if (!Guid.TryParseExact(uuidPart, "D", out packId))
			{
				return false;
			}
			if (underscore >= 0)
			{
				version = ProxyResourcePackRegistry.ParseVersion(resourceName.Substring(underscore + 1));
			}
			return true;
		}

		private sealed class ObservedPack
		{
			public readonly byte[] Buffer;
			public readonly byte[] Hash;
			public readonly long ChunkSize;
			public int Filled;

			public ObservedPack(byte[] buffer, byte[] hash, long chunkSize)
			{
				Buffer = buffer;
				Hash = hash;
				ChunkSize = Math.Min(int.MaxValue, chunkSize);
			}
		}

		/// <summary>
		/// Bounds the pack download in time. The player switching is waiting on it, so a backend that
		/// stops answering must not hold them there: the fetch is dropped and the handshake completes with
		/// the packs still unlearned, which is exactly the state the proxy was in before it tried.
		/// </summary>
		private void SchedulePackFetchDeadline(BackendPackFetch fetch)
		{
			var timer = new System.Threading.Timer(_ =>
			{
				try
				{
					if (!fetch.IsFinished())
					{
						fetch.Abandon("the backend stopped sending after " + PACK_FETCH_TIMEOUT_MILLIS + "ms");
					}
				}
				catch (Exception e)
				{
					Logger.Error($"pack fetch deadline callback failed: {e}");
				}
			}, null, TimeSpan.FromMilliseconds(PACK_FETCH_TIMEOUT_MILLIS), Timeout.InfiniteTimeSpan);
			lock (packFetchTimers)
			{
				packFetchTimers.Add(timer);
			}
		}

		/// <summary>
		/// This codec splits the Cloudburst Status enum into a wire discriminant
		/// (<see cref="global::Protocol.ResourcePackResponse"/>) plus a typed payload union whose
		/// members share the discriminant values: HAVE_ALL_PACKS(3) -> DownloadingFinished,
		/// COMPLETED(4) -> ResourcePackStackFinished.
		/// </summary>
		private void SendPackResponse(global::Protocol.ResourcePackResponse status)
		{
			ResourcePackClientResponsePacket response = new ResourcePackClientResponsePacket();
			switch (status)
			{
				case global::Protocol.ResourcePackResponse.Cancel:
				{
					response.Response = OneOf.OneOf<
						ResourcePackClientResponsePayload.Cancel,
						ResourcePackClientResponsePayload.Downloading,
						ResourcePackClientResponsePayload.DownloadingFinished,
						ResourcePackClientResponsePayload.ResourcePackStackFinished>.FromT0(
						new ResourcePackClientResponsePayload.Cancel { ResponseType = "" });
					break;
				}
				case global::Protocol.ResourcePackResponse.Downloading:
				{
					response.Response = OneOf.OneOf<
						ResourcePackClientResponsePayload.Cancel,
						ResourcePackClientResponsePayload.Downloading,
						ResourcePackClientResponsePayload.DownloadingFinished,
						ResourcePackClientResponsePayload.ResourcePackStackFinished>.FromT1(
						new ResourcePackClientResponsePayload.Downloading { ResponseType = "" });
					break;
				}
				case global::Protocol.ResourcePackResponse.DownloadingFinished:
				{
					response.Response = OneOf.OneOf<
						ResourcePackClientResponsePayload.Cancel,
						ResourcePackClientResponsePayload.Downloading,
						ResourcePackClientResponsePayload.DownloadingFinished,
						ResourcePackClientResponsePayload.ResourcePackStackFinished>.FromT2(
						new ResourcePackClientResponsePayload.DownloadingFinished { ResponseType = "" });
					break;
				}
				case global::Protocol.ResourcePackResponse.ResourcePackStackFinished:
				{
					response.Response = OneOf.OneOf<
						ResourcePackClientResponsePayload.Cancel,
						ResourcePackClientResponsePayload.Downloading,
						ResourcePackClientResponsePayload.DownloadingFinished,
						ResourcePackClientResponsePayload.ResourcePackStackFinished>.FromT3(
						new ResourcePackClientResponsePayload.ResourcePackStackFinished { ResponseType = "" });
					break;
				}
			}
			backend.SendPacket(response);
		}

		private void HandleMergedResourcePacksInfo(ResourcePacksInfoPacket backendInfo)
		{
			ProxyResourcePackRegistry registry = connection.ProxyResourcePackRegistry;
			ResourcePacksInfoPacket merged = registry.BuildMergedInfo(backendInfo);
			// Deliberately silent. The merge runs on every join and always did the same thing, so the
			// line said nothing an operator could act on. The pack problem that *is* worth reporting -
			// a backend serving packs the proxy does not have - still warns, from warnAboutUnservedSwitchPacks.
			// Forward merged info to client; client responses flow back through ClientRelayPacketHandler.
			// Proxy pack chunks are served locally there; backend pack chunks are forwarded to backend.
			connection.Client().SendPacket(merged);
		}

		private void HandleMergedResourcePackStack(ResourcePackStackPacket backendStack)
		{
			ProxyResourcePackRegistry registry = connection.ProxyResourcePackRegistry;
			ResourcePackStackPacket merged = registry.BuildMergedStack(backendStack);
			// Silent for the same reason as HandleMergedResourcePacksInfo above.
			// Send merged stack to client; the client's COMPLETED response will flow normally
			// through ClientRelayPacketHandler to the backend.
			connection.Client().SendPacket(merged);
		}

		private void SendSwitchWorldReadyPackets(StartGamePacket startGame, int sourceDimension)
		{
			RequestChunkRadiusPacket chunkRadius = new RequestChunkRadiusPacket();
			chunkRadius.ChunkRadius = connection.LastRequestedChunkRadius();
			chunkRadius.MaxChunkRadius = (byte)Math.Clamp(connection.LastRequestedMaxChunkRadius(), byte.MinValue, byte.MaxValue);
			backend.SendPacket(chunkRadius);

			BackendSwitchReset.Start(
				connection,
				backend,
				backendName,
				sourceDimension,
				startGame,
				backendInputLockData
			);

			if (connection.IsPacketTraceActive())
			{
				Logger.Info(
					$"Sent switch chunk-radius to backend {backendName} and deferred player initialization until dimension reset ack: chunkRadius={chunkRadius.ChunkRadius} maxRadius={chunkRadius.MaxChunkRadius} runtimeEntityId={(long)(startGame.RuntimeID?.Value ?? 0UL)}.");
			}
		}

		private void AcknowledgeRespawn(global::Protocol.PlayerRespawnState state, global::Protocol.Types.Vec3 position)
		{
			if (state == global::Protocol.PlayerRespawnState.ClientReadyToSpawn)
			{
				return;
			}
			RespawnPacket ready = new RespawnPacket();
			ready.State = global::Protocol.PlayerRespawnState.ClientReadyToSpawn;
			ready.Position = position;
			ready.PlayerRuntimeId = new global::Protocol.Types.ActorRuntimeID { Value = unchecked((ulong)connection.BackendPlayerRuntimeEntityId()) };
			backend.SendPacket(ready);
			if (connection.IsPacketTraceActive())
			{
				Logger.Info(
					$"Acknowledged respawn for backend {backendName}: state={state} runtimeEntityId={ready.PlayerRuntimeId?.Value ?? 0UL} position=({ready.Position?.X ?? 0f}, {ready.Position?.Y ?? 0f}, {ready.Position?.Z ?? 0f}).");
			}
		}

		private void SyncDefinitionState(IPacket packet)
		{
			if (packet is StartGamePacket startGame)
			{
				if (ReferenceEquals(backend, connection.PendingBackend()))
				{
					activation.OnStartGame(backend);
				}
				long backendRuntimeEntityId = (long)(startGame.RuntimeID?.Value ?? 0UL);
				connection.SetBackendPlayerRuntimeEntityId(backendRuntimeEntityId);
				long clientRuntimeEntityId = connection.ClientPlayerRuntimeEntityId();
				if (clientRuntimeEntityId > 0 && clientRuntimeEntityId != backendRuntimeEntityId)
				{
					startGame.RuntimeID.Value = unchecked((ulong)clientRuntimeEntityId);
					startGame.InvalidateWireCache();
				}
				long backendUniqueEntityId = startGame.EntityID?.Value ?? 0L;
				connection.SetBackendPlayerUniqueEntityId(backendUniqueEntityId);
				long clientUniqueEntityId = connection.ClientPlayerUniqueEntityId();
				if (clientUniqueEntityId != backendUniqueEntityId)
				{
					startGame.EntityID.Value = clientUniqueEntityId;
					startGame.InvalidateWireCache();
				}
				int startGameDimension = startGame.Settings?.SpawnSettings?.Dimension ?? 0;
				connection.SetPlayerDimensionId(startGameDimension);
				// CodecDefinitionState.syncFromStartGame: this build's protocol library keeps no
				// per-session definition registries (see EnderPearl.Codec.CodecDefinitionState), so the
				// sync shim has nothing to install and the call is deliberately omitted. (Java now also
				// skips this sync when the cross-backend palette is enabled; the block-properties half of
				// that behaviour runs from HandleCrossBackendPalette above.)
				connection.TracePacketsForMillis(ProxyConnection.ConfiguredPacketTraceMillis());
				if (ProxyConnection.ConfiguredPacketTraceMillis() > 0)
				{
					Logger.Info(
						$"Enabled detailed packet trace for {connection.Client().RemoteEndPoint} for {ProxyConnection.ConfiguredPacketTraceMillis()}ms after StartGame at +{connection.ElapsedMillis()}ms.");
				}
				if (connection.IsPacketTraceActive())
				{
					Logger.Info(
						$"StartGame from backend {backendName}: dimension={startGameDimension} backendRuntimeEntityId={backendRuntimeEntityId} clientRuntimeEntityId={(long)(startGame.RuntimeID?.Value ?? 0UL)} backendUniqueEntityId={backendUniqueEntityId} clientUniqueEntityId={startGame.EntityID?.Value ?? 0L} playerGameType={startGame.GameType} levelGameType={startGame.Settings?.GameType} playerPosition=({startGame.Position?.X ?? 0f}, {startGame.Position?.Y ?? 0f}, {startGame.Position?.Z ?? 0f}) defaultSpawn=({startGame.Settings?.DefaultSpawnBlockPosition?.X ?? 0}, {startGame.Settings?.DefaultSpawnBlockPosition?.Y ?? 0}, {startGame.Settings?.DefaultSpawnBlockPosition?.Z ?? 0}) commandsEnabled={startGame.Settings?.CommandsEnabled ?? false} defaultPermission={startGame.Settings?.PlayerPermissions} blockRegistryChecksum={startGame.ServerBlockTypeRegistryChecksum} blockNetworkIdsHashed={startGame.BlockNetworkIdsAreHashes}."
					);
				}
				if (connection.IsPacketTraceActive())
				{
					// Java also printed serverEngine, itemDefinitions, inventoriesServerAuth and
					// tickDeathSystems here; protocol 2168's StartGamePacket carries no such fields.
					// vanillaVersion maps onto the ServerVersion string this codec does carry.
					// rewindHistorySize/serverAuthBlockBreaking live inside MovementSettings - and the
					// latter decides whether the client breaks blocks via PlayerAuthInput or falls back
					// to legacy PlayerActionPacket, so it is always worth seeing in the trace.
					Logger.Info(
						$"StartGame details from backend {backendName}: vanillaVersion={startGame.ServerVersion} levelName={startGame.LevelName} blockProperties={startGame.BlockProperties?.Count ?? 0} clientSideGeneration={startGame.ServerEnabledClientSideGeneration} serverJoinInfo={startGame.ServerConfigurationJoinInfo != null && startGame.ServerConfigurationJoinInfo.HasValue} networkPermissions={startGame.NetworkPermissions != null} rewindHistorySize={startGame.MovementSettings?.RewindHistorySize} serverAuthBlockBreaking={startGame.MovementSettings?.ServerAuthoritativeBlockBreaking}."
					);
				}
				StartGameClientFixups fixups = StartGameClientFixups.Apply(startGame);
				if (fixups.ForcedTickDeathSystems)
				{
					Logger.Info(
						$"Forced tickDeathSystems=true for backend {backendName}; the backend reported false, "
							+ $"which makes the client disconnect on death."
					);
				}
				if (fixups.EnabledCommands)
				{
					Logger.Info($"Enabled client-side commands for backend {backendName}.");
				}
			}
			else if (packet is CameraPresetsPacket cameraPresets)
			{
				// Java had two further sync branches here: ItemComponentPacket via
				// CodecDefinitionState.syncFromItemComponents (skipped there too when the cross-backend
				// palette is enabled - the item half of that behaviour runs from
				// HandleCrossBackendPalette), and CameraPresetsPacket via
				// CodecDefinitionState.syncFromCameraPresets. This build keeps no per-session
				// definition registries, so both syncs have nothing to install and neither branch
				// carries behaviour.
				_ = cameraPresets;
			}
			else if (packet is ChangeDimensionPacket changeDimension)
			{
				connection.SetPlayerDimensionId(changeDimension.DimensionID?.Value ?? 0);
			}
			else if (packet is SetCommandsEnabledPacket commandsEnabled)
			{
				if (!commandsEnabled.CommandsEnabled)
				{
					commandsEnabled.CommandsEnabled = true;
					commandsEnabled.InvalidateWireCache();
					Logger.Info($"Overrode SetCommandsEnabled=false from backend {backendName}.");
				}
			}
			else if (packet is UpdateAbilitiesPacket abilities)
			{
				BackendPermissionSync.Apply(abilities);
				if (connection.IsPacketTraceActive())
				{
					string layers = abilities.Data == null
						? ""
						: string.Join(",", abilities.Data.Layers.ConvertAll(layer => layer.SerializedLayer_ + ":" + layer.AbilityValues));
					Logger.Info(
						$"UpdateAbilities from backend {backendName}: playerPermission={abilities.Data?.PlayerPermissions} commandPermission={abilities.Data?.CommandPermissions} layers={layers}."
					);
				}
				// The backend's individual ADMIN/HOST/OWNER command level corrects the MEMBER world
				// default above. Ordinary members and explicit visitor/custom permissions are untouched.
			}
		}

		/// <summary>Mirror of ProxyResourcePackRegistry's private MceUuid -> Guid conversion.</summary>
		private static Guid? PackUuidOf(global::Protocol.Types.mce.UUID uuid)
		{
			if (uuid == null)
			{
				return null;
			}
			Span<byte> bytes = stackalloc byte[16];
			ulong mostSignificantBits = uuid.MostSignificantBits;
			ulong leastSignificantBits = uuid.LeastSignificantBits;
			for (int i = 7; i >= 0; i--)
			{
				bytes[i] = (byte)mostSignificantBits;
				mostSignificantBits >>= 8;
			}
			for (int i = 15; i >= 8; i--)
			{
				bytes[i] = (byte)leastSignificantBits;
				leastSignificantBits >>= 8;
			}
			return new Guid(bytes);
		}
	}
}
