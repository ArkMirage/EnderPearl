using System;
using System.Collections.Generic;
using System.Globalization;
using EnderPearl.Core;
using global::Protocol.Packets;

namespace EnderPearl.Relay
{
	/// <summary>
	/// Diagnostic drop/neuter switches for the clientbound relay, plus the per-packet clientbound
	/// trace details (port of <c>BackendRelayPacketHandler</c> lines 203-523 and 2530-3202).
	/// </summary>
	public sealed partial class BackendRelayPacketHandler
	{
		/// <summary>
		/// Whether a backend-side runtime entity id is the player's own. Detail lines are printed before
		/// <see cref="PacketRewriter.RewriteClientbound"/>, so the id here is still the backend's; both
		/// sides are checked because they are usually equal but need not be.
		/// </summary>
		private string IsClientOwnRuntimeEntityId(long backendRuntimeEntityId)
		{
			if (backendRuntimeEntityId <= 0)
			{
				return "false";
			}
			bool own = backendRuntimeEntityId == Connection.BackendPlayerRuntimeEntityId()
				|| backendRuntimeEntityId == Connection.ClientPlayerRuntimeEntityId();
			return own ? "SELF" : "false";
		}

		private void LogClientboundDetails(IPacket packet)
		{
			if (packet is NetworkChunkPublisherUpdatePacket publisherUpdate)
			{
				Logger.Info(
					$"  NetworkChunkPublisherUpdate position=({publisherUpdate.NewPositionForView?.X ?? 0}, {publisherUpdate.NewPositionForView?.Y ?? 0}, {publisherUpdate.NewPositionForView?.Z ?? 0}) radius={publisherUpdate.NewRadiusForView} savedChunks={publisherUpdate.ServerBuiltChunksList?.Count ?? 0} playerDimension={Connection.PlayerDimensionId()} requestedRadius={Connection.LastRequestedChunkRadius()}/{Connection.LastRequestedMaxChunkRadius()}."
				);
			}
			else if (packet is ChunkRadiusUpdatedPacket radiusUpdated)
			{
				Logger.Info(
					$"  ChunkRadiusUpdated radius={radiusUpdated.ChunkRadius} rememberedRadius={Connection.LastRequestedChunkRadius()}/{Connection.LastRequestedMaxChunkRadius()}."
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
		/// Clientbound packet types to drop instead of relaying, from
		/// <c>proxy.dropClientbound=SetEntityData,AddEntity</c>. Names match with or without the
		/// <c>Packet</c> suffix and ignore case.
		///
		/// <para>A bisection tool, for when static analysis has run out. Every codec-level check available
		/// has come back clean on the 1.26.40→1.26.30 disconnect — no failed encodes, no failed
		/// decodes, and a clean outbound read-back — while the client still closes the connection
		/// abruptly, mid-stream, with no message. At that point the question is no longer "which field is
		/// wrong" but "which packet family is involved at all", and the fastest way to answer it is to
		/// stop sending one and see whether the session survives.</para>
		///
		/// <para>The same approach settled the death-disconnect investigation, where
		/// <c>proxy.noStartGameFixups</c> and <c>proxy.noCommandInjection</c> eliminated the proxy's
		/// two behavioural differences in a single run each.</para>
		///
		/// <para>Diagnostic only, and deliberately blunt: suppressing a packet family breaks whatever it
		/// drives. Dropping the entity families leaves mobs invisible or frozen, which is fine — the
		/// question being asked is whether the player is still connected five minutes later, not whether
		/// the world looks right.</para>
		/// </summary>
		private static readonly HashSet<string> DIAGNOSTIC_DROPPED_CLIENTBOUND = ParseDroppedClientbound();

		/// <summary>
		/// How much of a neutered packet's real content to keep. See
		/// <see cref="DIAGNOSTIC_NEUTERED_CLIENTBOUND"/>.
		/// </summary>
		// Internal rather than private because ParseNeuterSpec/Neuter carry it in internal signatures;
		// Java declared the enum public on the class for the same reachability reason.
		internal enum NeuterMode
		{
			/// <summary>
			/// Smallest valid body: for <c>MoveActorDelta</c>, the runtime id and every optional absent —
			/// roughly 11 bytes against ~26 for a real one, because the presence booleans are still
			/// written. Changes content <em>and</em> byte volume, so a survival under this mode does not by
			/// itself distinguish the two.
			/// </summary>
			MINIMAL,
			/// <summary>
			/// Byte-for-byte the same length as the real packet: every optional is kept, with its real
			/// value, and only the trailing booleans are normalised. Entities still move, the client still
			/// interpolates, and the encoded size is unchanged — so this isolates <em>only</em> the flag
			/// semantics, which is the one family that has already produced three bugs on this hop.
			/// </summary>
			SAME_SIZE
		}

		/// <summary>
		/// Clientbound packet types to relay with neutered content, from
		/// <c>proxy.neuterClientbound=MoveEntityDelta</c> or
		/// <c>proxy.neuterClientbound=MoveEntityDelta:samesize,SetEntityMotion</c>. Names match with or
		/// without the <c>Packet</c> suffix and ignore case; the mode defaults to <c>minimal</c>.
		///
		/// <para><b>Why this exists.</b> <c>proxy.dropClientbound</c> established that survival on the
		/// 1.26.40→1.26.30 hop goes from ~5s to 17-54s when <c>MoveEntityDelta</c> and
		/// <c>SetEntityMotion</c> are suppressed, and that suppressing a 12% slice does nothing. But those
		/// two packets are also ~60% of all clientbound traffic, so for them "the suspect" and "the volume"
		/// are the same variable and <b>no drop experiment can separate content from rate</b>.</para>
		///
		/// <para>Neutering breaks that tie by holding the packet count fixed and changing only what the
		/// packets say. Read the result as:</para>
		///
		/// <list type="bullet">
		/// <item><c>samesize</c> survives → the four trailing booleans are the bug. Same count, same
		///       bytes, same positions; only the flags differ.</item>
		/// <item><c>samesize</c> still dies at ~6s but <c>minimal</c> survives → not the flags. It is
		///       the positional payload's content, or the byte volume the optionals carry.</item>
		/// <item>both still die at ~6s → content is exonerated at the packet layer and the cause is
		///       packet <i>count</i>. The search moves below the packet layer, to compression and the transport
		///       fragmentation.</item>
		/// </list>
		///
		/// <para>Both modes report every entity as grounded and force nothing, and <c>minimal</c> freezes
		/// entities where they stand. Diagnostic only: the question a neutered run answers is whether the
		/// player is still connected, not whether the world looks right.</para>
		/// </summary>
		private static readonly Dictionary<string, NeuterMode> DIAGNOSTIC_NEUTERED_CLIENTBOUND =
			ParseNeuteredClientbound();

		/// <summary>
		/// The packet types <see cref="NeuterForDiagnostics"/> actually knows how to neuter. A name outside
		/// this set is rejected at startup rather than ignored: a run configured with a typo would otherwise
		/// relay everything untouched and its ~6s disconnect would look like a result, which is the same
		/// "no way to tell the flag did not take" trap that cost the 15:50Z capture.
		///
		/// <para>A method rather than a <c>static readonly</c> field on purpose: the field it is consulted
		/// from is initialised earlier in this class, so as a field it would read back as null during static
		/// initialisation. A method has no declaration-order hazard.</para>
		/// </summary>
		private static HashSet<string> NeuterableClientbound()
		{
			return new HashSet<string> { "moveentitydeltapacket", "setentitymotionpacket", "subchunkpacket" };
		}

		private static Dictionary<string, NeuterMode> ParseNeuteredClientbound()
		{
			Dictionary<string, NeuterMode> modes = ParseNeuterSpec(
				Environment.GetEnvironmentVariable("proxy.neuterClientbound") ?? "");
			if (modes.Count > 0)
			{
				Logger.Info($"Diagnostics: neutering clientbound packets {FormatModeMap(modes)} before relay.");
			}
			return modes;
		}

		/// <summary>
		/// Internal and taking the raw string so the syntax can be pinned by a test. Rejects an
		/// unrecognised name or mode by throwing, which surfaces at class-init and therefore at startup.
		/// </summary>
		internal static Dictionary<string, NeuterMode> ParseNeuterSpec(string value)
		{
			if (string.IsNullOrWhiteSpace(value))
			{
				return new Dictionary<string, NeuterMode>();
			}
			var modes = new Dictionary<string, NeuterMode>();
			foreach (string entry in value.Split(','))
			{
				string trimmed = entry.Trim();
				if (trimmed.Length == 0)
				{
					continue;
				}
				string name = trimmed;
				NeuterMode mode = NeuterMode.MINIMAL;
				int separator = trimmed.IndexOf(':');
				if (separator >= 0)
				{
					name = trimmed.Substring(0, separator).Trim();
					string requested = trimmed.Substring(separator + 1).Trim().ToLowerInvariant();
					switch (requested)
					{
						case "minimal":
						case "":
							mode = NeuterMode.MINIMAL;
							break;
						case "samesize":
						case "same-size":
						case "keepsize":
							mode = NeuterMode.SAME_SIZE;
							break;
						default:
							throw new ArgumentException(
								"Unknown -Dproxy.neuterClientbound mode '" + requested
									+ "' for " + name + "; expected minimal or samesize.");
					}
				}
				string normalized = name.ToLowerInvariant();
				if (!normalized.EndsWith("packet", StringComparison.Ordinal))
				{
					normalized = normalized + "packet";
				}
				if (!NeuterableClientbound().Contains(normalized))
				{
					throw new ArgumentException(
						"-Dproxy.neuterClientbound cannot neuter '" + name + "'. Supported: "
							+ FormatNameSet(NeuterableClientbound())
							+ ". Add a case to BackendRelayPacketHandler.NeuterForDiagnostics first — "
							+ "silently ignoring it would make the run answer nothing.");
				}
				modes[normalized] = mode;
			}
			return modes;
		}

		/// <summary>Rendered into the startup <c>Diagnostics:</c> line so a run's posture is always visible.</summary>
		public static string DiagnosticSuppressionSummary()
		{
			return $"dropClientbound={(DIAGNOSTIC_DROPPED_CLIENTBOUND.Count == 0 ? "none" : FormatNameSet(DIAGNOSTIC_DROPPED_CLIENTBOUND))}"
				+ $" neuterClientbound={(DIAGNOSTIC_NEUTERED_CLIENTBOUND.Count == 0 ? "none" : FormatModeMap(DIAGNOSTIC_NEUTERED_CLIENTBOUND))}";
		}

		// Java rendered these collections through AbstractCollection/AbstractMap.toString inside printf;
		// C# collection ToString does not render contents, so the exact shapes ("[a, b]", "{k=v}") are
		// reproduced here to keep every diagnostics log line identical.
		private static string FormatNameSet(HashSet<string> names)
		{
			return "[" + string.Join(", ", names) + "]";
		}

		private static string FormatModeMap(Dictionary<string, NeuterMode> modes)
		{
			var parts = new List<string>(modes.Count);
			foreach (KeyValuePair<string, NeuterMode> entry in modes)
			{
				parts.Add(entry.Key + "=" + entry.Value);
			}
			return "{" + string.Join(", ", parts) + "}";
		}

		private readonly HashSet<string> reportedDiagnosticDrops = new();
		private readonly HashSet<string> reportedDiagnosticNeuters = new();

		private static HashSet<string> ParseDroppedClientbound()
		{
			// Java read these from system properties (-Dproxy.dropClientbound=...); environment variables
			// play that role here, matching ClientRelayPacketHandler.ReadIntProperty.
			string value = Environment.GetEnvironmentVariable("proxy.dropClientbound") ?? "";
			if (string.IsNullOrWhiteSpace(value))
			{
				return new HashSet<string>();
			}
			var names = new HashSet<string>();
			foreach (string name in value.Split(','))
			{
				string trimmed = name.Trim();
				if (trimmed.Length == 0)
				{
					continue;
				}
				string normalized = trimmed.ToLowerInvariant();
				names.Add(normalized.EndsWith("packet", StringComparison.Ordinal) ? normalized : normalized + "packet");
			}
			Logger.Info($"Diagnostics: dropping clientbound packets {FormatNameSet(names)} before relay.");
			return names;
		}

		private bool IsSuppressedForDiagnostics(global::Protocol.Packets.IPacket packet)
		{
			if (DIAGNOSTIC_DROPPED_CLIENTBOUND.Count == 0)
			{
				return false;
			}
			string name = packet.GetType().Name;
			if (!DIAGNOSTIC_DROPPED_CLIENTBOUND.Contains(name.ToLowerInvariant()))
			{
				return false;
			}
			// Once per type per session: these are the highest-volume packets there are, and a line each
			// would bury the rest of the log in exactly the run where the log matters.
			if (reportedDiagnosticDrops.Add(name))
			{
				Logger.Info($"Diagnostics: suppressing clientbound {name} from backend {BackendName}.");
			}
			return true;
		}

		/// <summary>
		/// Strip the content out of a packet in place, keeping its identity and its place in the stream.
		///
		/// <para>Applied to the freshly decoded backend packet, before runtime-id rewriting, so the
		/// neutered content travels the exact path real content does.</para>
		/// </summary>
		private void NeuterForDiagnostics(global::Protocol.Packets.IPacket packet)
		{
			if (DIAGNOSTIC_NEUTERED_CLIENTBOUND.Count == 0)
			{
				return;
			}
			string name = packet.GetType().Name;
			if (!DIAGNOSTIC_NEUTERED_CLIENTBOUND.TryGetValue(name.ToLowerInvariant(), out NeuterMode mode))
			{
				return;
			}
			Neuter(packet, mode);
			if (reportedDiagnosticNeuters.Add(name))
			{
				Logger.Info(
					$"Diagnostics: neutering clientbound {name} ({mode}) from backend {BackendName}.");
			}
		}

		/// <summary>
		/// A complete, valid sub-chunk block payload containing nothing: format version 8, zero block
		/// storages, which every version from v471 onwards reads as "entirely air".
		///
		/// <para>Deliberately storage-free rather than a one-entry air palette, because a palette entry would
		/// have to carry a block network id and those are <em>hashes</em> of block state NBT on this hop
		/// (<c>blockNetworkIdsHashed=true</c>) — the proxy does not have the client's block registry and
		/// could not compute one. Zero storages needs no registry at all, which is what makes this usable
		/// as a neuter.</para>
		///
		/// <para>The reader stops after the declared storage count, so appending arbitrary bytes after these
		/// two keeps the payload valid at <em>any</em> length ≥ 2. That is what lets
		/// <see cref="NeuterMode.SAME_SIZE"/> hold the byte count fixed while removing all real block
		/// content.</para>
		/// </summary>
		private static readonly byte[] EMPTY_SUB_CHUNK_PAYLOAD = { 8, 0 };

		/// <summary>
		/// The transform itself, in place. Static and internal so a test can assert the property the
		/// whole experiment rests on — that <see cref="NeuterMode.SAME_SIZE"/> really does encode to the
		/// same number of bytes — without having to set an environment variable before this class loads.
		/// A <c>SAME_SIZE</c> that quietly changed the packet's length would silently reintroduce the
		/// volume confound it exists to remove, and the run would look like a clean answer.
		///
		/// <para><b>The <c>SubChunk</c> neuter is the one this hop still needs, and it is why the two modes
		/// matter.</b> <c>proxy.dropClientbound=SubChunk</c> makes the session immortal, which reads as a
		/// clean isolation — but it also removes about 90% of all clientbound <em>bytes</em>, so it is the
		/// same volume confound that has defeated every earlier bisect here, one packet further along. The
		/// envelope itself has since been verified byte-for-byte against gophertunnel PR #481 and the
		/// <c>r26_u4</c> dump and is correct, so what a drop cannot tell apart is the payload's content
		/// from the payload's size. These two modes do:</para>
		///
		/// <list type="bullet">
		/// <item><c>samesize</c> keeps every entry, every heightmap, every result and the exact encoded
		///       length, replacing only the opaque block payload with an equally long empty one. Survives
		///       → the <b>content</b> of the terrain payload is the cause, i.e. 1.26.30 block data a
		///       1.26.40 client will not accept. Still dies → content is exonerated and the cause is
		///       byte volume or packet count.</item>
		/// <item><c>minimal</c> cuts each payload to two bytes, so it cuts content and volume together. It
		///       is the control: if <c>samesize</c> dies and <c>minimal</c> survives, the variable is
		///       size alone and no amount of payload translation will help.</item>
		/// </list>
		///
		/// <para>Expect no terrain to render under either mode. As always, the question is whether the player
		/// is still connected, not whether the world looks right — but note this neuter leaves the client
		/// free to move, because the chunk still completes.</para>
		/// </summary>
		internal static void Neuter(global::Protocol.Packets.IPacket packet, NeuterMode mode)
		{
			if (packet is global::Protocol.Packets.MoveActorDeltaPacket move)
			{
				global::Protocol.Types.MoveActorDeltaData data = move.MoveData;
				if (mode == NeuterMode.MINIMAL)
				{
					// Every optional absent. The entity stops where it is; the packet keeps arriving.
					// In this codec presence and value live in one Optional struct (there is no separate
					// HAS_* EnumSet like Java's), so clearing the flags and zeroing the components
					// collapse into the same assignment: an absent optional writes nothing at all.
					data.NewPositionX = new global::Optional<float>();
					data.NewPositionY = new global::Optional<float>();
					data.NewPositionZ = new global::Optional<float>();
					data.RotationX = new global::Optional<sbyte>();
					data.RotationY = new global::Optional<sbyte>();
					data.RotationYHead = new global::Optional<sbyte>();
				}
				else
				{
					// Keep every optional present with its real value so the encoded length does not
					// move, and normalise only the trailing booleans below, which carry meaning rather
					// than presence. Java also removed the TELEPORTING flag here; this codec has no
					// teleporting representation on MoveActorDeltaData at all (no flag, no boolean), so
					// there is nothing left to clear for it.
				}
				// On-ground true in both modes, and deliberately not false — the HANDOFF's sketch said all
				// four booleans false, but a client told an entity is unsupported runs its own physics for
				// it, so all-false would swap one suspect content for another known-bad one (see the note
				// on MoveEntityDeltaSerializer_v2168). Java had to clear the ON_GROUND flag because the
				// v2168 writer ORs the boolean with that flag; here there is no flag, the boolean alone
				// decides.
				data.IsOnGround = true;
				data.ForceMove = false;
				data.ForceMoveLocalEntity = false;
				data.ForceCompletion = false;
			}
			else if (packet is global::Protocol.Packets.SetActorMotionPacket motion)
			{
				// Fixed shape, so both modes are the same neuter and neither changes the encoded length.
				motion.Motion = new global::Protocol.Types.Vec3();
			}
			else if (packet is global::Protocol.Packets.SubChunkPacket subChunkPacket)
			{
				foreach (global::Protocol.Types.SubChunkPacketPayload.SubChunkPacketData subChunk
					in subChunkPacket.SubChunkData)
				{
					global::Optional<byte[]> original = subChunk.SerializedSubChunk;
					if (original == null || !original.HasValue || original.Value == null)
					{
						// SUCCESS_ALL_AIR with the blob cache on carries no payload at all.
						continue;
					}
					int length = mode == NeuterMode.MINIMAL
						? EMPTY_SUB_CHUNK_PAYLOAD.Length
						: Math.Max(original.Value.Length, EMPTY_SUB_CHUNK_PAYLOAD.Length);
					byte[] replacement = new byte[length];
					Array.Copy(EMPTY_SUB_CHUNK_PAYLOAD, 0, replacement, 0, EMPTY_SUB_CHUNK_PAYLOAD.Length);
					subChunk.SerializedSubChunk = new global::Optional<byte[]>(replacement);
					// Java released the replaced ByteBuf's refcount here; the old array is garbage
					// collected once nothing references it.
				}
			}
			else
			{
				// Unreachable: ParseNeuteredClientbound rejects anything not handled above.
				throw new InvalidOperationException("No neuter implemented for " + packet.GetType().Name);
			}
		}
	}
}
