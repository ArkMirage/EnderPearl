using System;
using System.Collections.Generic;
using System.Net;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using EnderPearl.Crypto;
using EnderPearl.Net;
using global::Protocol.Packets;
using EnderPearl.Logging;

namespace EnderPearl.Auth
{
	/// <summary>
	/// Authenticates an incoming client login: validates the Mojang signature on the certificate chain,
	/// verifies the client-data JWT against the chain's identity key, and extracts the player's identity.
	///
	/// <p>Self-signed logins are an authentication bypass and are always rejected.</p>
	/// </summary>
	public sealed class ClientLoginAuthenticator
	{
		private readonly bool requireXuid;

		public ClientLoginAuthenticator(bool requireXuid)
		{
			this.requireXuid = requireXuid;
		}

		public ClientLogin Authenticate(LoginPacket packet)
		{
			try
			{
				LoginConnectionRequest request = LoginConnectionRequest.Decode(packet.ConnectionRequest);
				(string displayName, string xuid, string identityRaw, byte[] identityKey, JsonNode rawClaims, bool signed) =
					ExtractIdentity(request);

				if (!signed)
				{
					throw new InvalidOperationException("Client login chain is not Mojang-signed");
				}

				// The skin JWT must be signed by the same key that owns the identity: the chain's
				// identityPublicKey for a Mojang login, or the self-signed token signer otherwise.
				if (!BedrockAuthChain.VerifyWithKey(request.SkinJwt, identityKey))
				{
					throw new InvalidOperationException("Client data signature does not match login identity key");
				}
				var skinData = JsonNode.Parse(JwtHelper.DecodePayload(request.SkinJwt)) as JsonObject
					?? throw new InvalidOperationException("Client data payload is not a JSON object");
				if (requireXuid && string.IsNullOrWhiteSpace(xuid))
				{
					throw new InvalidOperationException(
						$"Mojang-signed login chain for {displayName} carries no XUID");
				}
				string effectiveXuid = NonBlank(xuid, "0");
				Guid identity = Guid.TryParse(identityRaw, out Guid parsed) && parsed != Guid.Empty
					? parsed
					: OnlineLoginForge.DeterministicUuid("pocket-auth-1-xuid:" + effectiveXuid);

				string effectiveName = NonBlank(displayName, "Player");

				return new ClientLogin(
					new AuthData(effectiveName, identity, effectiveXuid),
					skinData,
					identityKey,
					null
				);
			}
			catch (Exception exception)
			{
				if (exception is not InvalidOperationException)
				{
					throw new InvalidOperationException("Unable to authenticate Bedrock login", exception);
				}
				// One self-contained diagnostic line: what the client actually sent, so an unexpected
				// auth format can be identified without a packet capture.
				try
				{
					var requestDump = LoginConnectionRequest.Decode(packet.ConnectionRequest);
					Logger.Error(
						$"Login authentication failed ({exception.Message}); payload shape: {Describe(requestDump)}");
				}
				catch (Exception dumpFailure)
				{
					Logger.Error(
						$"Login authentication failed ({exception.Message}); payload could not be re-decoded for diagnostics: {dumpFailure.Message}");
				}
				throw;
			}
		}

		private static string Describe(LoginConnectionRequest request)
		{
			switch (request.AuthPayload)
			{
				case JsonArray array:
					var parts = new List<string>();
					foreach (JsonNode? item in array)
					{
						string token = item?.GetValue<string>() ?? "";
						parts.Add("jwt(headers=" + DescribeHeaders(token) + ",payload=" + Snip(JwtHelper.DecodePayload(token)) + ")");
					}
					return "array[" + string.Join(", ", parts) + "], skin=" + Snip(JwtHelper.DecodePayload(request.SkinJwt));
				case JsonObject obj:
					var fields = new List<string>();
					foreach (KeyValuePair<string, JsonNode?> field in obj)
					{
						fields.Add(field.Key + "=" + (field.Value is JsonValue v && v.TryGetValue<string>(out string? s)
							? Snip(s)
							: field.Value?.ToJsonString() ?? "null"));
					}
					return "object{" + string.Join(", ", fields) + "}";
				default:
					return request.AuthPayload == null ? "null" : request.AuthPayload.ToJsonString();
			}
		}

		private static string DescribeHeaders(string token)
		{
			try
			{
				return "{" + string.Join(",", JwtHelper.DecodeHeaders(token).Keys) + "}";
			}
			catch (Exception)
			{
				return "?";
			}
		}

		private static string Snip(string value)
		{
			const int max = 120;
			string oneLine = value.Replace("\n", "");
			return oneLine.Length <= max ? oneLine : oneLine[..max] + "...";
		}

