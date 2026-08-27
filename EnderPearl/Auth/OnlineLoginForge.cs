using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using EnderPearl.Crypto;
using Protocol.Packets;

namespace EnderPearl.Auth
{
	/// <summary>
	/// Builds the login the proxy sends to the backend on the player's behalf.
	///
	/// <p>Bedrock 1.26.10+ (protocol 944+) servers expect the modern OIDC multiplayer token format: a
	/// self-signed token carrying cpk/xid/xname/tid in the Token field, which is what survives the
	/// discovery-environment check that rejects a legacy extraData chain JWT lacking the PlayFab
	/// <c>tid</c> claim (see gophertunnel's login.EncodeOffline(legacy=false)). This build only speaks
	/// to 1.26.40 backends (protocol 2168), so the OIDC form is always used.</p>
	/// <p>Named "online" because the default posture mimics a genuine Microsoft franchise login
	/// (RS256 under the proxy's kid, AuthenticationType FULL) via 末影之眼; the self-signed shape
	/// remains only as the mimic=null fallback.</p>
	/// </summary>
	public sealed class OnlineLoginForge
	{
		public const LoginConnectionRequest.AuthenticationType DEFAULT_AUTH_TYPE = LoginConnectionRequest.AuthenticationType.SELF_SIGNED;

		public LoginPacket Forge(ECDsaHolder keyPair, ClientLogin clientLogin)
		{
			return Forge(keyPair, clientLogin, null, null);
		}

		/// <summary>
		/// Forges the modern self-signed login for one backend join - Java's forgeOidcLogin. Sends the
		/// OIDC multiplayer token in the Token field for the 1.26.10+ discovery-environment check, while
		/// keeping a legacy certificate in the Certificate field so backends that still source
		/// Player.xuid from extraData.XUID can populate it.
		/// </summary>
		/// <param name="keyPair">the per-player proxy key pair</param>
		/// <param name="clientLogin">the authenticated client identity</param>
		/// <param name="minecraftVersion">advertised as GameVersion in the skin data when set</param>
		/// <param name="serverAddress">stamped into the skin data's ServerAddress when set</param>
		public LoginPacket Forge(
			ECDsaHolder keyPair,
			ClientLogin clientLogin,
			string? minecraftVersion,
			string? serverAddress
		)
		{
			return Forge(keyPair, clientLogin, minecraftVersion, serverAddress, null);
		}

		/// <summary>
		/// When <paramref name="mimic"/> is provided the OIDC token is RS256-signed by the proxy's
		/// persistent identity with its <c>kid</c> and sent as AuthenticationType FULL - the exact
		/// shape of a genuine Microsoft franchise token. Verifiers resolve the kid against the JWKS
		/// served by 末影之眼 at the intercepted authorization.franchise.minecraft-services.net URL,
		/// so no self-signed/offline posture remains.
		/// </summary>
		public LoginPacket Forge(
			ECDsaHolder keyPair,
			ClientLogin clientLogin,
			string? minecraftVersion,
			string? serverAddress,
			MojangMimicIdentity? mimic
		)
		{
			string oidcToken = mimic != null
				? ForgeMimicToken(mimic, keyPair, clientLogin.AuthData, clientLogin.SkinData)
				: ForgeOidcToken(keyPair, clientLogin.AuthData, clientLogin.SkinData);
			var authType = mimic != null
				? LoginConnectionRequest.AuthenticationType.FULL
				: DEFAULT_AUTH_TYPE;

			var login = new LoginPacket();
			login.ClientNetworkVersion = (int)global::Protocol.ProtocolVersion.VERSION;
			if (mimic != null)
			{
				// Mimic mode mirrors a genuine FULL login: AuthenticationType + Token only, NO
				// Certificate field - an online-mode backend validates any certificate chain against
				// Mojang's root and rejects ours outright (NotAuthenticated).
				login.ConnectionRequest = new LoginConnectionRequest
				{
					AuthPayload = new System.Text.Json.Nodes.JsonObject
					{
						["AuthenticationType"] = (int)authType,
						["Token"] = oidcToken
					},
					SkinJwt = ForgeSkinData(keyPair, clientLogin.SkinData, clientLogin.AuthData, minecraftVersion, serverAddress)
				}.Encode();
				return login;
			}

			string chainJwt = ForgeAuthData(keyPair, clientLogin.AuthData);
			login.ConnectionRequest = new LoginConnectionRequest
			{
				AuthPayload = DualPayload(authType, chainJwt, oidcToken),
				SkinJwt = ForgeSkinData(keyPair, clientLogin.SkinData, clientLogin.AuthData, minecraftVersion, serverAddress)
			}.Encode();
			return login;
		}

