using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Threading;
using EnderPearl.Auth;
using EnderPearl.Config;
using EnderPearl.Crypto;
using EnderPearl.Listener;
using EnderPearl.Palette;
using EnderPearl.Resource;
using EnderPearl.Session;
using global::Protocol.Packets;
using global::Protocol.Types;
using EnderPearl.Logging;

namespace EnderPearl.Backend
{
	/// <summary>
	/// Everything the proxy knows about one player, and every piece of cross-leg state the relay and
	/// the switch machinery negotiate over: runtime/unique entity id maps, deferred switch buffers,
	/// join/failover/switch locks, packet tracing windows.
	/// </summary>
	public sealed class ProxyConnection
	{
		// Packet tracing is an opt-in diagnostic: ENDERPEARL_LOG_PACKETS=1 for continuous,
		// ENDERPEARL_TRACE_MILLIS=<ms> for an event-triggered window.
		private static readonly bool LOG_PACKETS =
			Environment.GetEnvironmentVariable("ENDERPEARL_LOG_PACKETS") == "1";
		private static readonly int CONFIGURED_PACKET_TRACE_MILLIS =
			int.TryParse(Environment.GetEnvironmentVariable("ENDERPEARL_TRACE_MILLIS"), out int traceMillis)
				? Math.Max(0, traceMillis)
				: 0;
		private const long FAILOVER_EPISODE_WINDOW_MILLIS = 30_000;
		private const int MAX_FAILOVERS_PER_EPISODE = 3;
		/// <summary>Comfortably longer than a full /server retry sequence.</summary>
		private const long SWITCH_LOCK_MAX_MILLIS = 120_000;
		/// <summary>
		/// Cap on world-state packets held across a switch reset. Sized to cover a full 32-chunk view
		/// (4225 columns) plus the publisher updates and block edits interleaved with them.
		/// </summary>
		private const int MAX_DEFERRED_SWITCH_WORLD_STATE = 8192;

		private readonly object mutex = new();
		private readonly ListenerSession client;
		// volatile: read several times per relayed packet from the client and backend read threads;
		// a plain field would need the mutex on every access and put that lock on the hot path.
		private volatile ProxySessionProfile sessionProfile;
		private readonly ClientLogin clientLogin;
		private readonly ECDsaHolder keyPair;
		private LoginPacket backendLogin;
		private readonly ProxyResourcePackRegistry proxyResourcePackRegistry;
		private readonly BackendPackCache backendPackCache;
		private readonly CrossBackendPalette crossBackendPalette;
		private bool? clientBlockIdsHashed;
		private readonly ClientWorldState clientWorldState = new();
		private BackendSession? backend;
		private string? backendName;
		private BackendSession? pendingBackend;
		private bool backendSwitchInProgress;
		private string? backendSwitchTarget;
		private long backendSwitchStartedAtMillis;
		private bool failoverInProgress;
		private long lastFailoverStartedAtMillis;
		private long lastProxyCommandAtMillis = long.MinValue / 2;
		private bool joinSequenceActive;
		private List<BackendConfig> remainingJoinCandidates = new();
		private long joinAttemptId;
		private long lastHandledJoinAttemptId = -1;
		private int failoversInEpisode;
		private bool clientJoinedWorld;
		private int lastRequestedChunkRadius = 12;
		private int lastRequestedMaxChunkRadius = 12;
		private long backendPlayerRuntimeEntityId;
		private long clientPlayerRuntimeEntityId;
		private long backendPlayerUniqueEntityId;
		private long clientPlayerUniqueEntityId;
		private long nextSyntheticClientRuntimeEntityId = 1_000_000_000L;
		private readonly Dictionary<long, long> backendToClientRuntimeIds = new();
		private readonly Dictionary<long, long> clientToBackendRuntimeIds = new();
		private readonly List<IPacket> deferredSwitchPlayerState = new();
		private readonly List<IPacket> deferredSwitchWorldState = new();
		private bool deferredSwitchWorldStateOverflowed;
		private int playerDimensionId;
		private BackendSwitchReset? backendSwitchReset;
		private long packetTraceUntilNanos;
		private readonly long createdAtNanos = NanoTime();
		private long clientboundTraceSequence;
		private long serverboundTraceSequence;

