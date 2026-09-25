using System.Collections.Generic;

namespace EnderPearl.Player
{
	/// <summary>
	/// The player's entity-id identity across backends: the local player's runtime/unique id pair, the
	/// bidirectional runtime-id rewrite table, and the synthetic-id generator that keeps backend ids
	/// from colliding with client ones.
	///
	/// <para>Not thread-safe by design: every method must be called while holding the owning
	/// <see cref="ProxyConnection"/>'s mutex, which is what keeps a rewrite and a backend swap atomic.</para>
	/// </summary>
	public sealed class PlayerEntityIds
	{
		private long backendPlayerRuntimeEntityId;
		private long clientPlayerRuntimeEntityId;
		private long backendPlayerUniqueEntityId;
		private long clientPlayerUniqueEntityId;
		private long nextSyntheticClientRuntimeEntityId = 1_000_000_000L;
		private readonly Dictionary<long, long> backendToClientRuntimeIds = new();
		private readonly Dictionary<long, long> clientToBackendRuntimeIds = new();

		/// <summary>Called when a backend's StartGame names the local player; resets the rewrite table.</summary>
		public void SetBackendPlayerRuntimeEntityId(long runtimeEntityId)
		{
			backendPlayerRuntimeEntityId = runtimeEntityId;
			if (clientPlayerRuntimeEntityId <= 0)
			{
				clientPlayerRuntimeEntityId = runtimeEntityId;
			}
			backendToClientRuntimeIds.Clear();
			clientToBackendRuntimeIds.Clear();
			RegisterMapping(runtimeEntityId, clientPlayerRuntimeEntityId);
		}

		public long BackendPlayerRuntimeEntityId() => backendPlayerRuntimeEntityId;

		/// <summary>
		/// The client keeps the identity it was given by its first StartGame for the whole proxy session,
		/// but every backend assigns the player its own unique entity id. Only the local player's id is
		/// remapped. Unique ids are signed and routinely negative, so 0 is the "not yet known" sentinel.
		/// </summary>
		public void SetBackendPlayerUniqueEntityId(long uniqueEntityId)
		{
			backendPlayerUniqueEntityId = uniqueEntityId;
			if (clientPlayerUniqueEntityId == 0)
			{
				clientPlayerUniqueEntityId = uniqueEntityId;
			}
		}

		public long BackendPlayerUniqueEntityId() => backendPlayerUniqueEntityId;

		public long ClientPlayerUniqueEntityId()
		{
			return clientPlayerUniqueEntityId == 0 ? backendPlayerUniqueEntityId : clientPlayerUniqueEntityId;
		}

		public long ToClientUniqueEntityId(long backendUniqueEntityId)
		{
			return backendPlayerUniqueEntityId != 0 && backendUniqueEntityId == backendPlayerUniqueEntityId
				? (clientPlayerUniqueEntityId == 0 ? backendPlayerUniqueEntityId : clientPlayerUniqueEntityId)
				: backendUniqueEntityId;
		}

		/// <summary>
		/// Java PlayerRewriteUtils.rewriteId applied to the local player's unique-id pair: swaps
		/// backend-id to client-id and back, leaving every other value untouched, so running the same
		/// value through twice restores the original. ActorLink endpoints carry unique ids, so unlike
		/// runtime ids they get exactly this plain swap - no table lookup, and nothing is registered.
		/// </summary>
		public long SwapClientUniqueEntityId(long value)
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

		public long ClientPlayerRuntimeEntityId()
		{
			return clientPlayerRuntimeEntityId <= 0 ? backendPlayerRuntimeEntityId : clientPlayerRuntimeEntityId;
		}

		public long ToClientRuntimeEntityId(long backendRuntimeEntityId, bool registerEntity)
		{
			if (backendRuntimeEntityId <= 0)
			{
				return backendRuntimeEntityId;
			}
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
			RegisterMapping(backendRuntimeEntityId, clientRuntimeEntityId);
			return clientRuntimeEntityId;
		}

		public bool HasBackendRuntimeEntityId(long backendRuntimeEntityId)
		{
			return backendRuntimeEntityId > 0 && backendToClientRuntimeIds.ContainsKey(backendRuntimeEntityId);
		}

		public long ToBackendRuntimeEntityId(long clientRuntimeEntityId)
		{
			if (clientRuntimeEntityId <= 0)
			{
				return clientRuntimeEntityId;
			}
			return clientToBackendRuntimeIds.GetValueOrDefault(clientRuntimeEntityId, clientRuntimeEntityId);
		}

		private void RegisterMapping(long backendRuntimeEntityId, long clientRuntimeEntityId)
		{
			if (backendRuntimeEntityId <= 0 || clientRuntimeEntityId <= 0)
			{
				return;
			}
			backendToClientRuntimeIds[backendRuntimeEntityId] = clientRuntimeEntityId;
			clientToBackendRuntimeIds[clientRuntimeEntityId] = backendRuntimeEntityId;
		}
	}
}
