using System;
using System.Collections.Generic;
using System.Globalization;
using EnderPearl.Diagnostics;
using EnderPearl.Net;
using global::Protocol.Packets;
using EnderPearl.Logging;

namespace EnderPearl.Backend
{
	/// <summary>
	/// Kick interception, packet-violation recording and clientbound tracing details
	/// (port of BackendRelayPacketHandler.java lines 2530-3202).
	/// </summary>
	public sealed partial class BackendRelayPacketHandler
	{
		/// <summary>
		/// The last <c>PacketViolationWarningPacket</c> this backend sent, if it was fatal.
		///
		/// <para>BDS answers a packet it cannot read with one of these and then tears the connection down
		/// without a disconnect packet, so the proxy only ever sees the timeout that follows. Holding the
		/// violation lets the disconnect be attributed to the real cause instead of looking like the
		/// backend went down - which is the difference between kicking the player with an explanation and
		/// silently failing them over into the same bug.</para>
		/// </summary>
		private ProtocolFault? pendingProtocolFault;

		/// <summary>
		/// Set when a backend kick was relayed to the client instead of being turned into a failover.
		///
		/// <para>The socket closes a moment later and <c>onDisconnect</c> would otherwise read that as the
		/// backend dying and start the failover the kick was just spared - which is what put a banned
		/// player on the fallback and then straight back onto the backend that banned them.</para>
		///
		/// <para>Note: the shared OnDisconnected() in this build consults kickIntercepted only; this flag
		/// is kept so the relayed-kick state stays visible and the guard survives future edits.</para>
		/// </summary>
		private bool kickPassedThrough;

		/// <summary>
		/// Whether a backend-side runtime entity id is the player's own. Detail lines are printed before
		/// RewriteClientboundRuntimeIds(), so the id here is still the backend's; both sides are
		/// checked because they are usually equal but need not be.
		/// </summary>
		private string IsClientOwnRuntimeEntityId(long backendRuntimeEntityId)
		{
			if (backendRuntimeEntityId <= 0)
			{
				return "false";
			}
			bool own = backendRuntimeEntityId == connection.BackendPlayerRuntimeEntityId()
				|| backendRuntimeEntityId == connection.ClientPlayerRuntimeEntityId();
			return own ? "SELF" : "false";
		}

