using EnderPearl.Auth;
using EnderPearl.Backend;
using EnderPearl.Config;
using EnderPearl.Core;
using EnderPearl.Frontend;
using global::Protocol.Packets;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;

namespace EnderPearl.Player;
/// <summary>
/// Everything the proxy knows about one player, and the thread-safety boundary for it: one mutex
/// guards every piece of cross-leg state the relay and the switch machinery negotiate over.
///
/// <para>This is a facade, deliberately thin. The state itself lives in four components, each an
/// ordinary class documented as "call only while holding <see cref="ProxyConnection"/>'s mutex":</para>
///
/// <list type="bullet">
/// <item><see cref="PlayerEntityIds"/> - runtime/unique entity-id identity and the rewrite table</item>
/// <item><see cref="PlayerSwitchState"/> - the switch / failover / join-sequence locks and counters</item>
/// <item><see cref="Deferred"/> - packets buffered across a switch reset</item>
/// <item><see cref="Trace"/> - packet-tracing windows and sequence numbers</item>
/// </list>
///
/// <para>Operations that span components (a backend handoff clears the switch lock and both deferred
/// buffers and swaps the backend reference in one atomic step) are orchestrated here, inside the
/// lock, so no component ever needs a second lock.</para>
/// </summary>
public sealed class ProxyConnection
{
	private readonly object mutex = new();
	private readonly ListenerSession client = null!;
	private LoginPacket backendLogin = null!;
	private bool? clientBlockIdsHashed;
	private readonly ClientWorldState clientWorldState = new();
	private BackendSession? backend;
	private string? backendName;
	private BackendSession? pendingBackend;
	private int playerDimensionId;
	private BackendSwitchReset? backendSwitchReset;
	private int lastRequestedChunkRadius = 12;
	private int lastRequestedMaxChunkRadius = 12;
	private long lastProxyCommandAtMillis = long.MinValue / 2;

	private readonly PlayerEntityIds entityIds = new();
	private readonly PlayerSwitchState switchState = new();
	private readonly DeferredSwitchState deferred = new();

	/// <summary>The client-facing leg this player speaks on.</summary>
	public required ListenerSession Client { get => client; init => client = value ?? throw new ArgumentNullException(nameof(Client)); }

	/// <summary>The verified identity (name, XUID, skin JWT) this player logged in with.</summary>
	public required ClientLogin ClientLogin { get; init; }

	/// <summary>The per-player P-384 key pair used for the backend leg's handshake and login.</summary>
	public required ECDsaHolder KeyPair { get; init; }

	/// <summary>The login packet the proxy forges toward the backend, set by the connector before use.</summary>
	public LoginPacket BackendLogin => ReadBackendLogin();

	/// <summary>Packets buffered across a switch reset, replayed once the client respawns.</summary>
	public DeferredSwitchState Deferred => deferred;

	/// <summary>Packet-tracing windows and the per-direction sequence counters.</summary>

	private LoginPacket ReadBackendLogin()
	{
		lock (mutex)
		{
			return backendLogin;
		}
	}

	/// <summary>The player's socket address.</summary>
	public IPEndPoint ClientAddress()
	{
		return client.RemoteEndPoint!;
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

	// -----------------------------------------------------------------------
	// Backend legs and handoff (spans the lock, the switch lock and both buffers)
	// -----------------------------------------------------------------------

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
			return switchState.IsSwitchInProgress();
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
			switchState.EndSwitchLocked();
			releasedWorldState = deferred.ClearForBackendSwitch();
			if (newBackend != null)
			{
				newBackend.SetDisconnectClientOnClose(true);
			}
		}
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

