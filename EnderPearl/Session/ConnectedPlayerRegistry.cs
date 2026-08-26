using System;
using System.Collections.Generic;
using EnderPearl.Backend;

namespace EnderPearl.Session
{
	public enum RegistrationResult
	{
		ACCEPTED,
		DUPLICATE_XUID,
		FULL
	}

	/// <summary>
	/// Every player currently registered with the proxy, keyed by XUID.
	///
	/// <para>Duplicate XUIDs are refused: a second session claiming the same identity is either a reconnect
	/// racing its own logout or someone spoofing, and neither should evict or alias the first.</para>
	/// </summary>
	public sealed class ConnectedPlayerRegistry
	{
		private readonly int maxPlayers;
		private readonly object mutex = new();
		private readonly Dictionary<string, ProxyConnection> connectionsByXuid = new();

		public ConnectedPlayerRegistry(int maxPlayers)
		{
			if (maxPlayers < 1)
			{
				throw new ArgumentException("maxPlayers must be positive");
			}
			this.maxPlayers = maxPlayers;
		}

		public RegistrationResult Register(ProxyConnection connection)
		{
			if (connection == null)
			{
				throw new ArgumentNullException(nameof(connection));
			}
			string key = Key(connection.ClientLogin.AuthData.Xuid);
			lock (mutex)
			{
				if (connectionsByXuid.ContainsKey(key))
				{
					return RegistrationResult.DUPLICATE_XUID;
				}
				if (connectionsByXuid.Count >= maxPlayers)
				{
					return RegistrationResult.FULL;
				}
				connectionsByXuid[key] = connection;
				return RegistrationResult.ACCEPTED;
			}
		}

		public void Unregister(ProxyConnection? connection)
		{
			if (connection == null)
			{
				return;
			}
			string key = Key(connection.ClientLogin.AuthData.Xuid);
			lock (mutex)
			{
				// Java removed only when the entry still maps to this exact connection; a reconnect that
				// already replaced the mapping must not unregister its replacement.
				if (connectionsByXuid.TryGetValue(key, out ProxyConnection? current) && ReferenceEquals(current, connection))
				{
					connectionsByXuid.Remove(key);
				}
			}
		}

		public int Size()
		{
			lock (mutex)
			{
				return connectionsByXuid.Count;
			}
		}

		/// <summary>
		/// A snapshot of everyone currently connected. Copied rather than exposed live: the callers are
		/// commands that message each player in turn, and holding the registry's lock while writing to a
		/// connection is a good way to stall every other login.
		/// </summary>
		public List<ProxyConnection> Connections()
		{
			lock (mutex)
			{
				return new List<ProxyConnection>(connectionsByXuid.Values);
			}
		}

		/// <summary>
		/// Finds a connected player by gamertag, case-insensitively. Gamertags are unique on Xbox Live
		/// and come from the Mojang-signed chain, so this cannot be pointed at someone else by choosing a
		/// clever name.
		/// </summary>
		public ProxyConnection? FindByName(string? name)
		{
			if (string.IsNullOrWhiteSpace(name))
			{
				return null;
			}
			string wanted = name.Trim();
			lock (mutex)
			{
				foreach (ProxyConnection connection in connectionsByXuid.Values)
				{
					if (string.Equals(wanted, connection.ClientLogin.AuthData.DisplayName, StringComparison.OrdinalIgnoreCase))
					{
						return connection;
					}
				}
			}
			return null;
		}

		/// <summary>
		/// Returns the real XUID (from the client's Mojang-signed chain) for a
		/// currently-connected player matched by display name, or an empty string
		/// if no such player is online. Used by the backend relay to inject real
		/// XUIDs into outgoing PlayerListPacket entries - BDS in offline mode
		/// (1.26.10+) leaves those blank because it does not trust self-signed
		/// xid claims, breaking the client-side friends tab.
		/// </summary>
		public string XuidByName(string? name)
		{
			if (string.IsNullOrEmpty(name))
			{
				return "";
			}
			lock (mutex)
			{
				foreach (ProxyConnection connection in connectionsByXuid.Values)
				{
					if (string.Equals(name, connection.ClientLogin.AuthData.DisplayName, StringComparison.OrdinalIgnoreCase))
					{
						return connection.ClientLogin.AuthData.Xuid;
					}
				}
			}
			return "";
		}

		private static string Key(string xuid)
		{
			return xuid.ToLowerInvariant();
		}
	}
}