		private void LogClientboundDetails(IPacket packet)
		{
			if (packet is NetworkChunkPublisherUpdatePacket publisherUpdate)
			{
				Logger.Info(
					$"  NetworkChunkPublisherUpdate position=({publisherUpdate.NewPositionForView?.X ?? 0}, {publisherUpdate.NewPositionForView?.Y ?? 0}, {publisherUpdate.NewPositionForView?.Z ?? 0}) radius={publisherUpdate.NewRadiusForView} savedChunks={publisherUpdate.ServerBuiltChunksList?.Count ?? 0} playerDimension={connection.PlayerDimensionId()} requestedRadius={connection.LastRequestedChunkRadius()}/{connection.LastRequestedMaxChunkRadius()}."
				);
			}
			else if (packet is ChunkRadiusUpdatedPacket radiusUpdated)
			{
				Logger.Info(
					$"  ChunkRadiusUpdated radius={radiusUpdated.ChunkRadius} rememberedRadius={connection.LastRequestedChunkRadius()}/{connection.LastRequestedMaxChunkRadius()}."
				);
			}
			else if (packet is LevelChunkPacket chunk)
			{
				byte[] data = chunk.SerializedChunkData;
				Logger.Info(
					$"  LevelChunk x={chunk.ChunkPosition?.X ?? 0} z={chunk.ChunkPosition?.Z ?? 0} dimension={chunk.DimensionId?.Value ?? 0} subChunks={chunk.SubChunksCount} requestSubChunks={(chunk.ClientRequestSubChunkLimit != null && chunk.ClientRequestSubChunkLimit.HasValue ? "true" : "false")} subChunkLimit={(chunk.ClientRequestSubChunkLimit != null && chunk.ClientRequestSubChunkLimit.HasValue ? chunk.ClientRequestSubChunkLimit.Value : 0)} cache={(chunk.CacheEnabled ? "true" : "false")} blobs={chunk.CacheMetadata?.Count ?? 0} dataBytes={data?.Length ?? 0} firstBytes={Preview(data, 32)}."
				);
			}
			else if (packet is SubChunkPacket subChunk)
			{
				var details = new List<string>();
				for (int i = 0; i < subChunk.SubChunkData.Count && i < 12; i++)
				{
					global::Protocol.Types.SubChunkPacketPayload.SubChunkPacketData entry = subChunk.SubChunkData[i];
					byte[] serialized = entry.SerializedSubChunk != null && entry.SerializedSubChunk.HasValue
						? entry.SerializedSubChunk.Value ?? Array.Empty<byte>()
						: Array.Empty<byte>();
					// renderHeightMapType is the only field of this packet that six captures never
					// printed, and therefore the only value on it that has never been checked against
					// 1.26.40's accepted range (the r26_u4 dump allows 0-4 here but only 0-3 for the
					// terrain heightmap above). first= is the sub-chunk format version byte and storage
					// count, which is what says the payload is the standard unchanged encoding.
					details.Add(
						$"({entry.SubChunkPosOffset?.SubchunkOffsetX ?? 0}, {entry.SubChunkPosOffset?.SubchunkOffsetY ?? 0}, {entry.SubChunkPosOffset?.SubchunkOffsetZ ?? 0})"
						+ ":" + entry.SubChunkRequestResult
						+ ":bytes=" + (serialized?.Length ?? 0)
						+ ":height=" + entry.HeightMapData?.HeightMapType
						+ ":render=" + entry.HeightMapData?.RenderHeightMapType
						+ ":first=" + Preview(serialized, 4)
					);
				}
				Logger.Info(
					$"  SubChunk dimension={subChunk.DimensionType?.Value ?? 0} center=({subChunk.CenterPos?.SubchunkPositionX ?? 0}, {subChunk.CenterPos?.SubchunkPositionY ?? 0}, {subChunk.CenterPos?.SubchunkPositionZ ?? 0}) cache={(subChunk.CacheEnabled ? "true" : "false")} entries={subChunk.SubChunkData.Count} details={string.Join(",", details)}."
				);
			}
			else if (packet is ClientCacheMissResponsePacket missResponse)
			{
				var firstBlobIds = new List<ulong>();
				for (int i = 0; i < missResponse.MissingBlobs.Count && i < 8; i++)
				{
					firstBlobIds.Add(missResponse.MissingBlobs[i].BlobId);
				}
				Logger.Info(
					$"  ClientCacheMissResponse blobs={missResponse.MissingBlobs.Count} firstBlobIds=[{string.Join(", ", firstBlobIds)}]."
				);
			}
			else if (packet is RespawnPacket respawn)
			{
				Logger.Info(
					$"  Respawn state={respawn.State} runtimeEntityId={(long)(respawn.PlayerRuntimeId?.Value ?? 0UL)} position={FormatRespawnVec3(respawn.Position)}."
				);
			}
			else if (packet is ItemStackResponsePacket stackResponse)
			{
				// The other half of the ItemStackRequest trace in ClientRelayPacketHandler. A rejection
				// names only the request id and a reason, so it is the request logged alongside that
				// says what was refused; a success is worth having too, because the stack network ids it
				// hands back are what the client has to quote in its next request.
				foreach (global::Protocol.Types.ItemStackResponseInfo response in stackResponse.Responses)
				{
					var line = new System.Text.StringBuilder("  ItemStackResponse id=")
						.Append(response.ClientRequestId?.ID ?? 0).Append(" result=").Append(response.Result);
					if (response.Containers != null && response.Containers.HasValue)
					{
						foreach (global::Protocol.Types.ItemStackResponseContainerInfo container in response.Containers.Value)
						{
							foreach (global::Protocol.Types.ItemStackResponseSlotInfo slot in container.Slots)
							{
								line.Append("\n    ").Append(container.FullContainerName?.ContainerName)
									.Append('[').Append(slot.Slot).Append("] count=").Append(slot.Amount)
									.Append(" netId=").Append(slot.ItemStackNetId != null && slot.ItemStackNetId.HasValue ? slot.ItemStackNetId.Value.ID : 0);
							}
						}
					}
					Logger.Info(line + ".");
				}
			}
			else if (packet is SetPlayerInventoryOptionsPacket inventoryOptions)
			{
				Logger.Info(
					$"  SetPlayerInventoryOptions left={inventoryOptions.InventoryOptions?.LeftInventoryTab} right={inventoryOptions.InventoryOptions?.RightInventoryTab} filtering={(inventoryOptions.InventoryOptions?.Filtering ?? false ? "true" : "false")} layout={inventoryOptions.InventoryOptions?.LayoutInv} craftingLayout={inventoryOptions.InventoryOptions?.LayoutCraft}."
				);
			}
			else if (packet is SetActorDataPacket entityData)
			{
				Logger.Info(
					$"  SetActorData runtimeEntityId={(long)(entityData.TargetRuntimeID?.Value ?? 0UL)} metadata={entityData.ActorData?.Data?.Count ?? 0} properties(int={entityData.SynchedProperties?.IntEntriesList?.Count ?? 0} float={entityData.SynchedProperties?.FloatEntriesList?.Count ?? 0}) tick={entityData.Tick?.InputTick ?? 0UL}."
				);
			}
			else if (packet is MoveActorDeltaPacket moveEntity)
			{
				// The two highest-volume packets on this hop had no detail line at all, which is why six
				// captures never showed which entity was moving. -Dproxy.neuterClientbound stripped their
				// content without changing the outcome, so the remaining suspects are the fields a neuter
				// must preserve to stay a neuter: the runtime entity id, and whether it is the player's
				// own (a server that keeps moving the local entity is fighting the client's prediction).
				global::Protocol.Types.MoveActorDeltaData delta = moveEntity.MoveData;
				string Flags()
				{
					var names = new List<string>();
					if (delta.NewPositionX != null && delta.NewPositionX.HasValue) { names.Add("HAS_X"); }
					if (delta.NewPositionY != null && delta.NewPositionY.HasValue) { names.Add("HAS_Y"); }
					if (delta.NewPositionZ != null && delta.NewPositionZ.HasValue) { names.Add("HAS_Z"); }
					if (delta.RotationX != null && delta.RotationX.HasValue) { names.Add("HAS_PITCH"); }
					if (delta.RotationY != null && delta.RotationY.HasValue) { names.Add("HAS_YAW"); }
					if (delta.RotationYHead != null && delta.RotationYHead.HasValue) { names.Add("HAS_HEAD_YAW"); }
					return string.Join("|", names);
				}
				float X() => delta.NewPositionX != null && delta.NewPositionX.HasValue ? delta.NewPositionX.Value : 0f;
				float Y() => delta.NewPositionY != null && delta.NewPositionY.HasValue ? delta.NewPositionY.Value : 0f;
				float Z() => delta.NewPositionZ != null && delta.NewPositionZ.HasValue ? delta.NewPositionZ.Value : 0f;
				float Pitch() => delta.RotationX != null && delta.RotationX.HasValue ? delta.RotationX.Value : 0f;
				float Yaw() => delta.RotationY != null && delta.RotationY.HasValue ? delta.RotationY.Value : 0f;
				float HeadYaw() => delta.RotationYHead != null && delta.RotationYHead.HasValue ? delta.RotationYHead.Value : 0f;
				long runtimeEntityId = (long)(delta?.ActorRuntimeID?.Value ?? 0UL);
				Logger.Info(
					$"  MoveActorDelta runtimeEntityId={runtimeEntityId} self={IsClientOwnRuntimeEntityId(runtimeEntityId)} flags={Flags()} position=({Invariant(X())},{Invariant(Y())},{Invariant(Z())}) rotation=({Invariant(Pitch())},{Invariant(Yaw())},{Invariant(HeadYaw())}) onGround={(delta?.IsOnGround ?? false ? "true" : "false")} forceMove={(delta?.ForceMove ?? false ? "true" : "false")} forceMoveLocalEntity={(delta?.ForceMoveLocalEntity ?? false ? "true" : "false")} forceCompletion={(delta?.ForceCompletion ?? false ? "true" : "false")}."
				);
			}
			else if (packet is SetActorMotionPacket entityMotion)
			{
				Logger.Info(
					$"  SetActorMotion runtimeEntityId={(long)(entityMotion.TargetRuntimeID?.Value ?? 0UL)} self={IsClientOwnRuntimeEntityId((long)(entityMotion.TargetRuntimeID?.Value ?? 0UL))} motion={FormatRespawnVec3(entityMotion.Motion)} tick={entityMotion.Tick?.InputTick ?? 0UL}."
				);
			}
			else if (packet is UpdateAttributesPacket attributes)
			{
				Logger.Info(
					$"  UpdateAttributes runtimeEntityId={(long)(attributes.TargetRuntimeID?.Value ?? 0UL)} attributes={attributes.AttributeList?.Count ?? 0} tick={attributes.Tick?.InputTick ?? 0UL}."
				);
			}
		}

