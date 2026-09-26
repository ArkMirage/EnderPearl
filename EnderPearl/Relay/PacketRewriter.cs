using System;
using System.Collections.Generic;
using EnderPearl.Core;
using EnderPearl.Player;
using global::Protocol.Packets;
using global::Protocol.Types;
using InteractAction = global::Protocol.InteractPacketPayload.Action;

namespace EnderPearl.Relay
{
	/// <summary>
	/// Every rewrite the proxy applies to packet contents, in one place. One instance per
	/// <see cref="ProxyConnection"/>, shared by the two relay handlers; the rewriter itself holds no
	/// per-direction state, so both directions (and concurrent read threads) may call it freely.
	///
	/// <para>Serverbound (<see cref="RewriteServerbound"/>): stamps the authenticated identity onto
	/// what the client says, and re-addresses client-local runtime ids to the current backend's ids.</para>
	///
	/// <para>Clientbound (<see cref="RewriteClientbound"/>): the mirror image - rewrites every id the
	/// backend assigned into the id space the client already knows, across runtime ids, unique ids,
	/// entity links and unique-id entity metadata. Anything handled here has a counterpart in the other
	/// direction; a packet rewritten on one side only is the shape of bug this class exists to prevent.</para>
	///
	/// <para>Coverage audit against the protocol library: every member typed <c>ActorRuntimeID</c> is
	/// rewritten in the direction it travels, together with the id fields that are not wrapper objects
	/// (<c>UpdateBlockSynced.UniqueActorId</c>, <c>ItemUseOnActorInventoryTransaction.RuntimeId</c>) and
	/// <c>StartGame.RuntimeID</c>, which the switch path owns before this class runs. Two kinds of "id"
	/// are deliberately excluded: block runtime ids (<c>UpdateSubChunkNetworkBlockInfo.RuntimeId</c>) are
	/// not entity ids, and <c>ActorLink</c> plus entity metadata carry only unique ids.</para>
	/// </summary>
	public sealed class PacketRewriter
	{
		private readonly ProxyConnection connection;

		public PacketRewriter(ProxyConnection connection)
		{
			this.connection = connection ?? throw new ArgumentNullException(nameof(connection));
		}

		// -----------------------------------------------------------------------
		// Serverbound (client -> backend)
		// -----------------------------------------------------------------------

