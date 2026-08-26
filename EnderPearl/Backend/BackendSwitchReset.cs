using System;
using System.Collections.Generic;
using System.Threading;
using EnderPearl.Listener;
using global::Protocol;
using global::Protocol.Packets;
using global::Protocol.Utility.IO;
using global::Protocol.Types;
using EnderPearl.Logging;

namespace EnderPearl.Backend
{
	/// <summary>
	/// The dimension-change dance a backend switch performs on the client: bounce it through a fake
	/// dimension (a real ChangeDimension is the only way to make a Bedrock client flush its world),
	/// seed empty chunks around the landing site, then complete once the client acks - replaying
	/// everything the target backend streamed while the client was in transit.
	///
	/// <p>State machine phases: AWAITING_FIRST_ACK -> (AWAITING_SECOND_ACK when the source and target
	/// dimensions match and a fake hop was needed) -> COMPLETE.</p>
	/// </summary>
	public sealed class BackendSwitchReset
	{
		private const int DIMENSION_OVERWORLD = 0;
		private const int DIMENSION_NETHER = 1;
		private const int DIMENSION_END = 2;
		private const int RESET_CHUNK_RADIUS = 3;

		// Last-resort timeout for completing a phase when the client never acknowledges the injected
		// dimension change. If the fallback fires too early it completes the switch before the client
		// is actually in the new world, which then makes the client's late loading-screen-start get
		// misread as a death respawn. Keep this comfortably longer than the worst observed client delay.
		private const long ACK_FALLBACK_MILLIS = 8000;

		private static int loadingScreenIds;

		private readonly BackendSession backend;
		private readonly string backendName;
		private readonly long backendRuntimeEntityId;
		private readonly long clientRuntimeEntityId;
		private readonly int targetDimension;
		private readonly Vec3 targetPosition;
		private readonly Vec2 targetRotation;
		private readonly bool secondDimensionChangeRequired;
		private readonly BackendSwitchInputState inputState;

		private Phase phase = Phase.AWAITING_FIRST_ACK;

		// Active ack-fallback timers MUST stay referenced: an unreferenced System.Threading.Timer is
		// garbage-collectible and silently never fires, which strands the reset forever.
		private readonly List<System.Threading.Timer> ackFallbackTimers = new();

		private enum Phase
		{
			AWAITING_FIRST_ACK,
			AWAITING_SECOND_ACK,
			COMPLETE
		}

		private BackendSwitchReset(
			BackendSession backend,
			string backendName,
			long backendRuntimeEntityId,
			long clientRuntimeEntityId,
			int targetDimension,
			Vec3 targetPosition,
			Vec2 targetRotation,
			bool secondDimensionChangeRequired,
			uint targetInputLockData
		)
		{
			this.backend = backend;
			this.backendName = backendName;
			this.backendRuntimeEntityId = backendRuntimeEntityId;
			this.clientRuntimeEntityId = clientRuntimeEntityId;
			this.targetDimension = targetDimension;
			this.targetPosition = targetPosition;
			this.targetRotation = targetRotation;
			this.secondDimensionChangeRequired = secondDimensionChangeRequired;
			inputState = new BackendSwitchInputState(targetInputLockData);
		}