		/// <summary>Renders a float exactly like the java %s/%f traces regardless of host locale.</summary>
		private static string Invariant(float value)
		{
			return value.ToString("R", CultureInfo.InvariantCulture);
		}

		private static string Preview(byte[] data, int maxBytes)
		{
			if (data == null || data.Length <= 0)
			{
				return "";
			}
			int length = Math.Min(maxBytes, data.Length);
			var hex = new System.Text.StringBuilder(length * 2);
			for (int i = 0; i < length; i++)
			{
				hex.Append(data[i].ToString("x2"));
			}
			return hex.ToString();
		}

		private IPacket RewriteClientboundRuntimeIds(IPacket packet)
		{
			if (packet is StartGamePacket startGame)
			{
				startGame.InvalidateWireCache();
				return packet;
			}
			if (packet is RespawnPacket respawn)
			{
				if (respawn.PlayerRuntimeId != null)
				{
					respawn.PlayerRuntimeId.Value = unchecked((ulong)connection.ToClientRuntimeEntityId(unchecked((long)respawn.PlayerRuntimeId.Value), false));
				}
				respawn.InvalidateWireCache();
			}
			else if (packet is MovePlayerPacket movePlayer)
			{
				if (movePlayer.PlayerRuntimeID != null)
				{
					movePlayer.PlayerRuntimeID.Value = unchecked((ulong)connection.ToClientRuntimeEntityId(unchecked((long)movePlayer.PlayerRuntimeID.Value), false));
				}
				if (movePlayer.RidingRuntimeID != null)
				{
					movePlayer.RidingRuntimeID.Value = unchecked((ulong)connection.ToClientRuntimeEntityId(unchecked((long)movePlayer.RidingRuntimeID.Value), false));
				}
				movePlayer.InvalidateWireCache();
			}
			else if (packet is MoveActorAbsolutePacket moveEntityAbsolute)
			{
				if (moveEntityAbsolute.MoveData?.ActorRuntimeID != null)
				{
					moveEntityAbsolute.MoveData.ActorRuntimeID.Value = unchecked((ulong)connection.ToClientRuntimeEntityId(unchecked((long)moveEntityAbsolute.MoveData.ActorRuntimeID.Value), false));
				}
				moveEntityAbsolute.InvalidateWireCache();
			}
			else if (packet is MoveActorDeltaPacket moveEntityDelta)
			{
				if (moveEntityDelta.MoveData?.ActorRuntimeID != null)
				{
					moveEntityDelta.MoveData.ActorRuntimeID.Value = unchecked((ulong)connection.ToClientRuntimeEntityId(unchecked((long)moveEntityDelta.MoveData.ActorRuntimeID.Value), false));
				}
				moveEntityDelta.InvalidateWireCache();
			}
			else if (packet is SetActorDataPacket actorData)
			{
				if (actorData.TargetRuntimeID != null)
				{
					actorData.TargetRuntimeID.Value = unchecked((ulong)connection.ToClientRuntimeEntityId(unchecked((long)actorData.TargetRuntimeID.Value), false));
				}
				RewriteUniqueIdMetadata(actorData.ActorData);
				actorData.InvalidateWireCache();
			}
			else if (packet is SetActorMotionPacket actorMotion)
			{
				if (actorMotion.TargetRuntimeID != null)
				{
					actorMotion.TargetRuntimeID.Value = unchecked((ulong)connection.ToClientRuntimeEntityId(unchecked((long)actorMotion.TargetRuntimeID.Value), false));
				}
				actorMotion.InvalidateWireCache();
			}
			else if (packet is UpdateBlockSyncedPacket blockSynced)
			{
				blockSynced.UniqueActorId = unchecked((ulong)connection.ToClientRuntimeEntityId(unchecked((long)blockSynced.UniqueActorId), false));
				blockSynced.InvalidateWireCache();
			}
			else if (packet is UpdateAttributesPacket attributes)
			{
				if (attributes.TargetRuntimeID != null)
				{
					attributes.TargetRuntimeID.Value = unchecked((ulong)connection.ToClientRuntimeEntityId(unchecked((long)attributes.TargetRuntimeID.Value), false));
				}
				attributes.InvalidateWireCache();
			}
			else if (packet is ActorEventPacket actorEvent)
			{
				if (actorEvent.TargetRuntimeID != null)
				{
					actorEvent.TargetRuntimeID.Value = unchecked((ulong)connection.ToClientRuntimeEntityId(unchecked((long)actorEvent.TargetRuntimeID.Value), false));
				}
				actorEvent.InvalidateWireCache();
			}
			// No EntityFallPacket counterpart: this codec registers no fall packet at all (id 37 exists
			// only as an enum name in MinecraftPacketIds), so Java's entityFall clause has no target here.
			else if (packet is AnimatePacket animate)
			{
				if (animate.TargetActorRuntimeID != null)
				{
					animate.TargetActorRuntimeID.Value = unchecked((ulong)connection.ToClientRuntimeEntityId(unchecked((long)animate.TargetActorRuntimeID.Value), false));
				}
				animate.InvalidateWireCache();
			}
			else if (packet is MovementEffectPacket movementEffect)
			{
				if (movementEffect.TargetRuntimeID != null)
				{
					movementEffect.TargetRuntimeID.Value = unchecked((ulong)connection.ToClientRuntimeEntityId(unchecked((long)movementEffect.TargetRuntimeID.Value), false));
				}
				movementEffect.InvalidateWireCache();
			}
			else if (packet is MobEffectPacket mobEffect)
			{
				// Potion effects: without rewriting the target the client applies the effect to an
				// entity id that is not its own after a backend switch, and the HUD never shows it.
				if (mobEffect.TargetRuntimeID != null)
				{
					mobEffect.TargetRuntimeID.Value = unchecked((ulong)connection.ToClientRuntimeEntityId(unchecked((long)mobEffect.TargetRuntimeID.Value), false));
				}
				connection.TrackClientEffect(mobEffect.EffectID,
					mobEffect.EventID == global::Protocol.MobEffectPacketPayload.Event.Remove);
				mobEffect.InvalidateWireCache();
			}
			else if (packet is ShowCreditsPacket sc)
			{
				if (sc.PlayerRuntimeID != null)
				{
					sc.PlayerRuntimeID.Value = unchecked((ulong)connection.ToClientRuntimeEntityId(unchecked((long)sc.PlayerRuntimeID.Value), false));
				}
				sc.InvalidateWireCache();
			}
			else if (packet is MotionPredictionHintsPacket movementPrediction)
			{
				// This codec's name for Java's MovementPredictionSyncPacket.
				if (movementPrediction.MRuntimeId != null)
				{
					movementPrediction.MRuntimeId.Value = unchecked((ulong)connection.ToClientRuntimeEntityId(unchecked((long)movementPrediction.MRuntimeId.Value), false));
				}
				movementPrediction.InvalidateWireCache();
			}
			else if (packet is TakeItemActorPacket takeItem)
			{
				// ActorRuntimeID carries the taker, ItemRuntimeID the item entity (Java's
				// runtimeEntityId / itemRuntimeEntityId pair).
				if (takeItem.ActorRuntimeID != null)
				{
					takeItem.ActorRuntimeID.Value = unchecked((ulong)connection.ToClientRuntimeEntityId(unchecked((long)takeItem.ActorRuntimeID.Value), false));
				}
				if (takeItem.ItemRuntimeID != null)
				{
					takeItem.ItemRuntimeID.Value = unchecked((ulong)connection.ToClientRuntimeEntityId(unchecked((long)takeItem.ItemRuntimeID.Value), false));
				}
				takeItem.InvalidateWireCache();
			}
			else if (packet is SetActorLinkPacket linkPacket && linkPacket.Link != null)
			{
				linkPacket.Link = RewriteLink(linkPacket.Link);
				linkPacket.InvalidateWireCache();
			}
			else if (packet is AddActorPacket addEntity)
			{
				if (addEntity.TargetRuntimeID != null)
				{
					addEntity.TargetRuntimeID.Value = unchecked((ulong)connection.ToClientRuntimeEntityId(unchecked((long)addEntity.TargetRuntimeID.Value), true));
				}
				addEntity.ActorLinks = RewriteLinks(addEntity.ActorLinks);
				RewriteUniqueIdMetadata(addEntity.ActorData);
				addEntity.InvalidateWireCache();
			}
			else if (packet is AddItemActorPacket addItem)
			{
				if (addItem.TargetRuntimeID != null)
				{
					addItem.TargetRuntimeID.Value = unchecked((ulong)connection.ToClientRuntimeEntityId(unchecked((long)addItem.TargetRuntimeID.Value), true));
				}
				RewriteUniqueIdMetadata(addItem.EntityData);
				addItem.InvalidateWireCache();
			}
			else if (packet is AddPlayerPacket addPlayer)
			{
				if (addPlayer.TargetRuntimeID != null)
				{
					addPlayer.TargetRuntimeID.Value = unchecked((ulong)connection.ToClientRuntimeEntityId(unchecked((long)addPlayer.TargetRuntimeID.Value), true));
				}
				// Protocol 2168's AddPlayerPacket carries no numeric unique-entity-id field (players are
				// identified by their mce UUID), so Java's addPlayer.setUniqueEntityId(toClientUnique(...))
				// clause has no counterpart here. The links still need rewriting.
				addPlayer.ActorLinks = RewriteLinks(addPlayer.ActorLinks);
				RewriteUniqueIdMetadata(addPlayer.EntityData);
				addPlayer.InvalidateWireCache();
			}
			else if (packet is AddPaintingPacket addHanging)
			{
				if (addHanging.TargetRuntimeID != null)
				{
					addHanging.TargetRuntimeID.Value = unchecked((ulong)connection.ToClientRuntimeEntityId(unchecked((long)addHanging.TargetRuntimeID.Value), true));
				}
				addHanging.InvalidateWireCache();
			}
			else if (packet is MobEquipmentPacket mobEquipmentClientbound)
			{
				// Clientbound held-item updates name OTHER entities (the local player's own arrives
				// serverbound and is normalized in ClientRelayPacketHandler). WaterdogPE rewrites both
				// directions; the clientbound one was missing, so other players' held items broke
				// after a backend switch.
				if (mobEquipmentClientbound.TargetRuntimeID != null)
				{
					mobEquipmentClientbound.TargetRuntimeID.Value = unchecked((ulong)connection.ToClientRuntimeEntityId(unchecked((long)mobEquipmentClientbound.TargetRuntimeID.Value), false));
				}
				mobEquipmentClientbound.InvalidateWireCache();
			}
			else if (packet is MobArmorEquipmentPacket mobArmorEquipment)
			{
				if (mobArmorEquipment.TargetRuntimeID != null)
				{
					mobArmorEquipment.TargetRuntimeID.Value = unchecked((ulong)connection.ToClientRuntimeEntityId(unchecked((long)mobArmorEquipment.TargetRuntimeID.Value), false));
				}
				mobArmorEquipment.InvalidateWireCache();
			}
			else if (packet is AnimateEntityPacket animateEntity)
			{
				foreach (global::Protocol.Types.ActorRuntimeID animateTarget in animateEntity.MRuntimeIds ?? new List<global::Protocol.Types.ActorRuntimeID>())
				{
					if (animateTarget != null)
					{
						animateTarget.Value = unchecked((ulong)connection.ToClientRuntimeEntityId(unchecked((long)animateTarget.Value), false));
					}
				}
				animateEntity.InvalidateWireCache();
			}
			else if (packet is EmotePacket emote)
			{
				if (emote.ActorRuntimeId != null)
				{
					emote.ActorRuntimeId.Value = unchecked((ulong)connection.ToClientRuntimeEntityId(unchecked((long)emote.ActorRuntimeId.Value), false));
				}
				emote.InvalidateWireCache();
			}
			else if (packet is EmoteListPacket emoteList)
			{
				if (emoteList.RuntimeId != null)
				{
					emoteList.RuntimeId.Value = unchecked((ulong)connection.ToClientRuntimeEntityId(unchecked((long)emoteList.RuntimeId.Value), false));
				}
				emoteList.InvalidateWireCache();
			}
			else if (packet is BossEventPacket bossEvent)
			{
				// Boss bars bind to unique ids: the bar's entity plus the viewing player. Both get the
				// plain local-player swap; every non-player boss id passes through untouched.
				if (bossEvent.TargetActorID != null)
				{
					bossEvent.TargetActorID.Value = connection.SwapClientUniqueEntityId(bossEvent.TargetActorID.Value);
				}
				if (bossEvent.PlayerID != null)
				{
					bossEvent.PlayerID.Value = connection.SwapClientUniqueEntityId(bossEvent.PlayerID.Value);
				}
				bossEvent.InvalidateWireCache();
			}
			else if (packet is UpdateTradePacket updateTrade)
			{
				// EntityUniqueId names the trader; LastTradingPlayer names the local player, which is
				// the only value that actually changes across a switch.
				if (updateTrade.EntityUniqueId != null)
				{
					updateTrade.EntityUniqueId.Value = connection.SwapClientUniqueEntityId(updateTrade.EntityUniqueId.Value);
				}
				if (updateTrade.LastTradingPlayer != null)
				{
					updateTrade.LastTradingPlayer.Value = connection.SwapClientUniqueEntityId(updateTrade.LastTradingPlayer.Value);
				}
				updateTrade.InvalidateWireCache();
			}
			else if (packet is PlayerLocationPacket playerLocation)
			{
				// Locator-bar waypoints always reference PLAYERS, so without this every teammate
				// waypoint binding breaks on the far side of a switch.
				if (playerLocation.TargetActorID != null)
				{
					playerLocation.TargetActorID.Value = connection.SwapClientUniqueEntityId(playerLocation.TargetActorID.Value);
				}
				playerLocation.InvalidateWireCache();
			}
			else if (packet is LevelSoundEventPacket levelSound)
			{
				levelSound.ActorUniqueId = connection.SwapClientUniqueEntityId(levelSound.ActorUniqueId);
				levelSound.InvalidateWireCache();
			}
			else if (packet is SpawnParticleEffectPacket spawnParticle)
			{
				if (spawnParticle.ActorId != null)
				{
					spawnParticle.ActorId.Value = connection.SwapClientUniqueEntityId(spawnParticle.ActorId.Value);
				}
				spawnParticle.InvalidateWireCache();
			}
			else if (packet is NpcDialoguePacket npcDialogue)
			{
				npcDialogue.NpcIdRawId = unchecked((ulong)connection.SwapClientUniqueEntityId(unchecked((long)npcDialogue.NpcIdRawId)));
				npcDialogue.InvalidateWireCache();
			}
			else if (packet is UpdateEquipPacket updateEquip)
			{
				if (updateEquip.EntityUniqueId != null)
				{
					updateEquip.EntityUniqueId.Value = connection.SwapClientUniqueEntityId(updateEquip.EntityUniqueId.Value);
				}
				updateEquip.InvalidateWireCache();
			}
			else if (packet is CameraInstructionPacket cameraInstruction)
			{
				if (cameraInstruction.CameraInstruction?.AttachToEntity != null
					&& cameraInstruction.CameraInstruction.AttachToEntity.HasValue)
				{
					global::Protocol.Types.CameraInstructionOptions.AttachToEntityInstruction attach =
						cameraInstruction.CameraInstruction.AttachToEntity.Value;
					if (attach != null)
					{
						attach.EntityActorID = connection.SwapClientUniqueEntityId(attach.EntityActorID);
					}
				}
				cameraInstruction.InvalidateWireCache();
			}
			else if (packet is UpdatePlayerGameTypePacket updateGameType)
			{
				if (updateGameType.TargetPlayer != null)
				{
					updateGameType.TargetPlayer.Value = TraceUniqueRewrite("UpdatePlayerGameType", updateGameType.TargetPlayer.Value);
					updateGameType.InvalidateWireCache();
				}
			}
			else if (packet is UpdateAbilitiesPacket abilities)
			{
				if (abilities.Data != null)
				{
					abilities.Data.TargetPlayerRawId = TraceUniqueRewrite("UpdateAbilities", abilities.Data.TargetPlayerRawId);
					abilities.InvalidateWireCache();
				}
			}
			else if (packet is PlayerListPacket playerList)
			{
				// Entry.entityId is the player's *unique* id. The local player's own entry has to be
				// remapped like any other local-player id packet, or the client binds its skin and
				// nametag to an id it does not recognise after a backend switch.
				foreach (OneOf.OneOf<global::Protocol.Types.PlayerListPacketPayload.RemoveEntry, global::Protocol.Types.PlayerListPacketPayload.AddEntry> entry in playerList.Entries)
				{
					if (entry.Index == 1 && entry.AsT1.ActorUniqueID != null)
					{
						entry.AsT1.ActorUniqueID.Value = connection.ToClientUniqueEntityId(entry.AsT1.ActorUniqueID.Value);
					}
				}
				playerList.InvalidateWireCache();
			}
			else if (packet is NetworkChunkPublisherUpdatePacket publisherUpdate)
			{
				publisherUpdate.InvalidateWireCache();
			}
			else if (packet is ChunkRadiusUpdatedPacket radiusUpdated)
			{
				radiusUpdated.InvalidateWireCache();
			}
			return packet;
		}

