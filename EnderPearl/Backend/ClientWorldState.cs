using System;
using System.Collections.Generic;
using global::Protocol;
using global::Protocol.Packets;
using PlayerListPacketPayload = global::Protocol.Types.PlayerListPacketPayload;
using global::Protocol.Types;
using EnderPearl.Logging;

namespace EnderPearl.Backend
{
	/// <summary>
	/// What the client has been told about the world, tracked from the packets the relay forwards, so a
	/// backend switch can clear it before the target's own world state arrives: entities, riding links,
	/// player-list entries and boss bars.
	/// </summary>
	public sealed class ClientWorldState
	{
		// Java declared every method synchronized here: Track runs per relayed packet while a switch's
		// ClearPackets can run from the reset/failover path on another thread, and losing an entry in
		// that window means one stale entity/link/boss bar survives the switch.
		private readonly object stateLock = new();
		private readonly LinkedOrder<long> entityUniqueIds = new();
		private readonly LinkedOrder<EntityLinkKey> entityLinks = new();
		private readonly LinkedOrder<Guid> playerListEntries = new();
		private readonly LinkedOrder<long> bossBars = new();
		// WaterdogPE's EntityTracker also retires these on a server switch; without the tracking the
		// client keeps stale scoreboards, fog, hidden HUD elements, open containers and volume
		// entities from the world it just left, because the next backend never removes what it does
		// not know about.
		private readonly LinkedOrder<long> scoreboardIds = new();
		private readonly LinkedOrder<string> scoreboardObjectives = new();
		private readonly Dictionary<int, long> volumeEntities = new();
		private readonly Dictionary<byte, byte> openContainers = new();
		private readonly List<global::Protocol.HudElement> hiddenHudElements = new();
		private bool fogApplied;

		public void Track(IPacket packet)
		{
			lock (stateLock)
			{
				TrackLocked(packet);
			}
		}

		private void TrackLocked(IPacket packet)
		{
			switch (packet)
			{
				case AddActorPacket addActor:
				{
					AddEntity(addActor.TargetActorID?.Value ?? 0);
					if (addActor.ActorLinks != null)
					{
						foreach (ActorLink link in addActor.ActorLinks)
						{
							AddLink(link);
						}
					}
					break;
				}
				case AddPlayerPacket addPlayer:
				{
					// Protocol 2168 carries no unique-id field on AddPlayer; the client derives it.
					// Riding links and the player-list entry still need tracking.
					if (addPlayer.ActorLinks != null)
					{
						foreach (ActorLink link in addPlayer.ActorLinks)
						{
							AddLink(link);
						}
					}
					if (addPlayer.UUID != null && addPlayer.UUID is { MostSignificantBits: var msb, LeastSignificantBits: var lsb })
					{
						playerListEntries.Add(UuidCodec.ToGuid(msb, lsb));
					}
					break;
				}
				case AddItemActorPacket addItemActor:
				{
					AddEntity(addItemActor.TargetActorID?.Value ?? 0);
					break;
				}
				case AddPaintingPacket addPainting:
				{
					AddEntity(addPainting.TargetActorID?.Value ?? 0);
					break;
				}
				case RemoveActorPacket removeActor:
				{
					entityUniqueIds.Remove(removeActor.TargetActorID?.Value ?? 0);
					break;
				}
				case SetActorLinkPacket linkPacket when linkPacket.Link != null:
				{
					// ActorLinkType.None is this codec's encoding of "no link" - i.e. removal.
					if (linkPacket.Link.Type_ == ActorLinkType.None)
					{
						entityLinks.Remove(new EntityLinkKey(
							linkPacket.Link.TargetA?.Value ?? 0,
							linkPacket.Link.TargetB?.Value ?? 0));
					}
					else
					{
						AddLink(linkPacket.Link);
					}
					break;
				}
				case PlayerListPacket playerList:
				{
					TrackPlayerList(playerList);
					break;
				}
				case BossEventPacket bossEvent:
				{
					TrackBossEvent(bossEvent);
					break;
				}
				case SetDisplayObjectivePacket displayObjective when !string.IsNullOrEmpty(displayObjective.ObjectiveName):
				{
					scoreboardObjectives.Add(displayObjective.ObjectiveName);
					break;
				}
				case RemoveObjectivePacket removeObjective when !string.IsNullOrEmpty(removeObjective.ObjectiveName):
				{
					scoreboardObjectives.Remove(removeObjective.ObjectiveName);
					break;
				}
				case SetScorePacket setScore:
				{
					TrackScoreboardIds(setScore);
					break;
				}
				case AddVolumeEntityPacket addVolumeEntity:
				{
					volumeEntities[unchecked((int)(addVolumeEntity.EntityNetworkId?.RawId ?? 0))] = addVolumeEntity.DimensionType?.Value ?? 0;
					break;
				}
				case RemoveVolumeEntityPacket removeVolumeEntity:
				{
					volumeEntities.Remove(unchecked((int)(removeVolumeEntity.EntityNetworkId?.RawId ?? 0)));
					break;
				}
				case PlayerFogPacket fog:
				{
					// An empty stack means "clear all fog"; only a non-empty one leaves fog applied.
					fogApplied = fog.FogStack != null && fog.FogStack.Count > 0;
					break;
				}
				case SetHudPacket setHud:
				{
					TrackHiddenHud(setHud);
					break;
				}
				case ContainerOpenPacket containerOpen:
				{
					openContainers[containerOpen.ContainerId] = containerOpen.ContainerType;
					break;
				}
				case ContainerClosePacket containerClose:
				{
					if ((sbyte)containerClose.ContainerId < 0)
					{
						openContainers.Clear();
					}
					else
					{
						openContainers.Remove(containerClose.ContainerId);
					}
					break;
				}
			}
		}

