using System;
using EnderPearl.Core;

namespace EnderPearl.Config
{
	/// <summary>
	/// What to do with a player when the backend they are on disconnects them.
	///
	/// <p>Two very different events arrive as the same packet. A backend shutting down disconnects
	/// everyone before the socket closes, and those players should be moved to a fallback. A backend
	/// banning somebody also disconnects them, and moving <em>that</em> player to a fallback overrides the
	/// ban - and loops, because the fallback transfers them straight back to the backend that just refused
	/// them.</p>
	///
	/// <p>The wire distinguishes them, which is what <see cref="BackendDisconnectAction.AUTO"/> keys off. A
	/// <c>DisconnectPacket</c> carries a <c>messageSkipped</c> flag: a host-level disconnect sends only a reason
	/// (<c>HOST_DISCONNECTED</c>, <c>SERVER_SHUTDOWN</c>) with the message skipped, while a ban or a
	/// moderator kick carries text written for that specific player. Message present means somebody
	/// decided something about this player; message absent means the host went away.</p>
	/// </summary>
	public enum BackendDisconnectAction
	{
		/// <summary>Fail over when the backend skipped the message, pass the disconnect through when it sent one.</summary>
		AUTO,
		/// <summary>Never fail over on a disconnect. Bans always hold; a restart drops its players.</summary>
		DISCONNECT,
		/// <summary>Always fail over on a disconnect. Restarts are seamless; a ban can be escaped.</summary>
		FAILOVER
	}

	public static class BackendDisconnectActions
	{
		/// <summary>Decides for one disconnect: did the backend send disconnect text of its own?</summary>
		public static bool FailsOver(this BackendDisconnectAction action, bool backendSuppliedMessage)
		{
			return action switch
			{
				BackendDisconnectAction.AUTO => !backendSuppliedMessage,
				BackendDisconnectAction.DISCONNECT => false,
				BackendDisconnectAction.FAILOVER => true,
				_ => throw new ArgumentOutOfRangeException(nameof(action))
			};
		}

		/// <summary>Unrecognised values fall back to AUTO rather than refusing to start the proxy.</summary>
		public static BackendDisconnectAction Parse(string? value)
		{
			if (string.IsNullOrWhiteSpace(value))
			{
				return BackendDisconnectAction.AUTO;
			}
			string normalized = value.Trim().ToUpperInvariant();
			foreach (BackendDisconnectAction candidate in Enum.GetValues<BackendDisconnectAction>())
			{
				if (candidate.ToString().Equals(normalized, StringComparison.Ordinal))
				{
					return candidate;
				}
			}
			Logger.Error(
				$"Unknown failover.onBackendDisconnect '{value}'; using auto. Valid values: auto, disconnect, failover.");
			return BackendDisconnectAction.AUTO;
		}
	}
}