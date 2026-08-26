using global::Protocol.Packets;
using global::Protocol.Types;

namespace EnderPearl.Backend
{
	/// <summary>
	/// Input-permission state carried across a backend switch.
	///
	/// <p>Input locks are client-session state rather than world state. Disconnecting normally clears
	/// them, but a seamless backend switch deliberately keeps the client session alive. The source
	/// backend's mask therefore has to be cleared explicitly and the target backend's mask restored
	/// after the dimension reset finishes.</p>
	/// </summary>
	public sealed class BackendSwitchInputState
	{
		private readonly object mutex = new();
		private uint targetLockComponentData;

		public BackendSwitchInputState(uint targetLockComponentData)
		{
			this.targetLockComponentData = targetLockComponentData;
		}

		public void RememberTarget(uint newTargetLockComponentData)
		{
			lock (mutex)
			{
				targetLockComponentData = newTargetLockComponentData;
			}
		}

		public UpdateClientInputLocksPacket ClearSource(Vec3? position)
		{
			return Packet(0, position);
		}

		public UpdateClientInputLocksPacket RestoreTarget(Vec3? position)
		{
			lock (mutex)
			{
				return Packet(targetLockComponentData, position);
			}
		}

		private static UpdateClientInputLocksPacket Packet(uint lockComponentData, Vec3? position)
		{
			var packet = new UpdateClientInputLocksPacket();
			packet.InputLockComponentData = lockComponentData;
			// Protocol 2168 serializes only the lock mask; the pre-v944 server-position field no longer
			// exists on the wire, so the position argument is kept for call-site fidelity and ignored.
			return packet;
		}
	}
}