		public void TrackClientContainerClose(byte containerId)
		{
			lock (stateLock)
			{
				// WaterdogPE treats the signed id -1 as "close everything".
				if ((sbyte)containerId < 0)
				{
					openContainers.Clear();
				}
				else
				{
					openContainers.Remove(containerId);
				}
			}
		}

		private void TrackScoreboardIds(SetScorePacket packet)
		{
			foreach (var entry in packet.ScoreInfo)
			{
				switch (entry.Index)
				{
					case 0:
						// RemoveScore: this scoreboard id's entries are gone (26.40+ removals carry
						// this shape; WaterdogPE mirrors it by deleting on REMOVE actions).
						scoreboardIds.Remove(entry.AsT0.ScoreboardId?.Value ?? 0);
						break;
					case 1:
						scoreboardIds.Add(entry.AsT1.ScoreboardId?.Value ?? 0);
						break;
					case 2:
						scoreboardIds.Add(entry.AsT2.ScoreboardId?.Value ?? 0);
						break;
					case 3:
						scoreboardIds.Add(entry.AsT3.ScoreboardId?.Value ?? 0);
						break;
				}
			}
		}

		private void TrackHiddenHud(SetHudPacket packet)
		{
			if (packet.HudElement == null)
			{
				return;
			}
			foreach (global::Protocol.HudElement element in packet.HudElement)
			{
				if (packet.HudVisible == global::Protocol.HudVisibility.Hide)
				{
					if (!hiddenHudElements.Contains(element))
					{
						hiddenHudElements.Add(element);
					}
				}
				else
				{
					hiddenHudElements.Remove(element);
				}
			}
		}

		public List<IPacket> ClearPackets()
		{
			lock (stateLock)
			{
				return ClearPacketsLocked();
			}
		}