		internal static BackendSwitchReset Start(
			ProxyConnection connection,
			BackendSession backend,
			string backendName,
			int sourceDimension,
			StartGamePacket startGame,
			uint targetInputLockData
		)
		{
			int targetDimension = startGame.Settings?.SpawnSettings?.Dimension ?? DIMENSION_OVERWORLD;
			Vec3 targetPosition = startGame.Position ?? Zero();
			Vec2 targetRotation = startGame.Rotation ?? new Vec2();
			long runtimeEntityId = connection.BackendPlayerRuntimeEntityId();
			long clientEntityId = connection.ClientPlayerRuntimeEntityId();
			bool needsFakeDimension = sourceDimension == targetDimension;

			var reset = new BackendSwitchReset(
				backend,
				backendName,
				runtimeEntityId,
				clientEntityId,
				targetDimension,
				targetPosition,
				targetRotation,
				needsFakeDimension,
				targetInputLockData
			);
			connection.SetBackendSwitchReset(reset);

			// The client session is continuous across switches, so effects drunk on the previous
			// backend are still displayed even though this backend knows nothing about them. Clear
			// them now, before the dimension bounce; the new backend's own join burst re-adds
			// whatever it considers active.
			//
			// The remove targets the CLIENT's runtime id for the player - the id the client has been
			// seeing since its very first backend - not the new backend's, which the client cannot
			// map to itself yet.
			foreach (int effectId in connection.TakeActiveClientEffects())
			{
				connection.Client().SendPacket(new MobEffectPacket
				{
					TargetRuntimeID = new global::Protocol.Types.ActorRuntimeID { Value = unchecked((ulong)clientEntityId) },
					EventID = global::Protocol.MobEffectPacketPayload.Event.Remove,
					EffectID = effectId,
					ShowParticles = false,
					Tick = new global::Protocol.Types.PlayerInputTick()
				});
			}

			int firstDimension = needsFakeDimension ? AlternateDimension(targetDimension) : targetDimension;
			Vec3 firstPosition = needsFakeDimension ? Add(targetPosition, 2000, 0, 2000) : targetPosition;
			// A normal TransferPacket reconnect clears input-permission state as a side effect; a
			// seamless proxy handoff does not, so clear the source backend's mask explicitly before
			// entering the dimension transition.
			connection.Client().SendPacket(reset.inputState.ClearSource(firstPosition));
			reset.InjectPosition(connection, firstPosition);
			reset.InjectDimensionChange(connection, firstDimension, firstPosition, true);
			reset.ScheduleAckFallback(connection);
			if (connection.IsPacketTraceActive())
			{
				Logger.Info(
					$"Started backend switch reset for {backendName}: sourceDimension={sourceDimension} targetDimension={targetDimension} firstDimension={firstDimension} runtimeEntityId={runtimeEntityId} secondPhase={needsFakeDimension}.");
			}
			return reset;
		}

		public void RememberTargetInputLocks(uint lockComponentData)
		{
			lock (this)
			{
				if (phase != Phase.COMPLETE)
				{
					inputState.RememberTarget(lockComponentData);
				}
			}
		}

		public bool HandleDimensionChangeSuccess(ProxyConnection connection)
		{
			lock (this)
			{
				if (phase == Phase.AWAITING_FIRST_ACK)
				{
					if (secondDimensionChangeRequired)
					{
						phase = Phase.AWAITING_SECOND_ACK;
						InjectPosition(connection, Add(targetPosition, -2000, 0, -2000));
						InjectDimensionChange(connection, targetDimension, targetPosition, true);
						ScheduleAckFallback(connection);
						if (connection.IsPacketTraceActive())
						{
							Logger.Info(
								$"Backend switch reset phase 1 complete for {backendName}; sent target dimension {targetDimension}.");
						}
						return true;
					}
					CompleteLocked(connection);
					return true;
				}
				if (phase == Phase.AWAITING_SECOND_ACK)
				{
					CompleteLocked(connection);
					return true;
				}
				return false;
			}
		}

		public bool HandleLoadingScreen(ProxyConnection connection, ServerboundLoadingScreenPacket packet)
		{
			// 1.26.40 clients report target-world readiness with loading-screen type 4
			// ("screen closed" — newer than cloudburst's UNKNOWN/START/END trio), so anything
			// that is not the explicit START counts as a completion ack.
			if (packet.LoadingScreenPacketType == global::Protocol.ServerboundLoadingScreenPacketType.StartLoadingScreen)
			{
				return true;
			}
			if (connection.IsPacketTraceActive())
			{
				string id = packet.LoadingScreenId != null && packet.LoadingScreenId.HasValue
					? packet.LoadingScreenId.Value.ToString()
					: "none";
				Logger.Info(
					$"Treating loading-screen end as backend switch reset ack for {backendName}: id={id} phase={phase}.");
			}
			return HandleDimensionChangeSuccess(connection);
		}

