using System;
using System.Collections.Generic;
using System.Net;
using System.Text.Json.Nodes;

namespace EnderPearl.Auth
{
	/// <summary>The authenticated identity of one player, extracted from the Mojang-signed login chain.</summary>
	public sealed class AuthData
	{
		public string DisplayName { get; }

		public Guid Identity { get; }

		public string Xuid { get; }

		public AuthData(string displayName, Guid identity, string xuid)
		{
			if (string.IsNullOrWhiteSpace(displayName))
			{
				throw new ArgumentException("displayName cannot be blank");
			}
			if (string.IsNullOrWhiteSpace(xuid))
			{
				throw new ArgumentException("xuid cannot be blank");
			}
			DisplayName = displayName;
			Identity = identity;
			Xuid = xuid;
		}

		public override string ToString() => DisplayName + " (" + Identity + ", xuid " + Xuid + ")";
	}

	/// <summary>
	/// A completed client authentication: who the player is, their skin JWT claims, the public key
	/// their client-data JWT was signed with, and (for bridged players) the address the bridge stamped
	/// into the login.
	///
	/// <p>Bridged players all share one loopback source address, so <see cref="BridgeClientAddress"/> is
	/// the only place their real address exists.</p>
	/// </summary>
	public sealed class ClientLogin
	{
		public AuthData AuthData { get; }

		/// <summary>The decoded client-data JWT claims, mutable so forge can copy and amend them.</summary>
		public JsonObject SkinData { get; }

		public byte[] IdentityPublicKey { get; }

		/// <summary>The player's real address when they arrived through a bridged edition bridge, or null.</summary>
		public IPEndPoint? BridgeClientAddress { get; }

		public ClientLogin(AuthData authData, JsonObject skinData, byte[] identityPublicKey, IPEndPoint? bridgeClientAddress)
		{
			AuthData = authData ?? throw new ArgumentNullException(nameof(authData));
			SkinData = skinData ?? throw new ArgumentNullException(nameof(skinData));
			if (identityPublicKey == null || identityPublicKey.Length == 0)
			{
				throw new ArgumentNullException(nameof(identityPublicKey));
			}
			IdentityPublicKey = identityPublicKey;
			BridgeClientAddress = bridgeClientAddress;
		}

		/// <summary>True when this player reached the proxy through a bridged edition bridge.</summary>
		public bool IsJavaEdition() => BridgeClientAddress != null;
	}
}
