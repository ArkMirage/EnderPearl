using System;
using System.Text.Json.Nodes;
using EnderPearl.Logging;

namespace EnderPearl.Config
{
	/// <summary>
	/// What to do with a player whose session hit a protocol fault rather than a backend outage.
	///
	/// <p>By default a protocol fault disconnects the player with a reason and is written to a
	/// dedicated log. Set <c>protocolFault.action=failover</c> to get the old behaviour back.</p>
	/// </summary>
	public sealed class ProtocolFaultPolicy
	{
		public enum ProtocolFaultAction
		{
			/// <summary>Kick the player with <see cref="Message"/> and log the fault. The default.</summary>
			DISCONNECT,
			/// <summary>Treat it as an ordinary backend loss and walk the fallback chain. Still logged.</summary>
			FAILOVER
		}

		public const string DEFAULT_MESSAGE =
			"Disconnected: the server sent something this proxy could not relay. This has been logged - please report it.";

		public const string DEFAULT_LOG_FILE = "logs/protocol-errors.log";

		public ProtocolFaultAction FaultAction { get; }

		public string Message { get; }

		public string? LogFile { get; }

		public ProtocolFaultPolicy(ProtocolFaultAction faultAction, string? message, string? logFile)
		{
			if (string.IsNullOrWhiteSpace(message))
			{
				message = DEFAULT_MESSAGE;
			}
			FaultAction = faultAction;
			Message = message;
			LogFile = logFile?.Trim();
		}

		public static ProtocolFaultPolicy Defaults()
		{
			return new ProtocolFaultPolicy(ProtocolFaultAction.DISCONNECT, DEFAULT_MESSAGE, DEFAULT_LOG_FILE);
		}

		public bool Disconnects() => FaultAction == ProtocolFaultAction.DISCONNECT;

		/// <summary>An empty <c>protocolFault.logFile</c> turns the dedicated log off without disabling the rule.</summary>
		public bool LogsToFile() => !string.IsNullOrEmpty(LogFile);

		// ------------------------------------------------------------------ config

		/// <summary>
		/// Reads the <c>"protocolFault"</c> section. A protocol fault is not a backend outage and must
		/// not be treated as one, which is why it has its own section. Has rather than a plain read on
		/// the log file, because an explicitly empty value means "keep the rule, drop the dedicated log".
		/// </summary>
		public static ProtocolFaultPolicy From(JsonConfig config)
		{
			return new ProtocolFaultPolicy(
				ParseAction(config.GetString("protocolFault.action")),
				config.GetString("protocolFault.message", DEFAULT_MESSAGE),
				config.Has("protocolFault.logFile")
					? config.GetString("protocolFault.logFile", "")
					: DEFAULT_LOG_FILE
			);
		}

		/// <summary>The <c>"protocolFault"</c> section of the generated default configuration.</summary>
		public static JsonObject DefaultSection()
		{
			return new JsonObject
			{
				["action"] = ProtocolFaultAction.DISCONNECT.ToString().ToLowerInvariant(),
				["message"] = DEFAULT_MESSAGE,
				["logFile"] = DEFAULT_LOG_FILE
			};
		}

		/// <summary>Unrecognised values fall back to the default rather than refusing to start the proxy.</summary>
		public static ProtocolFaultAction ParseAction(string? value)
		{
			if (value == null)
			{
				return ProtocolFaultAction.DISCONNECT;
			}
			string normalized = value.Trim().ToUpperInvariant();
			foreach (ProtocolFaultAction candidate in Enum.GetValues<ProtocolFaultAction>())
			{
				if (candidate.ToString().Equals(normalized, StringComparison.Ordinal))
				{
					return candidate;
				}
			}
			Logger.Error(
				$"Unknown protocolFault.action '{value}'; using disconnect. Valid values: disconnect, failover.");
			return ProtocolFaultAction.DISCONNECT;
		}
	}
}