		/// <summary>Applies every serverbound normalization pass. Mutates the packet in place.</summary>
		public void RewriteServerbound(IPacket packet)
		{
			NormalizePlayerRuntimeId(packet);
			NormalizeChatIdentity(packet);
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
		/// <see cref="RewriteClientbound"/>; a packet handled on one side only is the shape of bug this
		/// list exists to prevent.</para>
		///
		/// <para>In protocol 2168 the inventory transaction's runtime id moved inside the
		/// use-on-actor transaction variant, so that is the only transaction shape remapped here.</para>
		/// </summary>
		private void NormalizePlayerRuntimeId(IPacket packet)
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
					}
					interact.InvalidateWireCache();
					break;
				case NpcRequestPacket npcRequest when npcRequest.NPCRuntimeID != null:
					// The client learned this NPC's runtime id from a clientbound AddActor that was
					// rewritten into the client's id space; the backend only knows its own.
					npcRequest.NPCRuntimeID.Value = unchecked((ulong)connection.ToBackendRuntimeEntityId(unchecked((long)npcRequest.NPCRuntimeID.Value)));
					npcRequest.InvalidateWireCache();
					break;
				case CommandBlockUpdatePacket commandBlock
						when commandBlock.Target.Index == 0
						&& commandBlock.Target.AsT0?.TargetRuntimeID != null:
					// Variant 0 is the entity-target form; variant 1 targets a block instead and carries no
					// entity id. Same client-space -> backend-space direction as NPC requests.
					commandBlock.Target.AsT0.TargetRuntimeID.Value = unchecked((ulong)connection.ToBackendRuntimeEntityId(unchecked((long)commandBlock.Target.AsT0.TargetRuntimeID.Value)));
					commandBlock.InvalidateWireCache();
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
			if (text.Body.Index != 1 || text.Body.Value is not global::Protocol.Types.TextPacketPayload.AuthorAndMessage body)
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
					$"Rewrote serverbound chat identity from {connection.Client.RemoteEndPoint}: sourceName={body.PlayerName} xuid={text.SenderSXUID} -> {displayName}/{xuid}.");
				body.PlayerName = displayName;
				text.SenderSXUID = forgedXuid ? xuid : text.SenderSXUID;
				text.InvalidateWireCache();
			}
		}

		private static ActorRuntimeID Rid(long runtimeEntityId)
		{
			return new ActorRuntimeID { Value = unchecked((ulong)runtimeEntityId) };
		}

		// -----------------------------------------------------------------------
		// Clientbound (backend -> client)
		// -----------------------------------------------------------------------

		/// <summary>
		/// Rewrites every id the backend assigned into the id space the client knows. Mutates the packet
		/// in place and returns it; both sides speak the same protocol, so this is the whole pass.
		/// </summary>
		public IPacket RewriteClientbound(IPacket packet, string backendName)
		{
			if (packet is StartGamePacket startGame)
			{
				// StartGame.RuntimeID is the one runtime id deliberately absent from the chain below:
				// SyncDefinitionState (run from Annotate, before this) records it as the backend's
				// local-player id and swaps in the client's own. Rewriting it here would undo that handoff.
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
				// serverbound and is normalized in RewriteServerbound). WaterdogPE rewrites both
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
				// Education Edition agent arm swings/shrugs, replicated to every viewing client. The id is a
				// runtime id (Agent entity), so it needs the same client-space mapping as the other actors.
				if (agentAnimation.RuntimeId != null)
				{
					agentAnimation.RuntimeId.Value = unchecked((ulong)connection.ToClientRuntimeEntityId(unchecked((long)agentAnimation.RuntimeId.Value), false));
				}
				agentAnimation.InvalidateWireCache();
			}
			else if (packet is BossEventPacket bossEvent)
			{
				// Boss bars bind to unique ids: the bar's entity plus the viewing player. Both get the
				// plain local-player swap; every non-player boss id passes through untouched.
				if (bossEvent.TargetActorID != null)
				{
					bossEvent.TargetActorID.Value = connection.SwapClientUniqueEntityId(bossEvent.TargetActorID.Value);
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
					updateGameType.TargetPlayer.Value = TraceUniqueRewrite("UpdatePlayerGameType", updateGameType.TargetPlayer.Value, backendName);
					updateGameType.InvalidateWireCache();
				}
			}
			else if (packet is UpdateAbilitiesPacket abilities)
			{
				if (abilities.Data != null)
				{
					abilities.Data.TargetPlayerRawId = TraceUniqueRewrite("UpdateAbilities", abilities.Data.TargetPlayerRawId, backendName);
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

		/// <summary>
		/// Local-player unique-id rewrite with a loud "did it match?" trace.
		///
		/// <para>The whole local-player id mapping keys off <c>StartGame.uniqueEntityId</c>. If a backend
		/// ever reports a different id there than the one it uses in these packets, the mapping silently
		/// never fires and the client quietly ignores its own gamemode/ability updates - which looks like
		/// a half-applied gamemode rather than an error. Logging the miss makes that failure visible.</para>
		/// </summary>
		private long TraceUniqueRewrite(string label, long backendUniqueEntityId, string backendName)
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
			// WaterdogPE runs every link endpoint (SetActorLinkPacket plus the lists on AddActor/
			// AddPlayer) through its plain local-player swap and nothing else. These fields carry
			// *unique* ids: pushing them through the runtime-id table - worse with register=true, as
			// TargetB used to get - filed positive unique ids into the runtime maps, where they would
			// later mis-rewrite whichever entity really owned that runtime id.
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
}
