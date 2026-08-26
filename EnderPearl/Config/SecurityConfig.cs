using System;
using System.Text.Json.Nodes;

namespace EnderPearl.Config
{
	/// <summary>
	/// Limits that keep one client - or one host pretending to be many - from costing everyone else
	/// their session.
	///
	/// <para>The packet-rate limiter that used to live here was removed in 2026-08-24: its 10ms-tick
	/// per-address budget mistook login bursts and resource-pack ACK streams for floods and blocked
	/// legitimate players for ten seconds at a time.</para>
	/// </summary>
	public sealed class SecurityConfig
	{
		public bool SendConnectionCookie { get; }

		public int MaxConnectionsPerAddress { get; }

		public int MaxConnectionAttempts { get; }

		public long ConnectionAttemptWindowMillis { get; }

		public bool RequireXuid { get; }

		public long CommandCooldownMillis { get; }

		public SecurityConfig(
			bool sendConnectionCookie,
			int maxConnectionsPerAddress,
			int maxConnectionAttempts,
			long connectionAttemptWindowMillis,
			bool requireXuid,
			long commandCooldownMillis)
		{
			if (maxConnectionsPerAddress < 1)
			{
				throw new ArgumentException("maxConnectionsPerAddress must be positive");
			}
			if (maxConnectionAttempts < 1)
			{
				throw new ArgumentException("maxConnectionAttempts must be positive");
			}
			if (connectionAttemptWindowMillis < 0)
			{
				throw new ArgumentException("connectionAttemptWindowMillis cannot be negative");
			}
			if (commandCooldownMillis < 0)
			{
				throw new ArgumentException("commandCooldownMillis cannot be negative");
			}
			SendConnectionCookie = sendConnectionCookie;
			MaxConnectionsPerAddress = maxConnectionsPerAddress;
			MaxConnectionAttempts = maxConnectionAttempts;
			ConnectionAttemptWindowMillis = connectionAttemptWindowMillis;
			RequireXuid = requireXuid;
			CommandCooldownMillis = commandCooldownMillis;
		}

		public static SecurityConfig Defaults()
		{
			return new SecurityConfig(true, 64, 8, 10_000, true, 1_000);
		}

		public static SecurityConfig From(JsonConfig config)
		{
			SecurityConfig defaults = Defaults();
			return new SecurityConfig(
				config.GetBool("security.sendConnectionCookie", defaults.SendConnectionCookie),
				config.GetInt("security.maxConnectionsPerAddress", defaults.MaxConnectionsPerAddress),
				config.GetInt("security.maxConnectionAttempts", defaults.MaxConnectionAttempts),
				config.GetInt("security.connectionAttemptWindowMillis",
					(int)defaults.ConnectionAttemptWindowMillis),
				config.GetBool("security.requireXuid", defaults.RequireXuid),
				config.GetInt("security.commandCooldownMillis", (int)defaults.CommandCooldownMillis)
			);
		}

		/// <summary>The <c>"security"</c> section of the generated default configuration.</summary>
		public static JsonObject DefaultSection()
		{
			SecurityConfig defaults = Defaults();
			return new JsonObject
			{
				["sendConnectionCookie"] = defaults.SendConnectionCookie,
				["maxConnectionsPerAddress"] = defaults.MaxConnectionsPerAddress,
				["maxConnectionAttempts"] = defaults.MaxConnectionAttempts,
				["connectionAttemptWindowMillis"] = defaults.ConnectionAttemptWindowMillis,
				["requireXuid"] = defaults.RequireXuid,
				["commandCooldownMillis"] = defaults.CommandCooldownMillis
			};
		}
	}
}