		public ProxyConnection(
			ListenerSession client,
			ProxySessionProfile sessionProfile,
			ClientLogin clientLogin,
			ECDsaHolder keyPair,
			LoginPacket backendLogin,
			ProxyResourcePackRegistry proxyResourcePackRegistry
		)
			: this(client, sessionProfile, clientLogin, keyPair, backendLogin, proxyResourcePackRegistry, null)
		{
		}

		public ProxyConnection(
			ListenerSession client,
			ProxySessionProfile sessionProfile,
			ClientLogin clientLogin,
			ECDsaHolder keyPair,
			LoginPacket backendLogin,
			ProxyResourcePackRegistry proxyResourcePackRegistry,
			BackendPaletteStore? backendPaletteStore
		)
			: this(client, sessionProfile, clientLogin, keyPair, backendLogin, proxyResourcePackRegistry,
				backendPaletteStore, null)
		{
		}

		public ProxyConnection(
			ListenerSession client,
			ProxySessionProfile sessionProfile,
			ClientLogin clientLogin,
			ECDsaHolder keyPair,
			LoginPacket backendLogin,
			ProxyResourcePackRegistry proxyResourcePackRegistry,
			BackendPaletteStore? backendPaletteStore,
			BackendPackCache? backendPackCache
		)
		{
			this.client = client ?? throw new ArgumentNullException(nameof(client));
			this.sessionProfile = sessionProfile;
			this.clientLogin = clientLogin;
			this.keyPair = keyPair;
			this.backendLogin = backendLogin;
			this.proxyResourcePackRegistry = proxyResourcePackRegistry ?? ProxyResourcePackRegistry.Empty();
			this.crossBackendPalette = new CrossBackendPalette(backendPaletteStore);
			this.backendPackCache = backendPackCache ?? BackendPackCache.Disabled();
		}

		public ListenerSession Client() => client;

		// Effects the client currently displays, spanning backend switches: a potion drunk on one
		// backend is unknown to every other backend, whose join burst never removes it - without
		// tracking, the HUD keeps showing stale icons after every switch and appears to stack.
		private readonly HashSet<int> activeClientEffects = new();

		public void TrackClientEffect(int effectId, bool removed)
		{
			lock (mutex)
			{
				if (removed)
				{
					activeClientEffects.Remove(effectId);
				}
				else
				{
					activeClientEffects.Add(effectId);
				}
			}
		}

		/// <summary>Returns and clears the effects to remove client-side before a backend handoff.</summary>
		public int[] TakeActiveClientEffects()
		{
			lock (mutex)
			{
				int[] ids = activeClientEffects.ToArray();
				activeClientEffects.Clear();
				return ids;
			}
		}

		public ProxySessionProfile SessionProfile => sessionProfile!;

		public void SetSessionProfile(ProxySessionProfile profile)
		{
			if (profile == null)
			{
				throw new ArgumentNullException(nameof(profile));
			}
			// Volatile write: atomic reference swap, no mutex - the relay reads this per packet and
			// must not contend with every other state change the mutex protects.
			sessionProfile = profile;
			client.SessionProfile = profile;
		}

		public ClientLogin ClientLogin => clientLogin;

		/// <summary>
		/// The player's address as anything outside this process should see it.
		///
		/// <p>For a Bedrock player that is simply their socket address. For a bridged player it is the
		/// address the bridge stamped into their login.</p>
		/// </summary>
		public IPEndPoint ClientAddress()
		{
			IPEndPoint? bridgeAddress = clientLogin?.BridgeClientAddress;
			return bridgeAddress ?? client.RemoteEndPoint!;
		}

		public ECDsaHolder KeyPair => keyPair;

		public LoginPacket BackendLogin
		{
			get
			{
				lock (mutex)
				{
					return backendLogin;
				}
			}
		}

		public void SetBackendLogin(LoginPacket login)
		{
			if (login == null)
			{
				throw new ArgumentNullException(nameof(login));
			}
			lock (mutex)
			{
				backendLogin = login;
			}
		}

		public ProxyResourcePackRegistry ProxyResourcePackRegistry => proxyResourcePackRegistry;

		/// <summary>
		/// Where backend packs seen on this connection are kept. Shared by every connection: a pack
		/// learned from one player is served to all of them.
		/// </summary>
		public BackendPackCache BackendPackCache => backendPackCache;

		/// <summary>
		/// This player's cross-backend item and entity registries. Decided at login and unchangeable
		/// afterwards, because that is when Bedrock reads them; see <see cref="CrossBackendPalette"/>.
		/// </summary>
		public CrossBackendPalette CrossBackendPalette => crossBackendPalette;