		/// <summary>Formats a spawn/respawn position like the java Vector3f traces ("(x, y, z)").</summary>
		private static string FormatRespawnVec3(global::Protocol.Types.Vec3? value)
		{
			return value == null
				? "null"
				: "(" + value.X.ToString(CultureInfo.InvariantCulture) + ", "
					+ value.Y.ToString(CultureInfo.InvariantCulture) + ", "
					+ value.Z.ToString(CultureInfo.InvariantCulture) + ")";
		}

		/// <summary>
		/// Local-player unique-id rewrite with a loud "did it match?" trace.
		///
		/// <para>The whole local-player id mapping keys off <c>StartGame.uniqueEntityId</c>. If a backend
		/// ever reports a different id there than the one it uses in these packets, the mapping silently
		/// never fires and the client quietly ignores its own gamemode/ability updates - which looks like
		/// a half-applied gamemode rather than an error. Logging the miss makes that failure visible.</para>
		/// </summary>
		private long TraceUniqueRewrite(string label, long backendUniqueEntityId)
		{
			long clientUniqueEntityId = connection.ToClientUniqueEntityId(backendUniqueEntityId);
			if (connection.IsPacketTraceActive())
			{
				long expected = connection.BackendPlayerUniqueEntityId();
				Logger.Info(
					$"{label} unique-id rewrite from backend {backendName}: backendId={backendUniqueEntityId} -> clientId={clientUniqueEntityId} "
						+ $"(localPlayerBackendId={expected} localPlayerClientId={connection.ClientPlayerUniqueEntityId()} matchedLocalPlayer={(backendUniqueEntityId == expected && expected != 0 ? "true" : "false")})."
				);
			}
			return clientUniqueEntityId;
		}