		public bool HandleTargetWorldRequest(ProxyConnection connection, int dimension)
		{
			lock (this)
			{
				bool waitingForTargetDimension = phase == Phase.AWAITING_SECOND_ACK
					|| (!secondDimensionChangeRequired && phase == Phase.AWAITING_FIRST_ACK);
				if (!waitingForTargetDimension || dimension != targetDimension)
				{
					return false;
				}
				if (connection.IsPacketTraceActive())
				{
					Logger.Info(
						$"Treating target-world subchunk request as backend switch reset ack for {backendName}: dimension={dimension}.");
				}
				CompleteLocked(connection);
				return true;
			}
		}

		public bool IsActive()
		{
			lock (this)
			{
				return phase != Phase.COMPLETE;
			}
		}

		/// <summary>
		/// Drops this reset without completing it, for when the backend it is driving dies mid-switch.
		/// </summary>
		public void Abandon(ProxyConnection connection)
		{
			lock (this)
			{
				if (phase == Phase.COMPLETE)
				{
					return;
				}
				phase = Phase.COMPLETE;
				DisposeAckFallbackTimers();
			}
			connection.ClearBackendSwitchReset(this);
			// The world these chunks belong to is gone with the backend that sent them; replaying them
			// at whatever the player lands on next would paint the wrong terrain, so drop them.
			connection.ReleaseDeferredSwitchWorldState();
			Logger.Info($"Abandoned backend switch reset for {backendName}; that backend is gone.");
		}

		private void CompleteLocked(ProxyConnection connection)
		{
			if (phase == Phase.COMPLETE)
			{
				return;
			}
			phase = Phase.COMPLETE;
			DisposeAckFallbackTimers();
			connection.ClearBackendSwitchReset(this);
			connection.SetPlayerDimensionId(targetDimension);

			var stopSound = new StopSoundPacket();
			stopSound.SoundName = "portal.travel";
			stopSound.StopAllSounds = true;
			stopSound.StopMusicLegacy = false;
			connection.Client().SendPacket(stopSound);

			InjectPosition(connection, targetPosition);

			ReplayDeferredPlayerState(connection);
			ReplayDeferredWorldState(connection);

			// Restore what the target backend requested (normally zero). Sending the zero packet is
			// intentional even when neither backend advertised a mask: it forces the client to discard
			// stale locks left by a form or by the source backend, just as a real reconnect would.
			connection.Client().SendPacket(inputState.RestoreTarget(targetPosition));

			var chunkRadius = new RequestChunkRadiusPacket();
			chunkRadius.ChunkRadius = connection.LastRequestedChunkRadius();
			chunkRadius.MaxChunkRadius = (byte)Math.Clamp(connection.LastRequestedMaxChunkRadius(), 0, byte.MaxValue);
			backend.SendPacket(chunkRadius);

			// This build only ever speaks to protocol 2168 backends, which drive the loading-screen
			// flow themselves and never send a post-switch SERVER_READY, so initialize immediately:
			// waiting would cost the full ack-fallback window every time.
			var initialized = new SetLocalPlayerAsInitializedPacket();
			initialized.PlayerID = new ActorRuntimeID { Value = unchecked((ulong)backendRuntimeEntityId) };
			backend.SendPacket(initialized);
			if (connection.IsPacketTraceActive())
			{
				Logger.Info(
					$"Initialized player immediately after switch to {backendName}: backend protocol {connection.SessionProfile.BackendCodec.ProtocolVersion} drives its own respawn and sends no post-switch SERVER_READY to wait for.");
				// Java printed this same line after both branches; its wording predates the modern
				// immediate-initialize path and is kept for log parity.
				Logger.Info(
					$"Completed backend switch reset for {backendName}: deferred player initialization until backend SERVER_READY runtimeEntityId={backendRuntimeEntityId} chunkRadius={chunkRadius.ChunkRadius} maxRadius={chunkRadius.MaxChunkRadius}.");
			}
		}

