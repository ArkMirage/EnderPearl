using System;
using EnderPearl.Core;
using System.Collections.Generic;
using System.Threading;
using global::Protocol.Packets;
using ResourcePackClientResponsePayload = global::Protocol.Types.ResourcePackClientResponsePacketPayload;
using EnderPearl.Backend;
using EnderPearl.Player;

namespace EnderPearl.Relay;
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
	/// frozen movement, unable to interact). We rewrite and stash a client-ready copy here and
	/// replay it once the switch reset completes.
	/// </summary>
	private void CaptureSwitchResetPlayerState(IPacket packet)
	{
		if (!IsLocalPlayerStatePacket(packet))
		{
			return;
		}
		Connection.AddDeferredSwitchPlayerState(Rewriter.RewriteClientbound(packet, BackendName));
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
		// ReferenceCountUtil.retain/release: nothing to retain or release for managed packets;
		// when the buffer refuses the packet, dropping the reference is the whole job.
		return Connection.AddDeferredSwitchWorldState(Rewriter.RewriteClientbound(packet, BackendName));
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
		long playerRuntimeEntityId = Connection.BackendPlayerRuntimeEntityId();
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
		return runtimeEntityId > 0 && !Connection.HasBackendRuntimeEntityId(runtimeEntityId)
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
		List<IPacket> cleanupPackets = Connection.ClientWorldState.ClearPackets();
		foreach (IPacket cleanupPacket in cleanupPackets)
		{
			Connection.Client.SendPacket(cleanupPacket);
		}
		SendTargetBackendWeather();
		SendForceCloseInventory();
		SendCameraReset();
	}

	/// <summary>
	/// Clears any camera the backend being left had set on the client - a cutscene camera, a fixed
	/// boom, a first-person lock. The client keeps that camera across a seamless handoff, and the
	/// target backend never removes what it does not know about, so without this the player stays
	/// looking through the old server's camera until something on the new one happens to overwrite
	/// it. Sent before the target's StartGame is forwarded, so a camera the new backend installs in
	/// its own join burst still wins.
	/// </summary>
	private void SendCameraReset()
	{
		Connection.Client.SendPacket(new CameraInstructionPacket
		{
			CameraInstruction = new global::Protocol.Types.CameraInstruction
			{
				Clear = new Optional<bool>(true)
			}
		});
	}

	/// <summary>
	/// Puts the client's sky where the backend it is entering says it should be.
	///
	/// <para>Unconditionally stopping the rain (WaterdogPE's injectClearWeather) is only correct while
	/// nobody has ever heard this backend report its weather: the world being left may still be raining,
	/// and a backend that never sends rain never sends the stop either. Once this backend has reported a
	/// sky, replaying that state is what keeps a switching player in sync instead of stranding them in
	/// fair weather until the backend's next change.</para>
	/// </summary>
	private void SendTargetBackendWeather()
	{
		bool known = BackendWeather.TryGet(BackendName, out bool raining, out bool thundering);
		SendWeatherEvents(known && raining, known && thundering);
	}

	/// <summary>
	/// The same replay for a client being handed this backend without leaving another one behind.
	/// There is no previous world whose weather could still be rendering, so while nothing has ever
	/// been heard about this backend the honest thing to send is nothing at all.
	/// </summary>
	private void ReplayKnownBackendWeather()
	{
		if (BackendWeather.TryGet(BackendName, out bool raining, out bool thundering))
		{
			SendWeatherEvents(raining, thundering);
		}
	}

	/// <summary>
	/// STOP_RAINING carries data 10000 (gradual fade) and every other weather event 0, both at origin -
	/// Java's exact values. Wire ids are the LEVEL_EVENTS TypeMap entries, which this codec writes
	/// verbatim, NOT the cloudburst enum ordinals.
	/// </summary>
	private void SendWeatherEvents(bool raining, bool thundering)
	{
		Connection.Client.SendPacket(new LevelEventPacket
		{
			EventId = thundering ? BackendWeather.START_THUNDERSTORM : BackendWeather.STOP_THUNDERSTORM,
			Position = new global::Protocol.Types.Vec3 { X = 0f, Y = 0f, Z = 0f },
			Data = 0
		});
		Connection.Client.SendPacket(new LevelEventPacket
		{
			EventId = raining ? BackendWeather.START_RAINING : BackendWeather.STOP_RAINING,
			Position = new global::Protocol.Types.Vec3 { X = 0f, Y = 0f, Z = 0f },
			Data = raining ? 0 : 10000
		});
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
		long clientRuntimeEntityId = Connection.ClientPlayerRuntimeEntityId();
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
		Connection.Client.SendPacket(poke);
	}

	private bool AcknowledgePendingSwitchLoginPacket(IPacket packet)
	{
		if (packet is ResourcePacksInfoPacket packsInfo)
		{
			SendPackResponse(global::Protocol.ResourcePackResponse.DownloadingFinished);
			return true;
		}
		if (packet is ResourcePackStackPacket)
		{
			// ResourcePackClientResponsePacket.Status.COMPLETED.
			SendPackResponse(global::Protocol.ResourcePackResponse.ResourcePackStackFinished);
			return true;
		}
		return false;
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
		Backend.SendPacket(response);
	}



	private void SendSwitchWorldReadyPackets(StartGamePacket startGame, int sourceDimension)
	{
		RequestChunkRadiusPacket chunkRadius = new RequestChunkRadiusPacket();
		chunkRadius.ChunkRadius = Connection.LastRequestedChunkRadius();
		chunkRadius.MaxChunkRadius = (byte)Math.Clamp(Connection.LastRequestedMaxChunkRadius(), byte.MinValue, byte.MaxValue);
		Backend.SendPacket(chunkRadius);

		BackendSwitchReset.Start(
			Connection,
			Backend,
			BackendName,
			sourceDimension,
			startGame,
			backendInputLockData
		);
	}

	private void AcknowledgeRespawn(global::Protocol.PlayerRespawnState state, global::Protocol.Types.Vec3 position)
	{
		if (state == global::Protocol.PlayerRespawnState.ClientReadyToSpawn)
		{
			return;
		}
		RespawnPacket ready = new RespawnPacket
		{
			State = global::Protocol.PlayerRespawnState.ClientReadyToSpawn,
			Position = position,
			PlayerRuntimeId = new global::Protocol.Types.ActorRuntimeID { Value = unchecked((ulong)Connection.BackendPlayerRuntimeEntityId()) }
		};
		Backend.SendPacket(ready);
	}

	private void SyncDefinitionState(IPacket packet)
	{
		if (packet is StartGamePacket startGame)
		{
			if (ReferenceEquals(Backend, Connection.PendingBackend()))
			{
				Activation.OnStartGame(Backend);
			}
			long backendRuntimeEntityId = (long)(startGame.RuntimeID?.Value ?? 0UL);
			Connection.SetBackendPlayerRuntimeEntityId(backendRuntimeEntityId);
			long clientRuntimeEntityId = Connection.ClientPlayerRuntimeEntityId();
			if (clientRuntimeEntityId > 0 && clientRuntimeEntityId != backendRuntimeEntityId)
			{
				startGame.RuntimeID.Value = unchecked((ulong)clientRuntimeEntityId);
				startGame.InvalidateWireCache();
			}
			long backendUniqueEntityId = startGame.EntityID?.Value ?? 0L;
			Connection.SetBackendPlayerUniqueEntityId(backendUniqueEntityId);
			long clientUniqueEntityId = Connection.ClientPlayerUniqueEntityId();
			if (clientUniqueEntityId != backendUniqueEntityId)
			{
				startGame.EntityID.Value = clientUniqueEntityId;
				startGame.InvalidateWireCache();
			}
			int startGameDimension = startGame.Settings?.SpawnSettings?.Dimension ?? 0;
			Connection.SetPlayerDimensionId(startGameDimension);
			// Java's syncFromStartGame installed per-session block/item/camera definition registries
			// here. This build's protocol library keeps no such state, so there is nothing to sync —
			// a definition a backend mentions but the proxy has never seen survives
			// re-serialization on its own. (Java now also skips this sync when the cross-backend
			// palette is enabled; the block-properties half of that behaviour runs from
			// HandleCrossBackendPalette above.)
			StartGameClientFixups fixups = StartGameClientFixups.Apply(startGame);
			if (fixups.ForcedTickDeathSystems)
			{
				Logger.Info(
					$"Forced tickDeathSystems=true for backend {BackendName}; the backend reported false, "
						+ $"which makes the client disconnect on death."
				);
			}
			if (fixups.EnabledCommands)
			{
				Logger.Info($"Enabled client-side commands for backend {BackendName}.");
			}
		}
		else if (packet is CameraPresetsPacket cameraPresets)
		{
			// Java had two further sync branches here: ItemComponentPacket via
			// syncFromItemComponents (skipped there too when the cross-backend palette is enabled -
			// the item half of that behaviour runs from HandleCrossBackendPalette), and
			// CameraPresetsPacket via syncFromCameraPresets. Both installed per-session definition
			// registries, which this build's protocol library does not keep, so neither branch
			// carries behaviour.
			_ = cameraPresets;
		}
		else if (packet is ChangeDimensionPacket changeDimension)
		{
			Connection.SetPlayerDimensionId(changeDimension.DimensionID?.Value ?? 0);
		}
		else if (packet is SetCommandsEnabledPacket commandsEnabled)
		{
			if (!commandsEnabled.CommandsEnabled)
			{
				commandsEnabled.CommandsEnabled = true;
				commandsEnabled.InvalidateWireCache();
				Logger.Info($"Overrode SetCommandsEnabled=false from backend {BackendName}.");
			}
		}
		else if (packet is UpdateAbilitiesPacket abilities)
		{
			BackendPermissionSync.Apply(abilities);
		}
	}
}