		/// <summary>
		/// Whether this client reads block ids as hashes, fixed by the StartGame it logged in with.
		///
		/// <para>Null until the first StartGame reaches the client. Like the registries above this cannot
		/// change afterwards, which is why a backend on the other scheme has to be reached by a reconnect
		/// rather than a handoff.</para>
		/// </summary>
		public bool? ClientBlockIdsHashed()
		{
			lock (mutex)
			{
				return clientBlockIdsHashed;
			}
		}

		/// <summary>Recorded once, from the first StartGame forwarded to the client; later ones cannot change it.</summary>
		public void RememberClientBlockIdsHashed(bool hashed)
		{
			lock (mutex)
			{
				if (clientBlockIdsHashed == null)
				{
					clientBlockIdsHashed = hashed;
				}
			}
		}

		public BackendSession? Backend()
		{
			lock (mutex)
			{
				return backend;
			}
		}

		public string? BackendName()
		{
			lock (mutex)
			{
				return backendName;
			}
		}

		public BackendSession? PendingBackend()
		{
			lock (mutex)
			{
				return pendingBackend;
			}
		}

		public bool IsSwitchingBackend()
		{
			lock (mutex)
			{
				return backendSwitchInProgress;
			}
		}

		public void SetBackend(string name, BackendSession? newBackend)
		{
			if (string.IsNullOrWhiteSpace(name))
			{
				throw new ArgumentException("backendName cannot be blank");
			}
			List<IPacket> releasedWorldState;
			lock (mutex)
			{
				backendName = name;
				backend = newBackend;
				backendSwitchInProgress = false;
				backendSwitchTarget = null;
				deferredSwitchPlayerState.Clear();
				releasedWorldState = new List<IPacket>(deferredSwitchWorldState);
				deferredSwitchWorldState.Clear();
				deferredSwitchWorldStateOverflowed = false;
				if (newBackend != null)
				{
					newBackend.SetDisconnectClientOnClose(true);
				}
			}
			ReleaseDeferred(releasedWorldState);
		}

		public BackendSession? ReplaceBackend(string name, BackendSession newBackend)
		{
			BackendSession previous;
			lock (mutex)
			{
				previous = backend!;
				SetBackendLocked(name, newBackend);
				if (pendingBackend == newBackend)
				{
					pendingBackend = null;
				}
			}
			if (previous != null && previous != newBackend)
			{
				previous.SetDisconnectClientOnClose(false);
				previous.DiscardInboundPackets();
			}
			return previous;
		}

		// setBackend without the outer re-lock; caller holds mutex.
		private void SetBackendLocked(string name, BackendSession? newBackend)
		{
			backendName = name;
			backend = newBackend;
			backendSwitchInProgress = false;
			backendSwitchTarget = null;
			deferredSwitchPlayerState.Clear();
			foreach (IPacket packet in deferredSwitchWorldState)
			{
				Release(packet);
			}
			deferredSwitchWorldState.Clear();
			deferredSwitchWorldStateOverflowed = false;
			if (newBackend != null)
			{
				newBackend.SetDisconnectClientOnClose(true);
			}
		}

		public void SetPendingBackend(BackendSession newBackend)
		{
			lock (mutex)
			{
				pendingBackend = newBackend;
			}
		}

		public void ClearPendingBackend(BackendSession newBackend)
		{
			lock (mutex)
			{
				if (pendingBackend == newBackend)
				{
					pendingBackend = null;
				}
			}
		}

		/// <summary>
		/// Claims the right to move this player to another backend.
		///
		/// <p>The lock is released by whoever took it, but a stuck one strands the player on "already
		/// connecting" with no way out short of reconnecting. SWITCH_LOCK_MAX_MILLIS bounds that.</p>
		/// </summary>
		public SwitchStart BeginBackendSwitch(string name)
		{
			lock (mutex)
			{
				if (backendSwitchInProgress)
				{
					long heldForMillis = CurrentTimeMillis() - backendSwitchStartedAtMillis;
					if (heldForMillis < SWITCH_LOCK_MAX_MILLIS)
					{
						return SwitchStart.ALREADY_SWITCHING;
					}
					Logger.Info(
						$"Backend switch to {backendSwitchTarget} has been in progress for {heldForMillis}ms with no outcome; taking the switch over for {name}.");
				}
				backendSwitchInProgress = true;
				backendSwitchTarget = name;
				backendSwitchStartedAtMillis = CurrentTimeMillis();
				return SwitchStart.STARTED;
			}
		}

