using System;
using System.Text.Json.Nodes;

namespace EnderPearl.Config
{
	/// <summary>
	/// How hard the proxy tries when moving a player to another backend.
	///
	/// <p>Retries exist because the common reason a switch fails is that the target is restarting, not
	/// that it is gone. They run silently for <see cref="RetryWindowMillis"/>: a player who asked to be moved
	/// wants to be moved, not given a running commentary on the attempts.</p>
	/// </summary>
	public sealed class BackendSwitchConfig
	{
		public long RetryWindowMillis { get; }

		public long RetryDelayMillis { get; }

		public long TimeoutMillis { get; }

		public long ConnectTimeoutMillis { get; }

		private const long DEFAULT_RETRY_WINDOW_MILLIS = 30_000;
		private const long DEFAULT_RETRY_DELAY_MILLIS = 3_000;
		private const long DEFAULT_TIMEOUT_MILLIS = 20_000;
		private const long DEFAULT_CONNECT_TIMEOUT_MILLIS = 5_000;

		public BackendSwitchConfig(
			long retryWindowMillis,
			long retryDelayMillis,
			long timeoutMillis,
			long connectTimeoutMillis)
		{
			if (retryWindowMillis < 0)
			{
				throw new ArgumentException("retryWindowMillis cannot be negative");
			}
			if (retryDelayMillis < 0)
			{
				throw new ArgumentException("retryDelayMillis cannot be negative");
			}
			if (timeoutMillis < 1)
			{
				throw new ArgumentException("timeoutMillis must be positive");
			}
			if (connectTimeoutMillis < 1)
			{
				throw new ArgumentException("connectTimeoutMillis must be positive");
			}
			RetryWindowMillis = retryWindowMillis;
			RetryDelayMillis = retryDelayMillis;
			TimeoutMillis = timeoutMillis;
			ConnectTimeoutMillis = connectTimeoutMillis;
		}

		// 30s of retrying, roughly every 8s once a 5s dial-out failure is counted in - long enough
		// to cover a backend restart without leaving a player who mistyped a name waiting forever.
		public static BackendSwitchConfig Defaults()
		{
			return new BackendSwitchConfig(
				DEFAULT_RETRY_WINDOW_MILLIS,
				DEFAULT_RETRY_DELAY_MILLIS,
				DEFAULT_TIMEOUT_MILLIS,
				DEFAULT_CONNECT_TIMEOUT_MILLIS);
		}

		// ------------------------------------------------------------------ config

		public static BackendSwitchConfig From(JsonConfig config)
		{
			BackendSwitchConfig defaults = Defaults();
			return new BackendSwitchConfig(
				config.GetInt("switch.retryWindowMillis", (int)defaults.RetryWindowMillis),
				config.GetInt("switch.retryDelayMillis", (int)defaults.RetryDelayMillis),
				config.GetInt("switch.timeoutMillis", (int)defaults.TimeoutMillis),
				config.GetInt("switch.connectTimeoutMillis", (int)defaults.ConnectTimeoutMillis)
			);
		}

		/// <summary>The <c>"switch"</c> section of the generated default configuration.</summary>
		public static JsonObject DefaultSection()
		{
			BackendSwitchConfig defaults = Defaults();
			return new JsonObject
			{
				["retryWindowMillis"] = defaults.RetryWindowMillis,
				["retryDelayMillis"] = defaults.RetryDelayMillis,
				["timeoutMillis"] = defaults.TimeoutMillis,
				["connectTimeoutMillis"] = defaults.ConnectTimeoutMillis
			};
		}
	}
}
