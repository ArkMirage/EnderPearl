using System;
using EnderPearl.Net;
using global::Protocol;

namespace EnderPearl.Backend
{
	/// <summary>
	/// Reconciles the per-player permission fields sent by a Bedrock backend.
	///
	/// <p>Endstone/BDS can report an operator's individual command level as ADMIN while leaving the
	/// player permission at the world's MEMBER default. The command level changes when the backend ops
	/// or deops the player, so it is the backend-owned signal used to correct that contradiction.</p>
	/// </summary>
	public static class BackendPermissionSync
	{
		/// <summary>
		/// Fixes an UpdateAbilitiesPacket in place; returns true when a correction was applied.
		/// </summary>
		public static bool Apply(global::Protocol.Packets.UpdateAbilitiesPacket? packet)
		{
			if (packet == null || packet.Data == null)
			{
				return false;
			}
			if (packet.Data.PlayerPermissions != PlayerPermissionLevel.Member)
			{
				return false;
			}
			if (!IsOperatorCommandLevel(packet.Data.CommandPermissions))
			{
				return false;
			}
			packet.Data.PlayerPermissions = PlayerPermissionLevel.Operator;
			// The send path transmits a decoded packet's original wire bytes unless the cache is
			// cleared; without this the correction silently never reaches the client.
			packet.InvalidateWireCache();
			return true;
		}

		private static bool IsOperatorCommandLevel(CommandPermissionLevel permission)
		{
			return permission == CommandPermissionLevel.Admin
				|| permission == CommandPermissionLevel.Host
				|| permission == CommandPermissionLevel.Owner;
		}
	}
}