		private List<global::Protocol.Types.ActorLink> RewriteLinks(List<global::Protocol.Types.ActorLink> links)
		{
			if (links == null || links.Count == 0)
			{
				return links;
			}
			for (int i = 0; i < links.Count; i++)
			{
				links[i] = RewriteLink(links[i]);
			}
			return links;
		}

		private global::Protocol.Types.ActorLink RewriteLink(global::Protocol.Types.ActorLink link)
		{
			// WaterdogPE runs every link endpoint (SetActorLinkPacket plus the lists on AddActor/
			// AddPlayer) through its plain local-player swap and nothing else. These fields carry
			// *unique* ids: pushing them through the runtime-id table - worse with register=true, as
			// TargetB used to get - filed positive unique ids into the runtime maps, where they would
			// later mis-rewrite whichever entity really owned that runtime id.
			return new global::Protocol.Types.ActorLink
			{
				TargetA = new global::Protocol.Types.ActorUniqueID { Value = connection.SwapClientUniqueEntityId(link?.TargetA?.Value ?? 0L) },
				TargetB = new global::Protocol.Types.ActorUniqueID { Value = connection.SwapClientUniqueEntityId(link?.TargetB?.Value ?? 0L) },
				Type_ = link?.Type_ ?? default,
				Immediate = link?.Immediate ?? false,
				PassengerInitiated = link?.PassengerInitiated ?? false,
				VehicleAngularVelocity = link?.VehicleAngularVelocity ?? 0f
			};
		}