	// SetBackend without the outer re-lock; caller holds mutex.
	private void SetBackendLocked(string name, BackendSession? newBackend)
	{
		backendName = name;
		backend = newBackend;
		switchState.EndSwitchLocked();
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

	/// <summary>Claims the right to move this player to another backend.</summary>
	public SwitchStart BeginBackendSwitch(string name)
	{
		lock (mutex)
		{
			return switchState.BeginBackendSwitch(name);
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
			switchState.FinishBackendSwitch();
		}
	}

	public string? BackendSwitchTarget()
	{
		lock (mutex)
		{
			return switchState.BackendSwitchTarget();
		}
	}

	/// <summary>Marks the start of a failover after the player's backend went away.</summary>
	public FailoverStart BeginFailover()
	{
		lock (mutex)
		{
			return switchState.BeginFailover();
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
			switchState.FinishFailover();
		}
	}

	public bool IsFailingOver()
	{
		lock (mutex)
		{
			return switchState.IsFailingOver();
		}
	}

	/// <summary>Starts the ordered backend try-list for a player who has not reached a world yet.</summary>
	public void BeginJoinSequence(List<BackendConfig> candidates)
	{
		lock (mutex)
		{
			switchState.BeginJoinSequence(candidates);
		}
	}

	public bool IsJoinSequenceActive()
	{
		lock (mutex)
		{
			return switchState.IsJoinSequenceActive();
		}
	}

	public void EndJoinSequence()
	{
		lock (mutex)
		{
			switchState.EndJoinSequence();
		}
	}

	public BackendConfig? NextJoinCandidate()
	{
		lock (mutex)
		{
			return switchState.NextJoinCandidate();
		}
	}

	/// <summary>Numbers the current attempt, so a failure of an earlier one cannot end a later one.</summary>
	public void BeginJoinAttempt()
	{
		lock (mutex)
		{
			switchState.BeginJoinAttempt();
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
			return switchState.ClaimJoinFailure();
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
			switchState.MarkClientJoinedWorld();
		}
	}

	public bool HasClientJoinedWorld()
	{
		lock (mutex)
		{
			return switchState.HasClientJoinedWorld();
		}
	}

	// -----------------------------------------------------------------------
	// Client-side view state (chunk radius, dimension, effects)
	// -----------------------------------------------------------------------

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
			entityIds.SetBackendPlayerRuntimeEntityId(runtimeEntityId);
		}
	}

	public long BackendPlayerRuntimeEntityId()
	{
		lock (mutex)
		{
			return entityIds.BackendPlayerRuntimeEntityId();
		}
	}

	public void SetBackendPlayerUniqueEntityId(long uniqueEntityId)
	{
		lock (mutex)
		{
			entityIds.SetBackendPlayerUniqueEntityId(uniqueEntityId);
		}
	}

	public long BackendPlayerUniqueEntityId()
	{
		lock (mutex)
		{
			return entityIds.BackendPlayerUniqueEntityId();
		}
	}

	public long ClientPlayerUniqueEntityId()
	{
		lock (mutex)
		{
			return entityIds.ClientPlayerUniqueEntityId();
		}
	}

	public long ToClientUniqueEntityId(long backendUniqueEntityId)
	{
		lock (mutex)
		{
			return entityIds.ToClientUniqueEntityId(backendUniqueEntityId);
		}
	}

	public long SwapClientUniqueEntityId(long value)
	{
		lock (mutex)
		{
			return entityIds.SwapClientUniqueEntityId(value);
		}
	}

	public long ClientPlayerRuntimeEntityId()
	{
		lock (mutex)
		{
			return entityIds.ClientPlayerRuntimeEntityId();
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
			return entityIds.ToClientRuntimeEntityId(backendRuntimeEntityId, registerEntity);
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
			return entityIds.HasBackendRuntimeEntityId(backendRuntimeEntityId);
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
			return entityIds.ToBackendRuntimeEntityId(clientRuntimeEntityId);
		}
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

	public void TrackClientEffect(int effectId, bool removed)
	{
		lock (mutex)
		{
			clientWorldState.TrackClientEffect(effectId, removed);
		}
	}

	/// <summary>Returns and clears the effects to remove client-side before a backend handoff.</summary>
	public int[] TakeActiveClientEffects()
	{
		lock (mutex)
		{
			return clientWorldState.TakeActiveClientEffects();
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

	// -----------------------------------------------------------------------
	// Deferred switch buffers
	// -----------------------------------------------------------------------

	public void AddDeferredSwitchPlayerState(IPacket packet)
	{
		if (packet == null)
		{
			return;
		}
		lock (mutex)
		{
			deferred.AddPlayerState(packet);
		}
	}

	public List<IPacket> DrainDeferredSwitchPlayerState()
	{
		lock (mutex)
		{
			return deferred.DrainPlayerState();
		}
	}

	public bool AddDeferredSwitchWorldState(IPacket packet)
	{
		if (packet == null)
		{
			return false;
		}
		lock (mutex)
		{
			return deferred.AddWorldState(packet);
		}
	}

	public List<IPacket> DrainDeferredSwitchWorldState()
	{
		lock (mutex)
		{
			return deferred.DrainWorldState();
		}
	}

	/// <summary>Drops buffered world state without sending it, when its switch was abandoned.</summary>
	public void ReleaseDeferredSwitchWorldState()
	{
		List<IPacket> released;
		lock (mutex)
		{
			released = deferred.ReleaseWorldState();
		}
	
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

	internal static long CurrentTimeMillis() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

	internal static long NanoTime() => (long)(Stopwatch.GetTimestamp() * (double)1_000_000_000 / Stopwatch.Frequency);
}