		private (
			string DisplayName,
			string Xuid,
			string Identity,
			byte[] IdentityKey,
			JsonNode RawClaims,
			bool Signed) ExtractIdentity(LoginConnectionRequest request)
		{
			IReadOnlyList<string> legacyChain = request.LegacyChain();
			if (legacyChain.Count > 0)
			{
				BedrockAuthChain.ValidationResult result = BedrockAuthChain.Validate(legacyChain);
				return (result.DisplayName, result.Xuid, result.IdentityRaw, result.IdentityPublicKey,
					(JsonNode)JsonSerializerNode.ToNode(result.RawIdentityClaims), result.Signed);
			}

			var modern = request.ModernFields();
			string? token = modern.Token;
			JsonNode? certificate = modern.Certificate;

			// A modern payload may still carry a Certificate for extraData sourcing; prefer validating it.
			if (certificate != null)
			{
				List<string> tokens = certificate is JsonArray certArray
					? certArray.Where(n => n != null).Select(n => n!.GetValue<string>()).ToList()
					: certificate is JsonValue certValue && certValue.TryGetValue<string>(out string? certString)
						? ParseCertificateChain(certString)
						: new List<string>();
				if (tokens.Count > 0)
				{
					BedrockAuthChain.ValidationResult result = BedrockAuthChain.Validate(tokens);
					return (result.DisplayName, result.Xuid, result.IdentityRaw, result.IdentityPublicKey,
						(JsonNode)JsonSerializerNode.ToNode(result.RawIdentityClaims), result.Signed);
				}
			}

			// Modern token-only payload. Java's validateToken switches on the AuthenticationType
			// discriminator: FULL(0)/GUEST(1) go to the online Mojang consumer - the Microsoft/PlayFab
			// multiplayer identity token introduced with 1.26.10, whose claims are trusted as issued
			// here because an offline proxy cannot hold those keys (signed=true, same posture) -
			// while SELF_SIGNED(2) goes to the offline consumer and only counts as signed when it is
			// actually verifiable. The alg-header sniff below only runs when a legacy payload left
			// the discriminator out.
			if (string.IsNullOrEmpty(token))
			{
				throw new InvalidOperationException("Login carries neither a chain nor a Token");
			}
			string alg = JwtHelper.DecodeHeaders(token).TryGetValue("alg", out JsonElement algEl)
				? algEl.GetString() ?? ""
				: "";
			bool microsoftIssued = modern.Type is 0 or 1
				|| (modern.Type == null
					&& (alg.Equals("RS256", StringComparison.OrdinalIgnoreCase)
						|| alg.Equals("PS256", StringComparison.OrdinalIgnoreCase)));

			MojangMimicIdentity.CaptureGenuine(token);
			string payloadJson = JwtHelper.DecodePayload(token);
			var claims = JsonNode.Parse(payloadJson) as JsonObject
				?? throw new InvalidOperationException("Token payload is not a JSON object");
			string name = FirstClaim(claims, "xname", "displayName");
			string xuid = FirstClaim(claims, "xid", "XUID");
			// Java's createClaims always derives the identity UUID from the xuid
			// ("pocket-auth-1-xuid:" + xuid); it never reads an identity claim off this token type.
			// Returning "" routes through Authenticate's derived-UUID fallback, which computes exactly
			// that value - reading a client-supplied identity here would diverge from it.
			string identityRaw = "";

			byte[] key;
			string? x5u = BedrockAuthChain.TryHeaderX5U(token);
			if (x5u != null)
			{
				key = Convert.FromBase64String(x5u);
			}
			else if (claims.TryGetPropertyValue("cpk", out JsonNode? cpkNode) && cpkNode != null)
			{
				// The token carries its own public key claim; that is what the skin JWT is signed with.
				key = Convert.FromBase64String(cpkNode.GetValue<string>());
			}
			else if (!microsoftIssued)
			{
				throw new InvalidOperationException(
					"Token carries neither an x5u header nor a cpk claim to authenticate its skin data against");
			}
			else
			{
				key = Array.Empty<byte>();
			}

			bool signed = microsoftIssued;
			if (signed && (name.Length == 0 || xuid.Length == 0))
			{
				// A Microsoft token without usable identity claims cannot be routed or deduplicated;
				// dump the claim keys so the format can be identified from the log alone.
				throw new InvalidOperationException(
					"Microsoft identity token lacks name/xuid claims (found: "
					+ string.Join(",", claims.Select(p => p.Key)) + ")");
			}
			// Both Java token consumers demanded an exp claim (setRequireExpirationTime): the online
			// consumer for FULL/GUEST tokens enforced freshness, while the offline consumer for
			// SELF_SIGNED ones only required its presence. Mirror that split: exp is mandatory, and
			// for Microsoft tokens it must additionally be in the future (60s clock-skew slack) -
			// the one piece of their validation reproducible without the PlayFab keys.
			if (!claims.TryGetPropertyValue("exp", out JsonNode? expNode)
				|| expNode is not JsonValue expValue
				|| !expValue.TryGetValue<double>(out double expSeconds))
			{
				throw new InvalidOperationException(
					(signed ? "Microsoft identity token" : "Self-signed identity token")
					+ " carries no usable exp claim");
			}
			if (signed && DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0 > expSeconds + 60)
			{
				throw new InvalidOperationException("Microsoft identity token has expired");
			}
			return (name, xuid, identityRaw, key, claims, signed);
		}

		private static List<string> ParseCertificateChain(string json)
		{
			try
			{
				var node = JsonNode.Parse(json);
				if (node is JsonArray array)
				{
					return array.Where(n => n != null).Select(n => n!.GetValue<string>()).ToList();
				}
				if (node is JsonObject obj && obj.TryGetPropertyValue("chain", out JsonNode? chainNode) && chainNode is JsonArray chainArray)
				{
					return chainArray.Where(n => n != null).Select(n => n!.GetValue<string>()).ToList();
				}
			}
			catch (System.Text.Json.JsonException)
			{
				// Fall through to empty.
			}
			return new List<string>();
		}

		private static string NonBlank(string value, string fallback)
		{
			return string.IsNullOrWhiteSpace(value) ? fallback : value;
		}

		private static string FirstClaim(JsonObject claims, params string[] names)
		{
			foreach (string name in names)
			{
				if (claims.TryGetPropertyValue(name, out JsonNode? node) && node != null)
				{
					string value = node.GetValue<string>();
					if (!string.IsNullOrWhiteSpace(value))
					{
						return value;
					}
				}
			}
			return "";
		}
	}

	/// <summary>Tiny adapter from a read-only JsonElement to a mutable JsonNode.</summary>
	internal static class JsonSerializerNode
	{
		public static JsonNode ToNode(System.Text.Json.JsonElement element)
		{
			return JsonNode.Parse(element.GetRawText()) ?? new JsonObject();
		}
	}
}