		/// <summary>
		/// Wire ids of the entity-metadata fields that carry a unique actor id, per WaterdogPE's
		/// EntityMap.ENTITY_DATA_FIELDS. Registered once by Bedrock_v291 and never re-numbered by a
		/// later codec, so these are the v2168 values.
		/// </summary>
		private static readonly int[] UniqueIdMetadataFields =
		{
			5,   // OWNER_EID (pet ownership)
			6,   // TARGET_EID
			37,  // LEASH_HOLDER
			49,  // WITHER_TARGET_A
			50,  // WITHER_TARGET_B
			51,  // WITHER_TARGET_C
			67,  // TRADE_TARGET_EID
			84,  // BALLOON_ANCHOR_EID
			87   // AGENT_EID
		};

		/// <summary>
		/// WaterdogPE's rewriteMetadata: swaps the local player's unique id inside long-typed metadata
		/// entries (leash holders, pet owners, wither targets, trade partner...), leaving every other
		/// value untouched. Without it a leash or pet ownership that names the player keeps pointing at
		/// an id the client no longer recognises after a backend switch.
		/// </summary>
		private void RewriteUniqueIdMetadata(global::Protocol.Types.SynchedActorData.CopyableDataList metadata)
		{
			if (metadata?.Data == null)
			{
				return;
			}
			foreach (global::Protocol.Types.DataItemEntry entry in metadata.Data)
			{
				if (entry == null
					|| Array.IndexOf(UniqueIdMetadataFields, unchecked((int)entry.ID)) < 0
					|| entry.Payload.Index != 7)
				{
					continue;
				}
				global::Protocol.Types.DataItemInt64Payload payload = entry.Payload.AsT7;
				payload.Value = connection.SwapClientUniqueEntityId(payload.Value);
			}
		}