		/// <summary>
		/// Java's DualPayload: both auth carriers in one payload. The Certificate member keeps the
		/// v818 serializer's shape - a JSON string holding <c>{"chain":[jwt]}</c> - with the OIDC
		/// token beside it.
		/// </summary>
		private static System.Text.Json.Nodes.JsonObject DualPayload(
			LoginConnectionRequest.AuthenticationType type,
			string chainJwt,
			string oidcToken
		)
		{
			return new System.Text.Json.Nodes.JsonObject
			{
				["AuthenticationType"] = (int)type,
				["Certificate"] = new System.Text.Json.Nodes.JsonObject
				{
					["chain"] = new System.Text.Json.Nodes.JsonArray { chainJwt }
				}.ToJsonString(),
				["Token"] = oidcToken
			};
		}

		/// <summary>The port of forgeAuthData: the legacy extraData JWT inside the Certificate chain.</summary>
		private static string ForgeAuthData(ECDsaHolder keyPair, AuthData authData)
		{
			long timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
			string publicKey = keyPair.PublicKeyBase64();
			var claims = new Dictionary<string, object>
			{
				["nbf"] = timestamp - TimeSpan.FromSeconds(1).TotalMilliseconds,
				["exp"] = timestamp + TimeSpan.FromDays(1).TotalMilliseconds,
				["iat"] = timestamp,
				["iss"] = "Mojang",
				["extraData"] = new Dictionary<string, object>
				{
					["XUID"] = authData.Xuid,
					["identity"] = authData.Identity.ToString(),
					["displayName"] = authData.DisplayName,
					["titleId"] = "1739947436",
					["sandboxId"] = "RETAIL"
				},
				["identityPublicKey"] = publicKey,
				["randomNonce"] = Random.Shared.NextInt64()
			};

			var payloadJson = new System.Text.Json.Nodes.JsonObject();
			foreach (KeyValuePair<string, object> claim in claims)
			{
				payloadJson[claim.Key] = claim.Value switch
				{
					string s => s,
					long l => l,
					double d => d,
					Dictionary<string, object> nested => NestedJsonObject(nested),
					_ => claim.Value?.ToString() ?? ""
				};
			}
			return JwtHelper.EncodeEs384(payloadJson.ToJsonString(), keyPair.Signer, publicKey);
		}

		private static System.Text.Json.Nodes.JsonObject NestedJsonObject(Dictionary<string, object> values)
		{
			var node = new System.Text.Json.Nodes.JsonObject();
			foreach (KeyValuePair<string, object> entry in values)
			{
				node[entry.Key] = entry.Value switch
				{
					string s => s,
					long l => l,
					int i => i,
					_ => entry.Value?.ToString() ?? ""
				};
			}
			return node;
		}

		/// <summary>
		/// The OIDC multiplayer token claims expected by 1.26.10+ servers; names follow Microsoft's
		/// franchise multiplayer token (gophertunnel's tokenClaims).
		/// </summary>
		private static string ForgeOidcToken(ECDsaHolder keyPair, AuthData authData, System.Text.Json.Nodes.JsonObject skinData)
		{
			Dictionary<string, object> claims = BuildOidcClaims(keyPair, authData, skinData);

			var payloadJson = new System.Text.Json.Nodes.JsonObject();
			foreach (KeyValuePair<string, object> claim in claims)
			{
				payloadJson[claim.Key] = claim.Value switch
				{
					string s => s,
					long l => l,
					int i => i,
					bool b => b,
					_ => claim.Value?.ToString() ?? ""
				};
			}
			return JwtHelper.EncodeEs384(payloadJson.ToJsonString(), keyPair.Signer, keyPair.PublicKeyBase64());
		}