		private void ReplayDeferredPlayerState(ProxyConnection connection)
		{
			List<IPacket> deferred = connection.DrainDeferredSwitchPlayerState();
			if (deferred.Count == 0)
			{
				return;
			}
			foreach (IPacket statePacket in deferred)
			{
				connection.Client().SendPacket(statePacket);
			}
			if (connection.IsPacketTraceActive())
			{
				Logger.Info(
					$"Replayed {deferred.Count} deferred local-player state packet(s) for {backendName} after switch reset completed.");
			}
		}

		/// <summary>
		/// Replays the chunks, sub-chunks and block edits the new backend streamed while the client was
		/// being bounced through the fake dimension. Order is preserved so each publisher update still
		/// precedes the chunks it scopes, and the real chunk data lands after - and therefore overwrites -
		/// the empty chunks seeded around the target position.
		/// </summary>
		private void ReplayDeferredWorldState(ProxyConnection connection)
		{
			List<IPacket> deferred = connection.DrainDeferredSwitchWorldState();
			if (deferred.Count == 0)
			{
				return;
			}
			int replayed = 0;
			foreach (IPacket worldPacket in deferred)
			{
				if (!connection.Client().IsConnected)
				{
					continue;
				}
				connection.Client().SendPacket(worldPacket);
				replayed++;
			}
			if (connection.IsPacketTraceActive())
			{
				Logger.Info(
					$"Replayed {replayed} of {deferred.Count} deferred world-state packet(s) for {backendName} after switch reset completed.");
			}
		}

		private void InjectPosition(ProxyConnection connection, Vec3 position)
		{
			MovePlayerPacket move = new MovePlayerPacket();
			move.PlayerRuntimeID = new ActorRuntimeID { Value = unchecked((ulong)clientRuntimeEntityId) };
			move.Position = position;
			move.Rotation = new Vec2 { X = targetRotation.X, Y = targetRotation.Y };
			move.YHeadRotation = targetRotation.Y;
			move.PositionMode = global::Protocol.PlayerPositionModeComponent.PositionMode.Respawn;
			move.OnGround = false;
			move.RidingRuntimeID = new ActorRuntimeID { Value = 0 };
			// Java's tick is a primitive defaulting to 0; this codec models it as a reference type that
			// Write dereferences unconditionally. Leaving it unset threw mid-encode and the send path
			// swallowed the whole batch - every injected respawn teleport (start, phase 2 and completion)
			// never reached the client, which stranded it at its old coordinates in the new dimension.
			move.Tick = new global::Protocol.Types.PlayerInputTick();
			connection.Client().SendPacket(move);
		}

		private void InjectDimensionChange(ProxyConnection connection, int dimension, Vec3 position, bool chunks)
		{
			var change = new ChangeDimensionPacket();
			change.DimensionID = new DimensionType { Value = dimension };
			change.Position = position;
			change.Respawn = true;
			change.LoadingScreenId = new Optional<uint>((uint)Interlocked.Increment(ref loadingScreenIds));
			connection.Client().SendPacket(change);

			if (chunks)
			{
				InjectChunkPublisherUpdate(connection, position);
				InjectEmptyChunks(connection, position, dimension);
			}

			PlayerActionPacket action = new PlayerActionPacket();
			action.PlayerRuntimeID = new ActorRuntimeID { Value = unchecked((ulong)clientRuntimeEntityId) };
			action.Action = PlayerActionType.ChangeDimensionAck;
			action.BlockPosition = new BlockPos();
			action.ResultPos = new BlockPos();
			action.Face = 0;
			connection.Client().SendPacket(action);
		}