		/// <summary>
		/// Turns a kick from the backend the player is on into a failover, the way Velocity turns one
		/// into a redirect.
		///
		/// <para>A graceful backend shutdown sends this well before the socket closes, so without the
		/// interception the client is gone long before OnDisconnected - and the RakNet timeout that would
		/// eventually fire is ten seconds too late to matter. Forwarding resumes unchanged when failover
		/// declines the kick, so a player with no fallback still sees the backend's own message.</para>
		/// </summary>
		private bool InterceptBackendKick(IPacket packet)
		{
			if (kickIntercepted)
			{
				return true;
			}
			if (failover == null)
			{
				return false;
			}
			string reason;
			// Whether the backend wrote something for this player, which is what separates a ban from a
			// host going away. Messages.Index==1 is the wire flag itself ("message skipped"), so
			// DisconnectHasMessage() is the backend's own statement rather than a guess at its text.
			// This codec's framing layer drops undecodable packet ids before they reach a handler, so
			// the Java UnknownPacket branch (raw id == DISCONNECT_PACKET_ID, counted as "no message"
			// because a disconnect that did not decode cannot be read) has no counterpart here.
			bool backendSuppliedMessage;
			if (packet is DisconnectPacket disconnect)
			{
				reason = KickReason(disconnect);
				backendSuppliedMessage = Messages.DisconnectHasMessage(disconnect);
			}
			else
			{
				return false;
			}
			if (!failover.FailsOverOnBackendKick(backendSuppliedMessage))
			{
				// The backend decided something about this player - banned, whitelisted out, kicked by a
				// moderator. Rescuing them to a fallback overrides that decision, and because the
				// fallback transfers them straight back it also loops: kick, failover, transfer, kick.
				// Forwarding the packet unchanged lets the player read the backend's own message, which
				// is the one worth showing; the flag stops OnDisconnected starting a failover behind it.
				kickPassedThrough = true;
				Logger.Info(
					$"Backend {backendName} kicked {connection.Client().RemoteEndPoint} ({reason}); passing the kick through to the client."
				);
				return false;
			}
			Logger.Info(
				// A decoded disconnect is the only way a kick reaches this method in this build, so the
				// Java "its disconnect did not decode" alternative never applies.
				$"Backend {backendName} kicked {connection.Client().RemoteEndPoint} ({reason}); intercepted."
			);
			// The fault matters here too: a backend that answers a violation with a real disconnect
			// packet rather than by timing out arrives down this path instead of OnDisconnected.
			if (!failover.Begin(connection, backendName, reason, pendingProtocolFault))
			{
				return false;
			}
			kickIntercepted = true;
			// The socket closes moments after this packet; OnDisconnected must not undo the failover.
			backend.SetDisconnectClientOnClose(false);
			// Close it now rather than waiting: until it does, IsConnected is still true and the
			// client's input keeps being forwarded into a world it is already being moved out of.
			if (backend.IsConnected)
			{
				backend.Disconnect("Failing the player over after a kick");
			}
			return true;
		}

		/// <summary>
		/// Violations arrive as typed PacketViolationWarningPackets and are logged where they are
		/// handled; see HandleClientbound. The Java hand-decoder path that attributed a fatal
		/// violation to the disconnect cause has no counterpart here - this codec's framing layer
		/// drops undecodable packet ids before they reach a handler.
		/// </summary>
		private static string KickReason(DisconnectPacket disconnect)
		{
			string kickMessage = Messages.DisconnectMessage(disconnect);
			if (!string.IsNullOrWhiteSpace(kickMessage))
			{
				return kickMessage;
			}
			return disconnect.Reason.ToString();
		}
	}
}