		private static string ForgeMimicToken(
			MojangMimicIdentity mimic,
			ECDsaHolder keyPair,
			AuthData authData,
			System.Text.Json.Nodes.JsonObject skinData)
		{
			// Mirror the player's genuine franchise-token payload byte-for-byte (same claim set, same
			// exp window) so no environment/shape check can tell it apart, then sign RS256 under our
			// kid. Falls back to the constructed claim set only if nothing was ever captured.
			string? payloadJson = MojangMimicIdentity.GenuinePayloadJson;
			if (payloadJson != null)
			{
				// 模板是进程里第一个完成认证的玩家的正版令牌：除 cpk 外，所有身份声明也必须改写
				// 成本次登录者，否则每个玩家都以同一个 Xbox 身份（相同 xid/xname）进入后端，
				// 第二名玩家连上同一台 BDS 时会被以 ServerIdConflict(44) 拒绝。
				if (System.Text.Json.Nodes.JsonNode.Parse(payloadJson) is System.Text.Json.Nodes.JsonObject node)
				{
					StampPlayerIdentity(node, keyPair, authData, skinData);
					payloadJson = node.ToJsonString();
				}
				return JwtHelper.EncodeRs256(payloadJson, mimic.Rsa, mimic.Kid);
			}

			Dictionary<string, object> claims = BuildOidcClaims(keyPair, authData, skinData);
			var payloadNode = new System.Text.Json.Nodes.JsonObject();
			foreach (KeyValuePair<string, object> claim in claims)
			{
				payloadNode[claim.Key] = claim.Value switch
				{
					string s => s,
					long l => l,
					int i => i,
					bool b => b,
					_ => claim.Value?.ToString() ?? ""
				};
			}
			return JwtHelper.EncodeRs256(payloadNode.ToJsonString(), mimic.Rsa, mimic.Kid);
		}

		/// <summary>
		/// 把捕获模板（第一个玩家的正版令牌载荷）中的每玩家声明改写成本次登录者的值。字段集与
		/// <see cref="BuildOidcClaims"/> 保持一致：cpk 签名公钥、xid/xname/identity Xbox 身份、
		/// leguuid/mid 的 XUID 派生值；时间窗整体平移到现在、保持模板原有的时长与形状。
		/// </summary>
		private static void StampPlayerIdentity(
			System.Text.Json.Nodes.JsonObject node,
			ECDsaHolder keyPair,
			AuthData authData,
			System.Text.Json.Nodes.JsonObject skinData)
		{
			node["cpk"] = keyPair.PublicKeyBase64();
			node["xid"] = authData.Xuid;
			node["xname"] = authData.DisplayName;
			node["identity"] = authData.Identity.ToString();
			node["leguuid"] = DeterministicUuid("pocket-auth-1-xuid:" + authData.Xuid).ToString();
			node["mid"] = PlayFabId(skinData, authData.Xuid);

			long? iat = AsEpoch(node["iat"]);
			long? exp = AsEpoch(node["exp"]);
			if (iat != null && exp != null && exp > iat)
			{
				// 秒级时间戳 (<10^12) 与毫秒级并存，按量级判定后平移，避免跨单位错算。
				bool milliseconds = iat > 1_000_000_000_000;
				long now = milliseconds
					? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
					: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000;
				long shift = now - iat.Value;
				node["iat"] = now;
				node["exp"] = exp.Value + shift;
				if (AsEpoch(node["nbf"]) is long nbf)
				{
					node["nbf"] = nbf + shift;
				}
			}
		}

		private static long? AsEpoch(System.Text.Json.Nodes.JsonNode? node)
		{
			return node is System.Text.Json.Nodes.JsonValue value
				&& value.TryGetValue<long>(out long epoch)
				? epoch
				: null;
		}

		private static Dictionary<string, object> BuildOidcClaims(ECDsaHolder keyPair, AuthData authData, System.Text.Json.Nodes.JsonObject skinData)
		{
			long timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
			long notBefore = timestamp - TimeSpan.FromHours(6).TotalMilliseconds > long.MaxValue
				? 0 : (long)(timestamp - TimeSpan.FromHours(6).TotalMilliseconds);
			long expires = (long)(timestamp + TimeSpan.FromHours(6).TotalMilliseconds);
			string legacyUuid = DeterministicUuid("pocket-auth-1-xuid:" + authData.Xuid).ToString();

			var claims = new Dictionary<string, object>
			{
				["nbf"] = notBefore,
				["exp"] = expires,
				["iat"] = timestamp,
				["cpk"] = keyPair.PublicKeyBase64(),
				["leguuid"] = legacyUuid,
				// Java's playFabId(skinData, xuid) prefers a non-blank PlayFabId the client itself
				// reported, and only falls back to the xuid-derived value.
				["mid"] = PlayFabId(skinData, authData.Xuid),
				["nid"] = "",
				["nname"] = "",
				["pid"] = "",
				["pname"] = "",
				["xid"] = authData.Xuid,
				["xname"] = authData.DisplayName,
				["identity"] = authData.Identity.ToString(),
				["ipt"] = "PlayFab",
				["tid"] = "20CA2"
			};
			return claims;
		}