		private List<IPacket> ClearPacketsLocked()
		{
			var packets = new List<IPacket>();
			foreach (EntityLinkKey link in entityLinks.Snapshot())
			{
				var removeLink = new SetActorLinkPacket();
				removeLink.Link = new ActorLink
				{
					TargetA = new ActorUniqueID { Value = link.From },
					TargetB = new ActorUniqueID { Value = link.To },
					Type_ = ActorLinkType.None,
					Immediate = false,
					PassengerInitiated = false
				};
				packets.Add(removeLink);
			}
			foreach (long bossBar in bossBars.Snapshot())
			{
				var removeBossBar = new BossEventPacket();
				removeBossBar.EventType = BossEventUpdateType.Remove;
				removeBossBar.TargetActorID = new ActorUniqueID { Value = bossBar };
				// This codec's Write emits every field unconditionally; Java's REMOVE branch stops
				// after the action. Fill the rest so the encode cannot throw on the nulls (the send
				// path would otherwise drop the whole cleanup batch).
				removeBossBar.PlayerID = new ActorUniqueID();
				removeBossBar.Name = "";
				removeBossBar.FilteredName = "";
				packets.Add(removeBossBar);
			}
			if (!playerListEntries.IsEmpty())
			{
				var removePlayers = new PlayerListPacket();
				removePlayers.Action = PlayerListPacketType.Remove;
				foreach (Guid uuid in playerListEntries.Snapshot())
				{
					var (msb, lsb) = UuidCodec.FromGuid(uuid);
					var entry = new PlayerListPacketPayload.RemoveEntry();
					entry.Action = PlayerListPacketType.Remove;
					entry.UUID = new global::Protocol.Types.mce.UUID
					{
						MostSignificantBits = msb,
						LeastSignificantBits = lsb
					};
					// 1.26.40 encodes the action per entry as well as once per packet, so setting only
					// the packet-level action above leaves a 2168 client's serializer nothing to write.
					removePlayers.Entries.Add(OneOf.OneOf<PlayerListPacketPayload.RemoveEntry, PlayerListPacketPayload.AddEntry>.FromT0(entry));
				}
				packets.Add(removePlayers);
			}
			foreach (long entityUniqueId in entityUniqueIds.Snapshot())
			{
				var removeEntity = new RemoveActorPacket();
				removeEntity.TargetActorID = new ActorUniqueID { Value = entityUniqueId };
				packets.Add(removeEntity);
			}
			foreach (long scoreboardId in scoreboardIds.Snapshot())
			{
				// One REMOVE entry per tracked scoreboard id; the empty optional objective name is
				// exactly what this codec's RemoveScore.Write emits for an absent objective.
				var removeScores = new SetScorePacket();
				removeScores.Action = global::Protocol.ScorePacketEntryAction.Remove;
				var removeScore = new global::Protocol.Types.RemoveScore();
				removeScore.Action = global::Protocol.ScorePacketEntryAction.Remove;
				removeScore.ScoreboardId = new global::Protocol.Types.ScoreboardId { Value = scoreboardId };
				removeScore.ObjectiveName = new Optional<string>();
				removeScores.ScoreInfo.Add(
					OneOf.OneOf<global::Protocol.Types.RemoveScore, global::Protocol.Types.ChangePlayerScore, global::Protocol.Types.ChangeEntityScore, global::Protocol.Types.ChangeFakePlayerScore>.FromT0(removeScore));
				packets.Add(removeScores);
			}
			foreach (string objectiveName in scoreboardObjectives.Snapshot())
			{
				var removeObjective = new RemoveObjectivePacket();
				removeObjective.ObjectiveName = objectiveName;
				packets.Add(removeObjective);
			}
			foreach (KeyValuePair<int, long> volumeEntity in volumeEntities)
			{
				var removeVolumeEntity = new RemoveVolumeEntityPacket();
				removeVolumeEntity.EntityNetworkId = new EntityNetId { RawId = unchecked((uint)volumeEntity.Key) };
				removeVolumeEntity.DimensionType = new DimensionType { Value = unchecked((int)volumeEntity.Value) };
				packets.Add(removeVolumeEntity);
			}
			if (fogApplied)
			{
				// An empty fog stack clears everything the previous server's plugins applied.
				packets.Add(new PlayerFogPacket());
			}
			if (hiddenHudElements.Count > 0)
			{
				var resetHud = new SetHudPacket();
				resetHud.HudElement.AddRange(hiddenHudElements);
				resetHud.HudVisible = global::Protocol.HudVisibility.Reset;
				packets.Add(resetHud);
			}
			foreach (KeyValuePair<byte, byte> container in openContainers)
			{
				var closeContainer = new ContainerClosePacket();
				closeContainer.ContainerId = container.Key;
				closeContainer.ContainerType = container.Value;
				closeContainer.ServerInitiatedClose = true;
				packets.Add(closeContainer);
			}
			// WaterdogPE injectClearWeather: a backend that never sent rain never sends the stop, so
			// rain from the world being left would otherwise fall forever. STOP_THUNDERSTORM data 0,
			// STOP_RAINING data 10000 (gradual fade), both at origin - Java's exact values.
			// Wire ids are the LEVEL_EVENTS TypeMap entries (Bedrock_v291: LEVEL_EVENT_WORLD+n =
			// 3000+n), NOT the cloudburst enum ordinals - this codec writes EventId verbatim.
			var stopThunderstorm = new LevelEventPacket();
			stopThunderstorm.EventId = 3004; // LevelEvent.STOP_THUNDERSTORM (LEVEL_EVENT_WORLD + 4)
			stopThunderstorm.Position = new Vec3 { X = 0f, Y = 0f, Z = 0f };
			stopThunderstorm.Data = 0;
			packets.Add(stopThunderstorm);
			var stopRaining = new LevelEventPacket();
			stopRaining.EventId = 3003; // LevelEvent.STOP_RAINING (LEVEL_EVENT_WORLD + 3)
			stopRaining.Position = new Vec3 { X = 0f, Y = 0f, Z = 0f };
			stopRaining.Data = 10000;
			packets.Add(stopRaining);
			int entityCount = entityUniqueIds.Count;
			int linkCount = entityLinks.Count;
			int playerCount = playerListEntries.Count;
			int bossBarCount = bossBars.Count;
			int scoreCount = scoreboardIds.Count;
			int objectiveCount = scoreboardObjectives.Count;
			int volumeCount = volumeEntities.Count;
			int containerCount = openContainers.Count;
			int hudCount = hiddenHudElements.Count;
			bool hadFog = fogApplied;
			entityUniqueIds.Clear();
			entityLinks.Clear();
			playerListEntries.Clear();
			bossBars.Clear();
			scoreboardIds.Clear();
			scoreboardObjectives.Clear();
			volumeEntities.Clear();
			openContainers.Clear();
			hiddenHudElements.Clear();
			fogApplied = false;
			if (ProxyConnection.IsPacketTracingConfigured())
			{
				Logger.Info(
					$"Prepared client world cleanup: entities={entityCount} links={linkCount} playerListEntries={playerCount} bossBars={bossBarCount} scores={scoreCount} objectives={objectiveCount} volumes={volumeCount} containers={containerCount} hudElements={hudCount} fog={(hadFog ? "true" : "false")} packets={packets.Count}.");
			}
			return packets;
		}

