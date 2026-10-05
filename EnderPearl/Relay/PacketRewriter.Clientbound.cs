using System;
using System.Collections.Generic;
using EnderPearl.Core;
using EnderPearl.Player;
using global::Protocol.Packets;
using global::Protocol.Types;

namespace EnderPearl.Relay;

/// <summary>The clientbound (backend -> client) half of <see cref="PacketRewriter"/>.</summary>
public sealed partial class PacketRewriter
{
	/// <summary>
	/// Rewrites every id the backend assigned into the id space the client knows. Mutates the packet in
	/// place and returns it; both sides speak the same protocol, so this is the whole pass.
	/// </summary>
	public IPacket RewriteClientbound(IPacket packet, string backendName)
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
		// No EntityFallPacket counterpart: this codec registers no fall packet at all.
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
			if (movementPrediction.MRuntimeId != null)
			{
				movementPrediction.MRuntimeId.Value = unchecked((ulong)connection.ToClientRuntimeEntityId(unchecked((long)movementPrediction.MRuntimeId.Value), false));
			}
			movementPrediction.InvalidateWireCache();
		}
		else if (packet is TakeItemActorPacket takeItem)
		{
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
			// Protocol 2168's AddPlayerPacket carries no numeric unique-entity-id field; players are
			// identified by their mce UUID, so the links still need rewriting.
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
			foreach (ActorRuntimeID animateTarget in animateEntity.MRuntimeIds ?? new List<ActorRuntimeID>())
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
		else if (packet is AgentAnimationPacket agentAnimation)
		{
			if (agentAnimation.RuntimeId != null)
			{
				agentAnimation.RuntimeId.Value = unchecked((ulong)connection.ToClientRuntimeEntityId(unchecked((long)agentAnimation.RuntimeId.Value), false));
			}
			agentAnimation.InvalidateWireCache();
		}
		else if (packet is BossEventPacket bossEvent)
		{
			if (bossEvent.TargetActorID != null)
			{
				bossEvent.TargetActorID.Value = connection.SwapClientUniqueEntityId(bossEvent.TargetActorID.Value);
			}
			bossEvent.InvalidateWireCache();
		}
		else if (packet is UpdateTradePacket updateTrade)
		{
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
				updateGameType.TargetPlayer.Value = TraceUniqueRewrite(updateGameType.TargetPlayer.Value);
				updateGameType.InvalidateWireCache();
			}
		}
		else if (packet is UpdateAbilitiesPacket abilities)
		{
			if (abilities.Data != null)
			{
				abilities.Data.TargetPlayerRawId = TraceUniqueRewrite(abilities.Data.TargetPlayerRawId);
				abilities.InvalidateWireCache();
			}
		}
		else if (packet is PlayerListPacket playerList)
		{
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

	/// <summary>
	/// Local-player unique-id rewrite with a loud "did it match?" trace. The mapping keys off
	/// <c>StartGame.uniqueEntityId</c>; if a backend reports a different id there than in these packets, the
	/// mapping silently never fires and the client ignores its own gamemode/ability updates.
	/// </summary>
	private long TraceUniqueRewrite(long backendUniqueEntityId)
	{
		return connection.ToClientUniqueEntityId(backendUniqueEntityId);
	}

	private List<ActorLink> RewriteLinks(List<ActorLink> links)
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

	private ActorLink RewriteLink(ActorLink link)
	{
		// Link endpoints carry unique ids, so they go through the plain local-player swap, never the
		// runtime-id table (filed positive unique ids there would mis-rewrite whoever owns that runtime id).
		return new ActorLink
		{
			TargetA = new ActorUniqueID { Value = connection.SwapClientUniqueEntityId(link?.TargetA?.Value ?? 0L) },
			TargetB = new ActorUniqueID { Value = connection.SwapClientUniqueEntityId(link?.TargetB?.Value ?? 0L) },
			Type_ = link?.Type_ ?? default,
			Immediate = link?.Immediate ?? false,
			PassengerInitiated = link?.PassengerInitiated ?? false,
			VehicleAngularVelocity = link?.VehicleAngularVelocity ?? 0f
		};
	}

	/// <summary>
	/// Wire ids of the entity-metadata fields that carry a unique actor id, per WaterdogPE's
	/// EntityMap.ENTITY_DATA_FIELDS (v2168 values).
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
	/// Swaps the local player's unique id inside long-typed metadata entries (leash holders, pet owners,
	/// wither targets, trade partner...), leaving every other value untouched.
	/// </summary>
	private void RewriteUniqueIdMetadata(global::Protocol.Types.SynchedActorData.CopyableDataList metadata)
	{
		if (metadata?.Data == null)
		{
			return;
		}
		foreach (DataItemEntry entry in metadata.Data)
		{
			if (entry == null
				|| Array.IndexOf(UniqueIdMetadataFields, unchecked((int)entry.ID)) < 0
				|| entry.Payload.Index != 7)
			{
				continue;
			}
			DataItemInt64Payload payload = entry.Payload.AsT7;
			payload.Value = connection.SwapClientUniqueEntityId(payload.Value);
		}
	}
}