		public enum SwitchStart
		{
			STARTED,
			ALREADY_SWITCHING
		}

		public void FinishBackendSwitch()
		{
			lock (mutex)
			{
				backendSwitchInProgress = false;
				backendSwitchTarget = null;
			}
		}

		public string? BackendSwitchTarget()
		{
			lock (mutex)
			{
				return backendSwitchTarget;
			}
		}

		/// <summary>
		/// Marks the start of a failover: the backend the player was on has gone away and the proxy is
		/// walking the fallback chain looking for one that will take them.
		///
		/// <p>Hops that keep happening inside FAILOVER_EPISODE_WINDOW_MILLIS are counted as one episode
		/// and capped.</p>
		/// </summary>
		public FailoverStart BeginFailover()
		{
			lock (mutex)
			{
				if (failoverInProgress)
				{
					return FailoverStart.ALREADY_RUNNING;
				}
				long now = CurrentTimeMillis();
				failoversInEpisode = now - lastFailoverStartedAtMillis <= FAILOVER_EPISODE_WINDOW_MILLIS
					? failoversInEpisode + 1
					: 1;
				lastFailoverStartedAtMillis = now;
				if (failoversInEpisode > MAX_FAILOVERS_PER_EPISODE)
				{
					return FailoverStart.TOO_MANY;
				}
				failoverInProgress = true;
				return FailoverStart.STARTED;
			}
		}

		public enum FailoverStart
		{
			STARTED,
			ALREADY_RUNNING,
			TOO_MANY
		}

		public void FinishFailover()
		{
			lock (mutex)
			{
				failoverInProgress = false;
			}
		}

		public bool IsFailingOver()
		{
			lock (mutex)
			{
				return failoverInProgress;
			}
		}

		/// <summary>
		/// Starts the join sequence: the ordered backends to try before giving up on a player who has
		/// not reached a world yet.
		/// </summary>
		public void BeginJoinSequence(List<BackendConfig> candidates)
		{
			lock (mutex)
			{
				joinSequenceActive = true;
				remainingJoinCandidates = new List<BackendConfig>(candidates);
			}
		}

		public bool IsJoinSequenceActive()
		{
			lock (mutex)
			{
				return joinSequenceActive;
			}
		}

		public void EndJoinSequence()
		{
			lock (mutex)
			{
				joinSequenceActive = false;
				remainingJoinCandidates.Clear();
			}
		}

		public BackendConfig? NextJoinCandidate()
		{
			lock (mutex)
			{
				if (remainingJoinCandidates.Count == 0)
				{
					return null;
				}
				BackendConfig candidate = remainingJoinCandidates[0];
				remainingJoinCandidates.RemoveAt(0);
				return candidate;
			}
		}

		/// <summary>Numbers the current attempt, so a failure of an earlier one cannot end a later one.</summary>
		public void BeginJoinAttempt()
		{
			lock (mutex)
			{
				joinAttemptId++;
			}
		}

		/// <summary>
		/// Claims the right to react to the current attempt's failure. One dead backend surfaces on
		/// several paths at once; the first caller acts, the rest are told the failure is already handled.
		/// </summary>
		public bool ClaimJoinFailure()
		{
			lock (mutex)
			{
				if (lastHandledJoinAttemptId == joinAttemptId)
				{
					return false;
				}
				lastHandledJoinAttemptId = joinAttemptId;
				return true;
			}
		}

		/// <summary>
		/// Claims this player's proxy-command slot, refusing if they used one less than
		/// cooldownMillis ago.
		/// </summary>
		public bool ClaimProxyCommandSlot(long cooldownMillis)
		{
			if (cooldownMillis <= 0)
			{
				return true;
			}
			lock (mutex)
			{
				long now = CurrentTimeMillis();
				// A clock that moved backwards must not lock the player out until it catches up.
				if (now >= lastProxyCommandAtMillis && now - lastProxyCommandAtMillis < cooldownMillis)
				{
					return false;
				}
				lastProxyCommandAtMillis = now;
				return true;
			}
		}

