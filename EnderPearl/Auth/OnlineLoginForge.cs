using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Protocol.Packets;

namespace EnderPearl.Auth;

/// <summary>
/// Builds the online login the proxy sends to the backend on the player's behalf.
///
/// <para>Backend joins always present an online (Mojang-mimicked) identity: the OIDC multiplayer
/// token is RS256-signed by the proxy's persistent <see cref="MojangMimicIdentity"/> and sent as
/// AuthenticationType FULL, so an online-mode backend accepts it. The offline/self-signed posture
/// was removed — <c>Program.Main</c> always loads a mimic identity, so it was never reached.</para>
/// </summary>
public sealed class OnlineLoginForge
{
	/// <summary>
	/// Forges the online login for the backend, and returns the OIDC multiplayer token the NetherNet
	/// identity assertion must carry. A backend in online mode rejects an offer whose identity it
	/// cannot verify, so the assertion has to carry the exact token the Login packet does.
	/// </summary>
	public LoginPacket Forge(
		ECDsaHolder keyPair,
		ClientLogin clientLogin,
		string? minecraftVersion,
		string? serverAddress,
		MojangMimicIdentity mimic,
		out string oidcToken
	)
	{
		oidcToken = ForgeMimicToken(mimic, keyPair, clientLogin.AuthData, clientLogin.SkinData);

		return new LoginPacket
		{
			ClientNetworkVersion = (int)global::Protocol.ProtocolVersion.VERSION,
			// Mimic mode mirrors a genuine FULL login: AuthenticationType + Token only, NO
			// Certificate field - an online-mode backend validates any certificate chain against
			// Mojang's root and rejects ours outright (NotAuthenticated).
			ConnectionRequest = new LoginConnectionRequest
			{
				AuthPayload = new JsonObject
				{
					["AuthenticationType"] = (int)LoginConnectionRequest.AuthenticationType.FULL,
					["Token"] = oidcToken
				},
				SkinJwt = ForgeSkinData(keyPair, clientLogin.SkinData, clientLogin.AuthData, minecraftVersion, serverAddress)
			}.Encode()
		};
	}

	/// <summary>
	/// Signs the OIDC token with the mimic identity. Mirrors the captured genuine franchise-token
	/// payload byte-for-byte (same claim set, same exp window) so no environment/shape check can tell
	/// it apart, then signs RS256 under the mimic <c>kid</c>. Falls back to the constructed claim set
	/// only if nothing was ever captured.
	/// </summary>
	private static string ForgeMimicToken(
		MojangMimicIdentity mimic,
		ECDsaHolder keyPair,
		AuthData authData,
		JsonObject skinData)
	{
		string? payloadJson = MojangMimicIdentity.GenuinePayloadJson;
		if (payloadJson != null)
		{
			// 模板是进程里第一个完成认证的玩家的正版令牌：除 cpk 外，所有身份声明也必须改写
			// 成本次登录者，否则每个玩家都以同一个 Xbox 身份（相同 xid/xname）进入后端，
			// 第二名玩家连上同一台 BDS 时会被以 ServerIdConflict(44) 拒绝。
			if (JsonNode.Parse(payloadJson) is JsonObject node)
			{
				StampPlayerIdentity(node, keyPair, authData, skinData);
				payloadJson = node.ToJsonString();
			}
			return JwtHelper.EncodeRs256(payloadJson, mimic.Rsa, mimic.Kid);
		}

		JsonObject payloadNode = ClaimsObject(BuildOidcClaims(keyPair, authData, skinData));
		return JwtHelper.EncodeRs256(payloadNode.ToJsonString(), mimic.Rsa, mimic.Kid);
	}

	/// <summary>
	/// 把捕获模板（第一个玩家的正版令牌载荷）中的每玩家声明改写成本次登录者的值。字段集与
	/// <see cref="BuildOidcClaims"/> 保持一致：cpk 签名公钥、xid/xname/identity Xbox 身份、
	/// leguuid/mid 的 XUID 派生值；时间窗整体平移到现在、保持模板原有的时长与形状。
	/// </summary>
	private static void StampPlayerIdentity(
		JsonObject node,
		ECDsaHolder keyPair,
		AuthData authData,
		JsonObject skinData)
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

	private static long? AsEpoch(JsonNode? node)
	{
		return node is JsonValue value
			&& value.TryGetValue<long>(out long epoch)
			? epoch
			: null;
	}

	private static Dictionary<string, object> BuildOidcClaims(ECDsaHolder keyPair, AuthData authData, JsonObject skinData)
	{
		long timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
		long notBefore = timestamp - TimeSpan.FromHours(6).TotalMilliseconds > long.MaxValue
			? 0 : (long)(timestamp - TimeSpan.FromHours(6).TotalMilliseconds);
		long expires = (long)(timestamp + TimeSpan.FromHours(6).TotalMilliseconds);
		string legacyUuid = DeterministicUuid("pocket-auth-1-xuid:" + authData.Xuid).ToString();

		return new Dictionary<string, object>
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
	}

	private static JsonObject ClaimsObject(Dictionary<string, object> claims)
	{
		var node = new JsonObject();
		foreach (KeyValuePair<string, object> claim in claims)
		{
			node[claim.Key] = claim.Value switch
			{
				string s => s,
				long l => l,
				int i => i,
				bool b => b,
				_ => claim.Value?.ToString() ?? ""
			};
		}
		return node;
	}

	private static string PlayFabId(JsonObject? skinData, string xuid)
	{
		if (skinData != null
			&& skinData.TryGetPropertyValue("PlayFabId", out JsonNode? playFabNode)
			&& playFabNode is JsonValue playFabValue
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
		JsonObject skinData,
		AuthData authData,
		string? minecraftVersion,
		string? serverAddress
	)
	{
		JsonObject backendSkinData = (JsonObject)skinData.DeepClone();

		// Only fill it in when the client left it blank: a client that supplies its own self-signed
		// id is already telling the backend who it is.
		bool needsSelfSignedId = !backendSkinData.TryGetPropertyValue("SelfSignedId", out JsonNode? existingId)
			|| existingId is not JsonValue idValue
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