		private void AddEntity(long uniqueEntityId)
		{
			if (uniqueEntityId != 0)
			{
				entityUniqueIds.Add(uniqueEntityId);
			}
		}

		private void AddLink(ActorLink link)
		{
			entityLinks.Add(new EntityLinkKey(link.TargetA?.Value ?? 0, link.TargetB?.Value ?? 0));
		}

		private void TrackPlayerList(PlayerListPacket packet)
		{
			foreach (var entry in packet.Entries)
			{
				if (entry.Index == 1 && entry.Value is PlayerListPacketPayload.AddEntry addEntry)
				{
					if (packet.Action == PlayerListPacketType.Add && addEntry.UUID != null)
					{
						playerListEntries.Add(UuidCodec.ToGuid(addEntry.UUID.MostSignificantBits, addEntry.UUID.LeastSignificantBits));
					}
				}
				else if (entry.Index == 0 && entry.Value is PlayerListPacketPayload.RemoveEntry removeEntry)
				{
					if (packet.Action == PlayerListPacketType.Remove && removeEntry.UUID != null)
					{
						playerListEntries.Remove(UuidCodec.ToGuid(removeEntry.UUID.MostSignificantBits, removeEntry.UUID.LeastSignificantBits));
					}
				}
			}
		}

		private void TrackBossEvent(BossEventPacket packet)
		{
			long id = packet.TargetActorID?.Value ?? 0;
			// Java reacted to CREATE and REMOVE only: REGISTER_PLAYER(1)/UNREGISTER_PLAYER(3) describe
			// the local viewer's subscription to a bar that still exists, so treating them as
			// create/remove here made a switched-away world leave visible boss bars behind (or emit
			// REMOVE cleanups for bars that were never created).
			if (packet.EventType == BossEventUpdateType.Add)
			{
				bossBars.Add(id);
			}
			else if (packet.EventType == BossEventUpdateType.Remove)
			{
				bossBars.Remove(id);
			}
		}

		private readonly record struct EntityLinkKey(long From, long To);

		/// <summary>Insertion-ordered set semantics on a list.</summary>
		private sealed class LinkedOrder<T> where T : notnull
		{
			private readonly List<T> items = new();
			private readonly HashSet<T> seen = new();

			public int Count => items.Count;

			public bool IsEmpty() => items.Count == 0;

			public void Add(T item)
			{
				if (item is long l && l == 0)
				{
					return;
				}
				if (seen.Add(item))
				{
					items.Add(item);
				}
			}

			public void Remove(T item)
			{
				if (seen.Remove(item))
				{
					items.RemoveAll(i => EqualityComparer<T>.Default.Equals(i, item));
				}
			}

			public List<T> Snapshot() => new(items);

			public void Clear()
			{
				items.Clear();
				seen.Clear();
			}
		}
	}

	/// <summary>Java-UUID-compatible conversions between Guid and the two 64-bit halves.</summary>
	internal static class UuidCodec
	{
		public static Guid ToGuid(ulong mostSignificantBits, ulong leastSignificantBits)
		{
			var bytes = new byte[16];
			for (int i = 0; i < 8; i++)
			{
				bytes[i] = (byte)(mostSignificantBits >> 56 - 8 * i);
				bytes[8 + i] = (byte)(leastSignificantBits >> 56 - 8 * i);
			}
			return new Guid(bytes);
		}

		public static (ulong Msb, ulong Lsb) FromGuid(Guid guid)
		{
			byte[] bytes = guid.ToByteArray();
			// Guid.ToByteArray writes the first three groups little-endian; undo that to recover the
			// raw byte order the wire uses.
			byte[] ordered =
			{
				bytes[3], bytes[2], bytes[1], bytes[0],
				bytes[5], bytes[4],
				bytes[7], bytes[6],
				bytes[8], bytes[9], bytes[10], bytes[11], bytes[12], bytes[13], bytes[14], bytes[15]
			};
			ulong msb = 0;
			ulong lsb = 0;
			for (int i = 0; i < 8; i++)
			{
				msb = msb << 8 | ordered[i];
				lsb = lsb << 8 | ordered[8 + i];
			}
			return (msb, lsb);
		}
	}
}