		/// <summary>
		/// Records that the client has been handed a StartGame and is in a world. Deliberately not reset
		/// by SetBackend: once a client is in a world it stays in one across every subsequent switch.
		/// </summary>
		public void MarkClientJoinedWorld()
		{
			lock (mutex)
			{
				clientJoinedWorld = true;
				joinSequenceActive = false;
				remainingJoinCandidates.Clear();
			}
		}

		public bool HasClientJoinedWorld()
		{
			lock (mutex)
			{
				return clientJoinedWorld;
			}
		}

		public void RememberChunkRadius(int radius, int maxRadius)
		{
			lock (mutex)
			{
				if (radius > 0)
				{
					lastRequestedChunkRadius = radius;
				}
				if (maxRadius > 0)
				{
					lastRequestedMaxChunkRadius = maxRadius;
				}
			}
		}

		public int LastRequestedChunkRadius()
		{
			lock (mutex)
			{
				return lastRequestedChunkRadius;
			}
		}

		public int LastRequestedMaxChunkRadius()
		{
			lock (mutex)
			{
				return lastRequestedMaxChunkRadius;
			}
		}

		public void SetBackendPlayerRuntimeEntityId(long runtimeEntityId)
		{
			lock (mutex)
			{
				backendPlayerRuntimeEntityId = runtimeEntityId;
				if (clientPlayerRuntimeEntityId <= 0)
				{
					clientPlayerRuntimeEntityId = runtimeEntityId;
				}
				backendToClientRuntimeIds.Clear();
				clientToBackendRuntimeIds.Clear();
				RegisterRuntimeMappingLocked(runtimeEntityId, clientPlayerRuntimeEntityId);
			}
		}

		public long BackendPlayerRuntimeEntityId()
		{
			lock (mutex)
			{
				return backendPlayerRuntimeEntityId;
			}
		}

		/// <summary>
		/// The client keeps the identity it was given by its first StartGame for the whole proxy session,
		/// but every backend assigns the player its own unique entity id. Only the local player's id is
		/// remapped. Unique ids are signed and routinely negative, so 0 is the "not yet known" sentinel.
		/// </summary>
		public void SetBackendPlayerUniqueEntityId(long uniqueEntityId)
		{
			lock (mutex)
			{
				backendPlayerUniqueEntityId = uniqueEntityId;
				if (clientPlayerUniqueEntityId == 0)
				{
					clientPlayerUniqueEntityId = uniqueEntityId;
				}
			}
		}

		public long BackendPlayerUniqueEntityId()
		{
			lock (mutex)
			{
				return backendPlayerUniqueEntityId;
			}
		}

		public long ClientPlayerUniqueEntityId()
		{
			lock (mutex)
			{
				return clientPlayerUniqueEntityId == 0 ? backendPlayerUniqueEntityId : clientPlayerUniqueEntityId;
			}
		}

		public long ToClientUniqueEntityId(long backendUniqueEntityId)
		{
			lock (mutex)
			{
				return backendPlayerUniqueEntityId != 0 && backendUniqueEntityId == backendPlayerUniqueEntityId
					? (clientPlayerUniqueEntityId == 0 ? backendPlayerUniqueEntityId : clientPlayerUniqueEntityId)
					: backendUniqueEntityId;
			}
		}

		/// <summary>
		/// Java PlayerRewriteUtils.rewriteId applied to the local player's unique-id pair: swaps
		/// backend-id to client-id and back, leaving every other value untouched, so running the same
		/// value through twice restores the original. ActorLink endpoints carry unique ids, so unlike
		/// runtime ids they get exactly this plain swap - no table lookup, and nothing is registered.
		/// </summary>
		public long SwapClientUniqueEntityId(long value)
		{
			lock (mutex)
			{
				// 0 marks "not yet known" on both sides; until the pair exists there is nothing to swap.
				if (value == 0 || backendPlayerUniqueEntityId == 0)
				{
					return value;
				}
				long clientUniqueId = clientPlayerUniqueEntityId == 0 ? backendPlayerUniqueEntityId : clientPlayerUniqueEntityId;
				return value == backendPlayerUniqueEntityId ? clientUniqueId
					: value == clientUniqueId ? backendPlayerUniqueEntityId : value;
			}
		}

		public long ClientPlayerRuntimeEntityId()
		{
			lock (mutex)
			{
				return clientPlayerRuntimeEntityId <= 0 ? backendPlayerRuntimeEntityId : clientPlayerRuntimeEntityId;
			}
		}