		private void ScheduleAckFallback(ProxyConnection connection)
		{
			var timer = new Timer(_ =>
			{
				bool active = IsActive();
				if (!active)
				{
					return;
				}
				Logger.Info($"WARNING: Backend switch reset ack fallback for {backendName} (phase={phase}).");
				HandleDimensionChangeSuccess(connection);
			}, null, ACK_FALLBACK_MILLIS, Timeout.Infinite);
			// An unreferenced System.Threading.Timer is garbage-collectible and silently never fires,
			// which strands the reset forever. Hold the reference until the reset finishes.
			lock (this)
			{
				ackFallbackTimers.Add(timer);
			}
		}

		private void DisposeAckFallbackTimers()
		{
			lock (this)
			{
				foreach (Timer timer in ackFallbackTimers)
				{
					timer.Dispose();
				}
				ackFallbackTimers.Clear();
			}
		}

		private static void InjectChunkPublisherUpdate(ProxyConnection connection, Vec3 position)
		{
			var update = new NetworkChunkPublisherUpdatePacket();
			update.NewPositionForView = ToBlockPosition(position);
			update.NewRadiusForView = RESET_CHUNK_RADIUS;
			connection.Client().SendPacket(update);
		}

		private static void InjectEmptyChunks(ProxyConnection connection, Vec3 position, int dimension)
		{
			int chunkX = Floor(position.X) >> 4;
			int chunkZ = Floor(position.Z) >> 4;
			byte[] empty = EmptyChunkData(dimension);
			for (int x = -RESET_CHUNK_RADIUS; x <= RESET_CHUNK_RADIUS; x++)
			{
				for (int z = -RESET_CHUNK_RADIUS; z <= RESET_CHUNK_RADIUS; z++)
				{
					var chunk = new LevelChunkPacket();
					chunk.ChunkPosition = new ChunkPos { X = chunkX + x, Z = chunkZ + z };
					chunk.DimensionId = new DimensionType { Value = dimension };
					chunk.SubChunksCount = 1;
					chunk.CacheEnabled = false;
					chunk.SerializedChunkData = empty;
					connection.Client().SendPacket(chunk);
				}
			}
		}

		private static byte[] EmptyChunkData(int dimension)
		{
			return CreateChunkData(dimension switch
			{
				DIMENSION_NETHER => 8,
				DIMENSION_END => 16,
				_ => 24
			});
		}

		/// <summary>
		/// A minimal serialized chunk payload: one sub-chunk of air plus biome sections, enough for the
		/// client to render ground under its feet during the fake-dimension hop.
		/// </summary>
		private static byte[] CreateChunkData(int biomeSections)
		{
			using var ms = new MemoryStream();
			ms.WriteByte(8);   // sub-chunk version
			ms.WriteByte(0);   // storage count for block layers? format header
			WritePalette(ms, 0);
			for (int i = 1; i < biomeSections; i++)
			{
				ms.WriteByte((byte)((127 << 1) | 1));
			}
			ms.WriteByte(0);
			return ms.ToArray();
		}

		private static void WritePalette(MemoryStream buffer, int runtimeId)
		{
			buffer.WriteByte((byte)((1 << 1) | 1));
			for (int i = 0; i < 512; i++)
			{
				buffer.WriteByte(0);
			}
			VarInt.WriteSInt32(buffer, 1);
			VarInt.WriteSInt32(buffer, runtimeId);
		}

		private static int AlternateDimension(int dimension)
		{
			return dimension == DIMENSION_OVERWORLD ? DIMENSION_END : DIMENSION_OVERWORLD;
		}

		private static BlockPos ToBlockPosition(Vec3 position)
		{
			return new BlockPos { X = Floor(position.X), Y = Floor(position.Y), Z = Floor(position.Z) };
		}

		private static int Floor(float value)
		{
			return (int)Math.Floor(value);
		}

		private static Vec3 Zero() => new Vec3();

		private static Vec3 Add(Vec3 v, float dx, float dy, float dz)
		{
			return new Vec3 { X = v.X + dx, Y = v.Y + dy, Z = v.Z + dz };
		}
	}
}
