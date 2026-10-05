using System.Collections.Generic;
using global::Protocol.Packets;
using EnderPearl.Core;

namespace EnderPearl.Player;
/// <summary>
/// Client-ready packets buffered across a backend switch: the local-player authoritative state and
/// the world geometry the backend will never resend on its own. Replayed by the switch reset once
/// the client is back in the target dimension.
///
/// <para>Not thread-safe by design: every method must be called while holding the owning
/// <see cref="ProxyConnection"/>'s mutex, so a buffer fill and a backend handoff stay ordered.</para>
/// </summary>
public sealed class DeferredSwitchState
{
	/// <summary>
	/// Cap on world-state packets held across a switch reset. Sized to cover a full 32-chunk view
	/// (4225 columns) plus the publisher updates and block edits interleaved with them.
	/// </summary>
	private const int MAX_WORLD_STATE = 8192;

	private readonly List<IPacket> playerState = new();
	private readonly List<IPacket> worldState = new();
	private bool worldStateOverflowed;

	/// <summary>
	/// Records a client-ready (already rewritten) packet carrying local-player state which the
	/// backend only emits once during its join burst.
	/// </summary>
	public void AddPlayerState(IPacket packet)
	{
		if (packet == null)
		{
			return;
		}
		playerState.Add(packet);
	}

	public List<IPacket> DrainPlayerState()
	{
		if (playerState.Count == 0)
		{
			return new List<IPacket>();
		}
		List<IPacket> drained = new(playerState);
		playerState.Clear();
		return drained;
	}

	/// <summary>
	/// Records a client-ready packet that carries world geometry - chunks, sub-chunks, block updates
	/// and the publisher updates that scope them - buffered during a switch instead of dropped, then
	/// replayed once the client is back in the target dimension. Bounded by
	/// MAX_WORLD_STATE; past the cap we fall back to dropping.
	/// </summary>
	public bool AddWorldState(IPacket packet)
	{
		if (packet == null)
		{
			return false;
		}
		if (worldState.Count >= MAX_WORLD_STATE)
		{
			if (!worldStateOverflowed)
			{
				worldStateOverflowed = true;
				Logger.Info(
					$"Deferred switch world-state buffer full at {MAX_WORLD_STATE} packets; dropping further world state until the switch reset completes.");
			}
			return false;
		}
		worldState.Add(packet);
		return true;
	}

	public List<IPacket> DrainWorldState()
	{
		worldStateOverflowed = false;
		if (worldState.Count == 0)
		{
			return new List<IPacket>();
		}
		List<IPacket> drained = new(worldState);
		worldState.Clear();
		return drained;
	}

	/// <summary>Drops buffered world state without sending it, when its switch was abandoned.</summary>
	public List<IPacket> ReleaseWorldState()
	{
		List<IPacket> released = new(worldState);
		worldState.Clear();
		worldStateOverflowed = false;
		return released;
	}

	/// <summary>
	/// Clears both buffers as part of a backend handoff and returns the world state that was held,
	/// so the facade can drop it outside the mutex. The Java original released each packet's
	/// retained ByteBuf here; these are plain managed objects, so the list drop is the whole job.
	/// </summary>
	public List<IPacket> ClearForBackendSwitch()
	{
		playerState.Clear();
		List<IPacket> released = new(worldState);
		worldState.Clear();
		worldStateOverflowed = false;
		return released;
	}
}