		public long ToClientRuntimeEntityId(long backendRuntimeEntityId, bool registerEntity)
		{
			if (backendRuntimeEntityId <= 0)
			{
				return backendRuntimeEntityId;
			}
			lock (mutex)
			{
				if (backendToClientRuntimeIds.TryGetValue(backendRuntimeEntityId, out long existing))
				{
					return existing;
				}
				if (!registerEntity)
				{
					return backendRuntimeEntityId;
				}
				long clientRuntimeEntityId = backendRuntimeEntityId;
				if (clientRuntimeEntityId == clientPlayerRuntimeEntityId
					|| clientToBackendRuntimeIds.ContainsKey(clientRuntimeEntityId))
				{
					do
					{
						clientRuntimeEntityId = nextSyntheticClientRuntimeEntityId++;
					} while (clientToBackendRuntimeIds.ContainsKey(clientRuntimeEntityId)
						|| clientRuntimeEntityId == clientPlayerRuntimeEntityId);
				}
				RegisterRuntimeMappingLocked(backendRuntimeEntityId, clientRuntimeEntityId);
				return clientRuntimeEntityId;
			}
		}

		public bool HasBackendRuntimeEntityId(long backendRuntimeEntityId)
		{
			if (backendRuntimeEntityId <= 0)
			{
				return false;
			}
			lock (mutex)
			{
				return backendToClientRuntimeIds.ContainsKey(backendRuntimeEntityId);
			}
		}

		public long ToBackendRuntimeEntityId(long clientRuntimeEntityId)
		{
			if (clientRuntimeEntityId <= 0)
			{
				return clientRuntimeEntityId;
			}
			lock (mutex)
			{
				return clientToBackendRuntimeIds.GetValueOrDefault(clientRuntimeEntityId, clientRuntimeEntityId);
			}
		}

		private void RegisterRuntimeMappingLocked(long backendRuntimeEntityId, long clientRuntimeEntityId)
		{
			if (backendRuntimeEntityId <= 0 || clientRuntimeEntityId <= 0)
			{
				return;
			}
			backendToClientRuntimeIds[backendRuntimeEntityId] = clientRuntimeEntityId;
			clientToBackendRuntimeIds[clientRuntimeEntityId] = backendRuntimeEntityId;
		}

		public void SetPlayerDimensionId(int dimensionId)
		{
			lock (mutex)
			{
				playerDimensionId = dimensionId;
			}
		}

		public int PlayerDimensionId()
		{
			lock (mutex)
			{
				return playerDimensionId;
			}
		}

		public void SetBackendSwitchReset(BackendSwitchReset reset)
		{
			lock (mutex)
			{
				backendSwitchReset = reset;
			}
		}

		public BackendSwitchReset? BackendSwitchResetRef()
		{
			lock (mutex)
			{
				return backendSwitchReset;
			}
		}

		public void ClearBackendSwitchReset(BackendSwitchReset reset)
		{
			lock (mutex)
			{
				if (backendSwitchReset == reset)
				{
					backendSwitchReset = null;
				}
			}
		}

		public ClientWorldState ClientWorldState => clientWorldState;

		/// <summary>
		/// Records a client-ready (already translated) packet carrying local-player state which the
		/// backend only emits once during its join burst. Replayed once the switch reset completes.
		/// </summary>
		public void AddDeferredSwitchPlayerState(IPacket packet)
		{
			if (packet == null)
			{
				return;
			}
			lock (mutex)
			{
				deferredSwitchPlayerState.Add(packet);
			}
		}

		public List<IPacket> DrainDeferredSwitchPlayerState()
		{
			lock (mutex)
			{
				if (deferredSwitchPlayerState.Count == 0)
				{
					return new List<IPacket>();
				}
				List<IPacket> drained = new(deferredSwitchPlayerState);
				deferredSwitchPlayerState.Clear();
				return drained;
			}
		}

