using System;
using System.Collections.Concurrent;
using System.Net;
using EnderPearl.Config;
using EnderPearl.Logging;

namespace EnderPearl.Security
{
	/// <summary>
	/// Caps how many sessions one address may hold at once, and how fast it may open new ones.
	///
	/// <para>RakNet's own limits are per-datagram and global: the connection pool is a single pool
	/// everyone draws from, so without this one host can hold every slot and nobody else gets in. Each
	/// accepted session also costs the proxy a full backend dial-out, which makes the connection
	/// <em>rate</em> matter as much as the count - an unthrottled attacker turns one UDP stream into a
	/// flood of handshakes against every backend.</para>
	///
	/// <para>Limits are per IP, not per <c>(ip, port)</c>: the source port changes on every reconnect, so
	/// counting by socket address would count nothing. That does mean players behind one home NAT share
	/// a budget, which is why <see cref="SecurityConfig.MaxConnectionsPerAddress"/> defaults well above 1.</para>
	///
	/// <para>Called from I/O threads, so the map is concurrent and no lock is held across a callback.</para>
	/// </summary>
	public sealed class ConnectionThrottle
	{
		/// <summary>Above this many tracked addresses, sweep the expired ones. Ordinary traffic never reaches it.</summary>
		private const int SWEEP_THRESHOLD = 4096;

		private readonly SecurityConfig config;
		private readonly Func<long> clock;
		private readonly ConcurrentDictionary<IPAddress, AddressState> states = new();

		public ConnectionThrottle(SecurityConfig config)
			: this(config, () => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
		{
		}

		public ConnectionThrottle(SecurityConfig config, Func<long>? clock)
		{
			this.config = config ?? throw new ArgumentException("config cannot be null");
			this.clock = clock ?? (Func<long>)(() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
		}

		/// <summary>
		/// Claims a session slot for an address.
		///
		/// <returns>false if the address is over either limit, in which case the caller must close the
		/// session and must <em>not</em> call <see cref="Release"/></returns>
		/// </summary>
		public bool Accept(IPEndPoint? socketAddress)
		{
			IPAddress? address = AddressOf(socketAddress);
			if (address == null)
			{
				return true;
			}
			long now = clock();
			SweepIfCrowded(now);

			AddressState state = states.GetOrAdd(address, static _ => new AddressState());
			lock (state)
			{
				if (state.open >= config.MaxConnectionsPerAddress)
				{
					Report(address, state, now, $"already has {state.open} open session(s)");
					return false;
				}
				if (now - state.windowStartedAtMillis >= config.ConnectionAttemptWindowMillis)
				{
					state.windowStartedAtMillis = now;
					state.attempts = 0;
				}
				if (state.attempts >= config.MaxConnectionAttempts)
				{
					Report(address, state, now, $"opened {state.attempts} session(s) within {config.ConnectionAttemptWindowMillis}ms");
					return false;
				}
				state.attempts++;
				state.open++;
				state.lastSeenAtMillis = now;
				return true;
			}
		}

		/// <summary>Returns a slot claimed by a successful <see cref="Accept"/>.</summary>
		public void Release(IPEndPoint? socketAddress)
		{
			IPAddress? address = AddressOf(socketAddress);
			if (address == null)
			{
				return;
			}
			if (!states.TryGetValue(address, out AddressState? state))
			{
				return;
			}
			lock (state)
			{
				if (state.open > 0)
				{
					state.open--;
				}
				state.lastSeenAtMillis = clock();
			}
		}

		private static IPAddress? AddressOf(IPEndPoint? socketAddress)
		{
			return socketAddress?.Address;
		}

		/// <summary>
		/// A refused address usually keeps trying, and one log line per attempt is how a throttle turns a
		/// flood into a disk-space problem. One line per address per window is enough to see it happening.
		/// </summary>
		private void Report(IPAddress address, AddressState state, long now, string detail)
		{
			state.lastSeenAtMillis = now;
			if (now - state.lastReportedAtMillis < config.ConnectionAttemptWindowMillis)
			{
				return;
			}
			state.lastReportedAtMillis = now;
			Logger.Info($"Refused connection from {address}: it {detail}.");
		}

		private void SweepIfCrowded(long now)
		{
			if (states.Count < SWEEP_THRESHOLD)
			{
				return;
			}
			long idleCutoff = now - Math.Max(config.ConnectionAttemptWindowMillis, 60_000L);
			foreach (KeyValuePair<IPAddress, AddressState> entry in states)
			{
				AddressState state = entry.Value;
				lock (state)
				{
					if (state.open == 0 && state.lastSeenAtMillis < idleCutoff)
					{
						states.TryRemove(entry.Key, out _);
					}
				}
			}
		}

		private sealed class AddressState
		{
			internal int open;
			internal int attempts;
			internal long windowStartedAtMillis;
			internal long lastSeenAtMillis;
			internal long lastReportedAtMillis = long.MinValue / 2;
		}
	}
}
