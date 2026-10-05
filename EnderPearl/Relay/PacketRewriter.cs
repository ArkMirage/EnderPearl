using System;
using System.Collections.Generic;
using EnderPearl.Core;
using EnderPearl.Player;
using global::Protocol.Packets;
using global::Protocol.Types;
using InteractAction = global::Protocol.InteractPacketPayload.Action;

namespace EnderPearl.Relay;

/// <summary>
/// Every rewrite the proxy applies to packet contents, in one place. One instance per
/// <see cref="ProxyConnection"/>, shared by the two relay handlers; the rewriter holds no per-direction
/// state, so both directions (and concurrent read threads) may call it freely.
///
/// <para>Serverbound (<see cref="RewriteServerbound"/>): stamps the authenticated identity onto what the
/// client says, and re-addresses client-local runtime ids to the current backend's ids.</para>
///
/// <para>Clientbound (<see cref="RewriteClientbound"/>): the mirror image - rewrites every id the backend
/// assigned into the id space the client already knows, across runtime ids, unique ids, entity links and
/// unique-id entity metadata. Anything handled here has a counterpart in the other direction; a packet
/// rewritten on one side only is the shape of bug this class exists to prevent. The clientbound half lives
/// in <see cref="PacketRewriter.Clientbound"/>.</para>
/// </summary>
public sealed partial class PacketRewriter
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
	/// Remaps the local player's runtime id on packets the client addresses to itself. The client keeps the
	/// id from its first StartGame for the whole session, while every backend assigns its own, so after a
	/// switch a packet still carrying the client's id names an entity the backend does not associate with
	/// this player. Protocol 2168 moves the inventory transaction's runtime id inside the use-on-actor
	/// variant, so that is the only transaction shape remapped here.
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
				npcRequest.NPCRuntimeID.Value = unchecked((ulong)connection.ToBackendRuntimeEntityId(unchecked((long)npcRequest.NPCRuntimeID.Value)));
				npcRequest.InvalidateWireCache();
				break;
			case CommandBlockUpdatePacket commandBlock
					when commandBlock.Target.Index == 0
					&& commandBlock.Target.AsT0?.TargetRuntimeID != null:
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
	/// Stamps the authenticated identity onto anything the client says. A modified client can otherwise
	/// send chat as any name it likes; the values here come from the Mojang-signed login chain, so
	/// overwriting them costs an honest client nothing and closes the impersonation. Packet type is left
	/// alone: with name and XUID corrected the remaining capability is an odd-looking self-message.
	/// </summary>
	private void NormalizeChatIdentity(IPacket packet)
	{
		if (packet is not TextPacket text)
		{
			return;
		}
		if (text.Body.Index != 1 || text.Body.Value is not global::Protocol.Types.TextPacketPayload.AuthorAndMessage body)
		{
			return;
		}
		string displayName = connection.ClientLogin.AuthData.DisplayName;
		string xuid = connection.ClientLogin.AuthData.Xuid;
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
}