		/// <summary>
		/// Records a client-ready packet that carries world geometry - chunks, sub-chunks, block updates
		/// and the publisher updates that scope them - buffered during a switch instead of dropped, then
		/// replayed once the client is back in the target dimension. Bounded by
		/// MAX_DEFERRED_SWITCH_WORLD_STATE; past the cap we fall back to dropping.
		/// </summary>
		public bool AddDeferredSwitchWorldState(IPacket packet)
		{
			if (packet == null)
			{
				return false;
			}
			lock (mutex)
			{
				if (deferredSwitchWorldState.Count >= MAX_DEFERRED_SWITCH_WORLD_STATE)
				{
					if (!deferredSwitchWorldStateOverflowed)
					{
						deferredSwitchWorldStateOverflowed = true;
						Logger.Info(
							$"Deferred switch world-state buffer full at {MAX_DEFERRED_SWITCH_WORLD_STATE} packets; dropping further world state until the switch reset completes.");
					}
					return false;
				}
				deferredSwitchWorldState.Add(packet);
				return true;
			}
		}

		public List<IPacket> DrainDeferredSwitchWorldState()
		{
			List<IPacket> drained;
			lock (mutex)
			{
				deferredSwitchWorldStateOverflowed = false;
				if (deferredSwitchWorldState.Count == 0)
				{
					return new List<IPacket>();
				}
				drained = new List<IPacket>(deferredSwitchWorldState);
				deferredSwitchWorldState.Clear();
			}
			return drained;
		}

		/// <summary>Drops buffered world state without sending it, when its switch was abandoned.</summary>
		public void ReleaseDeferredSwitchWorldState()
		{
			List<IPacket> released;
			lock (mutex)
			{
				released = new List<IPacket>(deferredSwitchWorldState);
				deferredSwitchWorldState.Clear();
				deferredSwitchWorldStateOverflowed = false;
			}
			ReleaseDeferred(released);
		}

		private static void ReleaseDeferred(List<IPacket> packets)
		{
			// The Java original released each packet's retained ByteBuf here. This codec builds plain
			// managed objects, so there is nothing to release; the list drop is the whole job.
		}

		private static void Release(IPacket packet)
		{
			// See ReleaseDeferred: no reference counting exists on this side of the port.
		}

		public void CloseBackend(string reason)
		{
			BackendSession? currentBackend;
			BackendSession? currentPending;
			lock (mutex)
			{
				currentBackend = backend;
				currentPending = pendingBackend;
				pendingBackend = null;
			}
			if (currentBackend != null && currentBackend.IsConnected)
			{
				currentBackend.Disconnect(reason);
			}
			if (currentPending != null && !ReferenceEquals(currentPending, currentBackend) && currentPending.IsConnected)
			{
				currentPending.SetDisconnectClientOnClose(false);
				currentPending.DiscardInboundPackets();
				currentPending.Disconnect(reason);
			}
			lock (mutex)
			{
				backendSwitchReset = null;
			}
			// A reset that never completed still owns retained chunk buffers; nobody will replay them now.
			ReleaseDeferredSwitchWorldState();
		}

		public void TracePacketsForMillis(long millis)
		{
			if (LOG_PACKETS || CONFIGURED_PACKET_TRACE_MILLIS <= 0 || millis <= 0)
			{
				return;
			}
			packetTraceUntilNanos = NanoTime() + millis * 1_000_000L;
		}

		public bool IsPacketTraceActive()
		{
			return LOG_PACKETS
				|| (CONFIGURED_PACKET_TRACE_MILLIS > 0 && NanoTime() <= Volatile.Read(ref packetTraceUntilNanos));
		}

		public static int ConfiguredPacketTraceMillis() => CONFIGURED_PACKET_TRACE_MILLIS;

		public static bool IsPacketTracingConfigured() => LOG_PACKETS || CONFIGURED_PACKET_TRACE_MILLIS > 0;

		public static bool IsContinuousPacketTracingConfigured() => LOG_PACKETS;

		public long ElapsedMillis() => (NanoTime() - createdAtNanos) / 1_000_000L;

		public long NextClientboundTraceSequence()
		{
			lock (mutex)
			{
				return ++clientboundTraceSequence;
			}
		}

		public long NextServerboundTraceSequence()
		{
			lock (mutex)
			{
				return ++serverboundTraceSequence;
			}
		}

		public long ClientboundTraceSequence()
		{
			lock (mutex)
			{
				return clientboundTraceSequence;
			}
		}

		public long ServerboundTraceSequence()
		{
			lock (mutex)
			{
				return serverboundTraceSequence;
			}
		}

		internal static long CurrentTimeMillis() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

		internal static long NanoTime()
		{
			return (long)(Stopwatch.GetTimestamp() * (double)1_000_000_000 / Stopwatch.Frequency);
		}
	}
}
