using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace EnderPearl.Crypto;

public static class JwtHelper
{
	private static string Base64UrlEncode(byte[] data)
	{
		return Convert.ToBase64String(data)
			.TrimEnd('=')
			.Replace('+', '-')
			.Replace('/', '_');
	}

	public static byte[] Base64UrlDecode(string str)
	{
		var s = str.Replace('-', '+').Replace('_', '/');
		switch (s.Length % 4)
		{
			case 2: s += "=="; break;
			case 3: s += "="; break;
		}
		return Convert.FromBase64String(s);
	}

	public static string DecodePayload(string token)
	{
		var parts = token.Split('.');
		if (parts.Length < 2)
			throw new ArgumentException("Invalid JWT token", nameof(token));

		return Encoding.UTF8.GetString(Base64UrlDecode(parts[1]));
	}

	public static Dictionary<string, JsonElement> DecodeHeaders(string token)
	{
		var parts = token.Split('.');
		if (parts.Length < 2)
			throw new ArgumentException("Invalid JWT token", nameof(token));

		var headerJson = Encoding.UTF8.GetString(Base64UrlDecode(parts[0]));
		return JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(headerJson)!;
	}

	public static bool TryVerifyEs384(string token, ECDsa verificationKey)
	{
		var parts = token.Split('.');
		if (parts.Length != 3)
			return false;

		var signingInput = Encoding.UTF8.GetBytes($"{parts[0]}.{parts[1]}");
		var signature = Base64UrlDecode(parts[2]);

		return verificationKey.VerifyData(signingInput, signature,
			HashAlgorithmName.SHA384, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
	}

	public static T? DecodeAndVerify<T>(string token, ECDsa verificationKey) where T : class
	{
		if (!TryVerifyEs384(token, verificationKey))
			return null;

		var payload = DecodePayload(token);
		return JsonSerializer.Deserialize<T>(payload);
	}

	private static byte[] SerializePayload<T>(T payload)
	{
		return payload is string jsonStr
			? Encoding.UTF8.GetBytes(jsonStr)
			: JsonSerializer.SerializeToUtf8Bytes(payload);
	}

	public static string EncodeEs384<T>(T payload, ECDsa signingKey, string? x5u = null)
	{
		var header = new Dictionary<string, object> { ["alg"] = "ES384" };
		if (x5u != null)
			header["x5u"] = x5u;

		var b64Header = Base64UrlEncode(JsonSerializer.SerializeToUtf8Bytes(header));
		var b64Payload = Base64UrlEncode(SerializePayload(payload));

		var signingInput = Encoding.UTF8.GetBytes($"{b64Header}.{b64Payload}");
		var signature = signingKey.SignData(signingInput, HashAlgorithmName.SHA384, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);

		return $"{b64Header}.{b64Payload}.{Base64UrlEncode(signature)}";
	}

	/// <summary>
	/// RS256 with an explicit <c>kid</c>: the shape Microsoft's franchise multiplayer tokens use,
	/// which verifiers resolve against the JWKS published at authorization.franchise.minecraft-
	/// services.net/.well-known/keys.
	/// </summary>
	public static string EncodeRs256<T>(T payload, RSA signingKey, string kid)
	{
		var header = new Dictionary<string, object> { ["alg"] = "RS256", ["kid"] = kid, ["typ"] = "JWT" };

		var b64Header = Base64UrlEncode(JsonSerializer.SerializeToUtf8Bytes(header));
		var b64Payload = Base64UrlEncode(SerializePayload(payload));

		var signingInput = Encoding.UTF8.GetBytes($"{b64Header}.{b64Payload}");
		var signature = signingKey.SignData(signingInput, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

		return $"{b64Header}.{b64Payload}.{Base64UrlEncode(signature)}";
	}

	public static string EncodeEs384(ECDsa signingKey, string rawJsonPayload, string? x5u = null)
	{
		return EncodeEs384(rawJsonPayload, signingKey, x5u);
	}
}
