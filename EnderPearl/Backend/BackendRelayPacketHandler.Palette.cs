using System;
using System.Collections.Generic;
using EnderPearl.Codec;
using EnderPearl.Net;
using EnderPearl.Palette;
using global::Protocol.Packets;
using CompoundTag = global::Protocol.Types.CompoundTag;
using EnderPearl.Logging;

namespace EnderPearl.Backend
{
	/// <summary>
	/// The cross-backend palette half of <see cref="BackendRelayPacketHandler"/>: keeps custom items,
	/// blocks and entities rendering correctly across a seamless backend switch.
	///
	/// <para>Bedrock reads its item registry (ItemRegistryPacket), its block definitions
	/// (StartGame.BlockProperties), its entity identifier list and its entity property lists exactly
	/// once, at level init, and a switch deliberately does not re-run level init. So whatever the client
	/// is told on its <em>first</em> backend is what it still believes on every later one. At login the
	/// client is therefore given the union of every backend's registries; on a later backend these
	/// packets are learned, used to rebuild the mapping, and dropped - resending them would tell the
	/// client something it cannot act on.</para>
	///
	/// <para>Java counterpart: BackendRelayPacketHandler.handleCrossBackendPalette and its two helpers.</para>
	/// </summary>
	public sealed partial class BackendRelayPacketHandler
	{
		/// <returns>true when the packet has been fully handled and must not be forwarded</returns>
		private bool HandleCrossBackendPalette(IPacket packet, long traceSequence)
		{
			CrossBackendPalette palette = connection.CrossBackendPalette;
			if (packet is StartGamePacket startGame)
			{
				// Applied on every StartGame, switches included: the union is a superset of what this
				// backend sent, so a client that acts on a later StartGame is no worse off for it.
				if (palette.ApplyToStartGame(backendName, startGame))
				{
					startGame.InvalidateWireCache();
				}
				// The client keeps whichever scheme its first StartGame carried. Recorded here rather
				// than inside the palette because it is a property of this player's session, not of the
				// backend, and it is what decides how they can be moved from now on.
				connection.RememberClientBlockIdsHashed(startGame.BlockNetworkIdsAreHashes);
				return false;
			}
			if (packet is ItemRegistryPacket itemComponent)
			{
				List<global::Protocol.Types.ItemData> backendItems = new List<global::Protocol.Types.ItemData>(itemComponent.ItemData);
				if (backendItems.Count == 0)
				{
					return false;
				}
				palette.Store.LearnItems(backendName, backendItems);
				bool firstBackend = !palette.HasClientItems();
				if (firstBackend)
				{
					List<global::Protocol.Types.ItemData> union = palette.BuildClientItems(backendName, backendItems);
					itemComponent.ItemData.Clear();
					itemComponent.ItemData.AddRange(union);
					itemComponent.InvalidateWireCache();
				}
				InstallItemPaletteMapping(backendItems);
				if (firstBackend)
				{
					return false;
				}
				if (connection.IsPacketTraceActive())
				{
					Logger.Info(
						$"Suppressed clientbound #{traceSequence} item registry from backend {backendName}: the client's registry was "
						+ "fixed at login and now maps through the cross-backend palette.");
				}
				return true;
			}
			if (packet is AvailableActorIdentifiersPacket entityIdentifiers)
			{
				palette.Store.LearnEntityIdentifiers(backendName, entityIdentifiers.IdentifierList);
				if (palette.ClientEntityIdentifiers != null)
				{
					return true;
				}
				CompoundTag merged = palette.BuildClientEntityIdentifiers(backendName, entityIdentifiers.IdentifierList);
				if (!ReferenceEquals(merged, entityIdentifiers.IdentifierList))
				{
					entityIdentifiers.IdentifierList = merged;
					entityIdentifiers.InvalidateWireCache();
				}
				SendForeignEntityProperties();
				return false;
			}
			if (packet is SyncActorPropertyPacket entityProperty)
			{
				palette.Store.LearnEntityProperty(backendName, entityProperty.PropertyData);
				// One list per entity type is all the client keeps; a second backend's copy of the same
				// type is noise, and a type it has already been told about must not be re-sent.
				return !palette.MarkEntityPropertySent(entityProperty.PropertyData);
			}
			return false;
		}

		/// <summary>
		/// Sends the entity property lists belonging to backends this player has not visited, so their
		/// entities behave correctly the moment they switch. Sent with the identifier list, which is the
		/// last of the definition burst and still ahead of any entity spawn.
		/// </summary>
		private void SendForeignEntityProperties()
		{
			List<global::Protocol.Types.CompoundTag> pending = connection.CrossBackendPalette.PendingEntityProperties(backendName);
			foreach (global::Protocol.Types.CompoundTag property in pending)
			{
				SyncActorPropertyPacket packet = new SyncActorPropertyPacket();
				packet.PropertyData = property;
				connection.Client().SendPacket(packet);
			}
			if (pending.Count > 0
				&& connection.CrossBackendPalette.Store.FirstReportOf("properties:" + backendName + ":" + pending.Count))
			{
				Logger.Info(
					$"Sent {pending.Count} entity property list(s) from other backends to a client joining {backendName}.");
			}
		}

		private void InstallItemPaletteMapping(List<global::Protocol.Types.ItemData> backendItems)
		{
			ItemPaletteMapping? mapping = connection.CrossBackendPalette.MappingFor(backendName, backendItems);
			if (mapping == null)
			{
				return;
			}
			CodecDefinitionState.InstallItemMapping(backend, connection.Client(), mapping);
			if (connection.IsPacketTraceActive())
			{
				Logger.Info(
					$"Installed item palette mapping for backend {backendName}: "
					+ (mapping.IsIdentity ? "ids already agree" : "ids remapped to the client's registry") + ".");
			}
		}
	}
}
