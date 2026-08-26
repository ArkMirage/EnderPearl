using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace EnderPearl.Backend
{
	/// <summary>
	/// Where a player should land on their <em>next</em> login, when the proxy has just asked them to
	/// reconnect.
	///
	/// <para>Some backends cannot be joined by a seamless handoff — see
	/// <see cref="Palette.BackendPalette"/>. Those are reached by sending the client back to the proxy's
	/// own address, which means the destination has to survive the gap between the transfer and the new
	/// login. That is all this holds.</para>
	///
	/// <para>Deliberately short-lived and single-use. A route that outlived its reconnect would silently
	/// redirect an ordinary login much later — the player logs in tomorrow and lands somewhere they never
	/// asked for, with nothing in the log to explain it. Expiry is a correctness property here, not
	/// housekeeping.</para>
	/// </summary>
	public sealed class ReconnectRoutes
	{
		/// <summary>
		/// Long enough for a client to tear down its session, reconnect and re-run the login handshake
		/// including resource packs, short enough that a player who gave up and closed the game does not
		/// find themselves redirected when they come back.
		/// </summary>
		internal const long DEFAULT_TTL_MILLIS = 60_000L;

		private readonly ConcurrentDictionary<string, Route> routes = new();
		private readonly long ttlMillis;

		public ReconnectRoutes() : this(DEFAULT_TTL_MILLIS)
		{
		}

		internal ReconnectRoutes(long ttlMillis)
		{
			ttlMillis = ttlMillis < 1 ? DEFAULT_TTL_MILLIS : ttlMillis;
			this.ttlMillis = ttlMillis;
		}

		private long NowMillis => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

		/// <summary>Keyed on XUID, which is the one identifier that survives a reconnect unchanged.</summary>
		public void Remember(string? xuid, string? backendName)
		{
			if (IsBlank(xuid) || IsBlank(backendName))
			{
				return;
			}
			Prune();
			routes[xuid] = new Route(backendName!, NowMillis + ttlMillis);
		}

		/// <summary>
		/// Takes the pending destination for this player, if any.
		///
		/// <para>Consumed rather than read: the reconnect it belongs to has now happened. If that login
		/// fails for some other reason the player falls back to the ordinary join path, which is the
		/// right outcome — retrying a route the client could not use once is not obviously better.</para>
		/// </summary>
		public string? Take(string? xuid)
		{
			if (IsBlank(xuid))
			{
				return null;
			}
			routes.TryRemove(xuid, out Route? route);
			if (route == null)
			{
				return null;
			}
			return route.ExpiresAtMillis < NowMillis ? null : route.BackendName;
		}

		public void Forget(string? xuid)
		{
			if (!IsBlank(xuid))
			{
				routes.TryRemove(xuid, out _);
			}
		}

		public int Size()
		{
			Prune();
			return routes.Count;
		}

		private void Prune()
		{
			long now = NowMillis;
			foreach (KeyValuePair<string, Route> pair in routes)
			{
				if (pair.Value.ExpiresAtMillis < now)
				{
					routes.TryRemove(pair.Key, out _);
				}
			}
		}

		private static bool IsBlank(string? value)
		{
			return value == null || value.Trim().Length == 0;
		}

		private sealed record Route(string BackendName, long ExpiresAtMillis);
	}
}
