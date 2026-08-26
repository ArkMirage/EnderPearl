using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text.Json;
using EnderPearl.Crypto;

namespace EnderPearl.Crypto
{
	/// <summary>
	/// Validation of the Bedrock login certificate chain - the port of Cloudburst's
	/// EncryptionUtils.validateChain / ChainValidationResult.createLegacyClaims.
	///
	/// <p>A chain is a list of JWTs and must be exactly one or three links long. A three-link chain
	/// is Mojang-signed when the second link verifies against Mojang's well-known ECDSA P-384 key;
	/// each link must be signed by the identityPublicKey declared by the previous link (not merely by
	/// that link's own x5u header), and every link must use ES384. The last link's payload carries
	/// the player's identity claims (extraData) and the identityPublicKey the client-data JWT must be
	/// signed with.</p>
	///
	/// <p>A single-link chain is "self-signed" (never Mojang-signed): rejected on every listener.</p>
	/// </summary>
	public static class BedrockAuthChain
	{
		/// <summary>Mojang's well-known login-chain signing key (X.509 SubjectPublicKeyInfo, base64).</summary>
		/// <remarks>The rotated key the live protocol library ships; the pre-rotation key made every
		/// genuine retail chain validate as self-signed.</remarks>
		public const string MojangPublicKeyBase64 =
			"MHYwEAYHKoZIzj0CAQYFK4EEACIDYgAECRXueJeTDqNRRgJi/vlRufByu/2G0i2Ebt6YMar5QX/R0DIIyrJMcUpruK4QveTfJSTp3Shlq4Gk34cD/4GUWwkv0DVuzeuB+tXija7HBxii03NHDbPAD0AKnLr2wdAp";

		private static readonly byte[] MojangPublicKeyBytes = ParseKey(MojangPublicKeyBase64);

		public sealed record ValidationResult(
			bool Signed,
			string DisplayName,
			string Xuid,
			string IdentityRaw,
			byte[] IdentityPublicKey,
			JsonElement RawIdentityClaims)
		{
		}

		/// <summary>
		/// Validates a decoded login chain. Throws when the chain itself is malformed (no links, wrong
		/// length, broken signatures inside the chain, missing extraData); a merely self-signed chain
		/// returns with Signed=false rather than throwing, so the caller decides whether to accept it.
		/// </summary>
		public static ValidationResult Validate(IReadOnlyList<string> chainTokens)
		{
			switch (chainTokens?.Count ?? 0)
			{
				case 1:
				{
					// Offline / proxied single-token chain. Java validates no signature at all on this
					// path; it is never Mojang-signed.
					return LegacyClaims(false, ParsePayloadElement(chainTokens[0]));
				}
				case 3:
				{
					byte[]? currentKey = null;
					JsonElement parsedPayload = default;
					for (int i = 0; i < 3; i++)
					{
						string token = chainTokens![i];
						// jose4j ran each link under an ES384-only algorithm constraint.
						RequireAlgorithm(token, "ES384");
						byte[] expectedKey = ParseKey(HeaderX5U(token));
						if (currentKey == null)
						{
							currentKey = expectedKey;
						}
						else if (!currentKey.AsSpan().SequenceEqual(expectedKey))
						{
							throw new InvalidOperationException("Received broken chain");
						}
						if (!VerifyWithKey(token, currentKey))
						{
							throw new InvalidOperationException("Chain signature doesn't match content");
						}
						// The second link is the one Mojang signs.
						if (i == 1 && !currentKey.AsSpan().SequenceEqual(MojangPublicKeyBytes))
						{
							throw new InvalidOperationException("The chain isn't signed by Mojang!");
						}
						parsedPayload = ParsePayloadElement(token);
						currentKey = ParseKey(RequiredString(parsedPayload, "identityPublicKey",
							"chain link carries no identityPublicKey"));
					}
					return LegacyClaims(true, parsedPayload);
				}
				default:
					throw new InvalidOperationException(
						"Unexpected login chain length: " + (chainTokens?.Count ?? 0));
			}
		}

		/// <summary>The port of ChainValidationResult.createLegacyClaims: strict extraData extraction.</summary>
		private static ValidationResult LegacyClaims(bool signed, JsonElement payload)
		{
			byte[] identityPublicKey = ParseKey(RequiredString(payload, "identityPublicKey",
				"Login chain carries no identityPublicKey"));
			if (!payload.TryGetProperty("extraData", out JsonElement extraData) || extraData.ValueKind != JsonValueKind.Object)
			{
				throw new InvalidOperationException("Login chain carries no extraData");
			}
			string displayName = RequiredString(extraData, "displayName", "extraData carries no displayName");
			string identity = RequiredString(extraData, "identity", "extraData carries no identity");
			if (!Guid.TryParse(identity, out _))
			{
				throw new InvalidOperationException("identity node is an invalid UUID");
			}
			string xuid = RequiredString(extraData, "XUID", "extraData carries no XUID");
			return new ValidationResult(signed, displayName, xuid, identity, identityPublicKey, payload.Clone());
		}

		private static JsonElement ParsePayloadElement(string token)
		{
			using JsonDocument document = JsonDocument.Parse(JwtHelper.DecodePayload(token));
			return document.RootElement.Clone();
		}

		private static void RequireAlgorithm(string token, string algorithm)
		{
			IDictionary<string, JsonElement> headers = JwtHelper.DecodeHeaders(token);
			string actual = headers.TryGetValue("alg", out JsonElement value) ? value.GetString() ?? "" : "";
			if (!actual.Equals(algorithm, StringComparison.OrdinalIgnoreCase))
			{
				throw new InvalidOperationException("Login chain token uses alg '" + actual + "', expected " + algorithm);
			}
		}

		private static string RequiredString(JsonElement payload, string name, string message)
		{
			if (!payload.TryGetProperty(name, out JsonElement element)
				|| element.ValueKind != JsonValueKind.String
				|| element.GetString() is not string value)
			{
				throw new InvalidOperationException(message);
			}
			return value;
		}

		public static bool VerifyWithKey(string token, byte[] publicKeyBytes)
		{
			using var key = ECDsa.Create();
			try
			{
				key.ImportSubjectPublicKeyInfo(publicKeyBytes, out _);
				return JwtHelper.TryVerifyEs384(token, key);
			}
			catch (CryptographicException)
			{
				return false;
			}
		}

		public static string? TryHeaderX5U(string token)
		{
			IDictionary<string, JsonElement> headers = JwtHelper.DecodeHeaders(token);
			return headers.TryGetValue("x5u", out JsonElement value) ? value.GetString() : null;
		}

		public static string HeaderX5U(string token)
		{
			return TryHeaderX5U(token) ?? throw new InvalidOperationException(
				"JWT header has no x5u (header keys: " + string.Join(",", JwtHelper.DecodeHeaders(token).Keys) + ")");
		}

		private static byte[] ParseKey(string base64) => Convert.FromBase64String(base64);
	}
}
