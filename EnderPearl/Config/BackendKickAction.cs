using System;
using EnderPearl.Logging;

namespace EnderPearl.Config
{
	/// <summary>
	/// What to do with a player when the backend they are on kicks them.
	///
	/// <p>Two very different events arrive as the same packet. A backend shutting down kicks everyone
	/// before the socket closes, and those players should be moved to a fallback. A backend banning
	/// somebody also kicks them, and moving <em>that</em> player to a fallback overrides the ban - and
	/// loops, because the fallback transfers them straight back to the backend that just refused them.</p>
	///
	/// <p>The wire distinguishes them, which is what <see cref="BackendKickAction.AUTO"/> keys off. A
	/// <c>DisconnectPacket</c> carries a <c>messageSkipped</c> flag: a host-level disconnect sends only a reason
	/// (<c>HOST_DISCONNECTED</c>, <c>SERVER_SHUTDOWN</c>) with the message skipped, while a ban or a
	/// moderator kick carries text written for that specific player. Message present means somebody
	/// decided something about this player; message absent means the host went away.</p>
	/// </summary>
	public enum BackendKickAction
	{
		/// <summary>Fail over when the backend skipped the message, pass the kick through when it sent one.</summary>
		AUTO,
		/// <summary>Never fail over on a kick. Bans always hold; a restart drops its players.</summary>
		DISCONNECT,
		/// <summary>Always fail over on a kick. Restarts are seamless; a ban can be escaped.</summary>
		FAILOVER
	}

	public static class BackendKickActions
	{
		/// <summary>Decides for one kick: did the backend send kick text of its own?</summary>
		public static bool FailsOver(this BackendKickAction action, bool backendSuppliedMessage)
		{
			return action switch
			{
				BackendKickAction.AUTO => !backendSuppliedMessage,
				BackendKickAction.DISCONNECT => false,
				BackendKickAction.FAILOVER => true,
				_ => throw new ArgumentOutOfRangeException(nameof(action))
			};
		}

		/// <summary>Unrecognised values fall back to AUTO rather than refusing to start the proxy.</summary>
		public static BackendKickAction Parse(string? value)
		{
			if (string.IsNullOrWhiteSpace(value))
			{
				return BackendKickAction.AUTO;
			}
			string normalized = value.Trim().ToUpperInvariant();
			foreach (BackendKickAction candidate in Enum.GetValues<BackendKickAction>())
			{
				if (candidate.ToString().Equals(normalized, StringComparison.Ordinal))
				{
					return candidate;
				}
			}
			Logger.Error(
				$"Unknown failover.onBackendKick '{value}'; using auto. Valid values: auto, disconnect, failover.");
			return BackendKickAction.AUTO;
		}
	}
}