		private static string PlayFabId(System.Text.Json.Nodes.JsonObject? skinData, string xuid)
		{
			if (skinData != null
				&& skinData.TryGetPropertyValue("PlayFabId", out System.Text.Json.Nodes.JsonNode? playFabNode)
				&& playFabNode is System.Text.Json.Nodes.JsonValue playFabValue
				&& playFabValue.TryGetValue<string>(out string? playFabId)
				&& !string.IsNullOrWhiteSpace(playFabId))
			{
				return playFabId;
			}
			if (System.Numerics.BigInteger.TryParse(xuid, out System.Numerics.BigInteger value))
			{
				return value.ToString("X");
			}
			unchecked
			{
				return xuid.GetHashCode().ToString("X");
			}
		}

		/// <summary>
		/// Re-signs the client's own skin-data claims with the proxy key, filling in the stable
		/// self-signed id and the routing metadata a backend expects.
		/// </summary>
		private static string ForgeSkinData(
			ECDsaHolder keyPair,
			System.Text.Json.Nodes.JsonObject skinData,
			AuthData authData,
			string? minecraftVersion,
			string? serverAddress
		)
		{
			System.Text.Json.Nodes.JsonObject backendSkinData =
				(System.Text.Json.Nodes.JsonObject)skinData.DeepClone();

			// Only fill it in when the client left it blank: a client that supplies its own self-signed
			// id is already telling the backend who it is.
			bool needsSelfSignedId = !backendSkinData.TryGetPropertyValue("SelfSignedId", out System.Text.Json.Nodes.JsonNode? existingId)
				|| existingId is not System.Text.Json.Nodes.JsonValue idValue
				|| !idValue.TryGetValue<string>(out string? idString)
				|| string.IsNullOrWhiteSpace(idString);
			if (needsSelfSignedId)
			{
				backendSkinData["SelfSignedId"] = StableSelfSignedId(authData);
			}

			if (!string.IsNullOrWhiteSpace(serverAddress))
			{
				backendSkinData["ServerAddress"] = serverAddress;
			}
			if (!string.IsNullOrWhiteSpace(minecraftVersion))
			{
				backendSkinData["GameVersion"] = minecraftVersion;
			}
			return JwtHelper.EncodeEs384(backendSkinData.ToJsonString(), keyPair.Signer, keyPair.PublicKeyBase64());
		}

		/// <summary>
		/// A stable offline identity for the backend, derived from the player's XUID so it is identical
		/// on every join and across every backend - this is what makes a proxied player a returning
		/// player rather than a new one.
		/// </summary>
		private static string StableSelfSignedId(AuthData authData)
		{
			return DeterministicUuid("endstone-proxy-self-signed:" + authData.Xuid).ToString();
		}

		/// <summary>
		/// The port of UUID.nameUUIDFromBytes (MD5, RFC 4122 version 3). Java's toString() renders the
		/// 16 digest bytes as big-endian hex groups; <c>new Guid(byte[])</c> little-endians the first
		/// three groups, so feed them reversed to make the string form byte-for-byte identical to the
		/// Java value (same conversion as UuidCodec.ToGuid).
		/// </summary>
		public static Guid DeterministicUuid(string name)
		{
			byte[] hash = MD5.HashData(Encoding.UTF8.GetBytes(name));
			hash[6] = (byte)((hash[6] & 0x0f) | 0x30);
			hash[8] = (byte)((hash[8] & 0x3f) | 0x80);
			byte[] swapped = new byte[16];
			Array.Copy(hash, swapped, 16);
			Array.Reverse(swapped, 0, 4);
			Array.Reverse(swapped, 4, 2);
			Array.Reverse(swapped, 6, 2);
			return new Guid(swapped);
		}
	}
}
