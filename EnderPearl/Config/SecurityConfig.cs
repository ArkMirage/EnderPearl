using System;
using System.Text.Json.Nodes;

namespace EnderPearl.Config;

/// <summary>
/// Limits that keep one client - or one host pretending to be many - from costing everyone else their
/// session.
///
/// <para>The packet-rate limiter that used to live here was removed in 2026-08-24: its 10ms-tick per-address
/// budget mistook login bursts and resource-pack ACK streams for floods and blocked legitimate players for
/// ten seconds at a time.</para>
/// </summary>
public sealed class SecurityConfig
{
	private int maxConnectionsPerAddress;

	public int MaxConnectionsPerAddress { get => maxConnectionsPerAddress; init => maxConnectionsPerAddress = Checked(value, 1, "maxConnectionsPerAddress must be positive"); }

	private int maxConnectionAttempts;

	public int MaxConnectionAttempts { get => maxConnectionAttempts; init => maxConnectionAttempts = Checked(value, 1, "maxConnectionAttempts must be positive"); }

	private long connectionAttemptWindowMillis;

	public long ConnectionAttemptWindowMillis { get => connectionAttemptWindowMillis; init => connectionAttemptWindowMillis = Checked(value, 0, "connectionAttemptWindowMillis cannot be negative"); }

	public bool RequireXuid { get; init; }

	private long commandCooldownMillis;

	public long CommandCooldownMillis { get => commandCooldownMillis; init => commandCooldownMillis = Checked(value, 0, "commandCooldownMillis cannot be negative"); }

	private static T Checked<T>(T value, T floor, string message) where T : IComparable<T>
	{
		if (value.CompareTo(floor) < 0)
		{
			throw new ArgumentException(message);
		}
		return value;
	}

	public static SecurityConfig Defaults()
	{
		return new SecurityConfig
		{
			MaxConnectionsPerAddress = 64,
			MaxConnectionAttempts = 8,
			ConnectionAttemptWindowMillis = 10_000,
			RequireXuid = true,
			CommandCooldownMillis = 1_000
		};
	}

	public static SecurityConfig From(JsonConfig config)
	{
		SecurityConfig defaults = Defaults();
		return new SecurityConfig
		{
			MaxConnectionsPerAddress = config.GetInt("security.maxConnectionsPerAddress", defaults.MaxConnectionsPerAddress),
			MaxConnectionAttempts = config.GetInt("security.maxConnectionAttempts", defaults.MaxConnectionAttempts),
			ConnectionAttemptWindowMillis = config.GetInt("security.connectionAttemptWindowMillis",
				(int)defaults.ConnectionAttemptWindowMillis),
			RequireXuid = config.GetBool("security.requireXuid", defaults.RequireXuid),
			CommandCooldownMillis = config.GetInt("security.commandCooldownMillis", (int)defaults.CommandCooldownMillis)
		};
	}

	/// <summary>The <c>"security"</c> section of the generated default configuration.</summary>
	public static JsonObject DefaultSection()
	{
		SecurityConfig defaults = Defaults();
		return new JsonObject
		{
			["maxConnectionsPerAddress"] = defaults.MaxConnectionsPerAddress,
			["maxConnectionAttempts"] = defaults.MaxConnectionAttempts,
			["connectionAttemptWindowMillis"] = defaults.ConnectionAttemptWindowMillis,
			["requireXuid"] = defaults.RequireXuid,
			["commandCooldownMillis"] = defaults.CommandCooldownMillis
		};
	}
}
