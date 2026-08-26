using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection.Metadata;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.X509;
using Protocol.Utils.Crypto;

/// <summary>
/// JWK (JSON Web Key) 格式
/// </summary>
public class JwkKey
{
	[JsonPropertyName("kty")]
	public string Kty { get; set; }  // 密钥类型，如 "RSA"

	[JsonPropertyName("use")]
	public string Use { get; set; }  // 用途，如 "sig" (签名)

	[JsonPropertyName("kid")]
	public string Kid { get; set; }  // 密钥ID，40字符十六进制

	[JsonPropertyName("x5t")]
	public string X5t { get; set; }  // SHA-1 指纹，40字符十六进制

	[JsonPropertyName("n")]
	public string N { get; set; }    // RSA模数，Base64Url编码

	[JsonPropertyName("e")]
	public string E { get; set; }    // RSA公钥指数，通常为 "AQAB" (65537)

	[JsonPropertyName("alg")]
	[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
	public string Alg { get; set; }  // 算法，如 "RS256"
}

/// <summary>
/// JWKS (JSON Web Key Set) 格式
/// </summary>
public class JwksDocument
{
	[JsonPropertyName("keys")]
	public List<JwkKey> Keys { get; set; } = new List<JwkKey>();
}

/// <summary>
/// 修正后的JWT密钥管理器 - 使用正确的密钥格式
/// </summary>
public class MinecraftStyleKeyManager
{
	private readonly string _keyDirectory;
	private readonly Dictionary<string, RSA> _keyCache = new Dictionary<string, RSA>();

	public MinecraftStyleKeyManager(string keyDirectory = "minecraft_keys")
	{
		_keyDirectory = keyDirectory;
		if (!Directory.Exists(_keyDirectory))
		{
			Directory.CreateDirectory(_keyDirectory);
		}
	}

	/// <summary>
	/// 创建新的RSA密钥对 - 使用Minecraft风格的kid (SHA-1哈希)
	/// </summary>
	public (JwkKey jwk, string privateKeyPem) CreateNewKeyPair()
	{
		using var rsa = RSA.Create(2048);  // 2048位RSA

		// 1. 导出公钥参数
		var parameters = rsa.ExportParameters(false);

		// 2. 计算kid - 使用公钥的SHA-1哈希 (40字符十六进制)
		byte[] publicKeyBytes = rsa.ExportRSAPublicKey();
		using var sha1 = SHA1.Create();
		byte[] hash = sha1.ComputeHash(publicKeyBytes);
		string kid = BitConverter.ToString(hash).Replace("-", "").ToUpper();

		Console.WriteLine($"生成的公钥指纹 (SHA-1): {kid}");
		Console.WriteLine($"指纹长度: {kid.Length} 字符");

		// 3. Base64Url编码N和E
		string n = Base64UrlEncode(parameters.Modulus);
		string e = Base64UrlEncode(parameters.Exponent);

		Console.WriteLine($"N长度: {n.Length} 字符");
		Console.WriteLine($"E: {e}");

		// 4. 创建JWK
		var jwk = new JwkKey
		{
			Kty = "RSA",
			Use = "sig",
			Kid = kid,
			X5t = kid,  // x5t 也是相同的SHA-1指纹
			N = n,
			E = e,
			Alg = "RS256"
		};

		// 5. 导出私钥
		string privateKeyPem = rsa.ExportRSAPrivateKeyPem();

		// 6. 保存到文件
		SaveKeyPair(kid, jwk, privateKeyPem, rsa);

		// 7. 缓存RSA实例
		_keyCache[kid] = rsa;

		return (jwk, privateKeyPem);
	}

	public RSA ImportPublicKeyFromJwk(JwkKey jwk)
	{
		try
		{
			byte[] modulus = Base64UrlDecode(jwk.N);
			byte[] exponent = Base64UrlDecode(jwk.E);

			Console.WriteLine($"导入密钥 - Kid: {jwk.Kid}");
			Console.WriteLine($"N长度: {modulus.Length * 8} bits, Base64Url长度: {jwk.N.Length}");
			Console.WriteLine($"E: {jwk.E}");


			var parameters = new RSAParameters
			{
				Modulus = modulus,
				Exponent = exponent
			};


			var rsa = RSA.Create();
			rsa.ImportParameters(parameters);


			byte[] publicKeyBytes = rsa.ExportRSAPublicKey();
			using var sha1 = SHA1.Create();
			byte[] hash = sha1.ComputeHash(publicKeyBytes);
			string computedKid = BitConverter.ToString(hash).Replace("-", "").ToUpper();

			Console.WriteLine($"计算出的kid: {computedKid}");
			Console.WriteLine($"提供的kid: {jwk.Kid}");
			Console.WriteLine($"匹配: {computedKid == jwk.Kid}");


			_keyCache[jwk.Kid] = rsa;

			return rsa;
		}
		catch (Exception ex)
		{
			Console.WriteLine($"导入密钥失败: {ex.Message}");
			throw;
		}
	}

	public JwksDocument GetJwksDocument()
	{
		var document = new JwksDocument();

		foreach (var file in Directory.GetFiles(_keyDirectory, "*_jwk.json"))
		{
			string json = File.ReadAllText(file);
			var jwk = JsonSerializer.Deserialize<JwkKey>(json);
			if (jwk != null)
			{
				document.Keys.Add(jwk);
			}
		}
		return document;
	}
	public List<JwkKey> GetAllJwkKeys()
	{
		List<JwkKey> temp = new List<JwkKey>();
		foreach (var file in Directory.GetFiles(_keyDirectory, "*_jwk.json"))
		{
			string json = File.ReadAllText(file);
			var jwk = JsonSerializer.Deserialize<JwkKey>(json);
			if (jwk != null)
			{
				temp.Add(jwk);
			}
		}

		return temp;
	}

	public RSA LoadPrivateKey(string kid)
	{

		if (_keyCache.ContainsKey(kid))
			return _keyCache[kid];

		string privateKeyPath = Path.Combine(_keyDirectory, $"{kid}_private.pem");
		if (!File.Exists(privateKeyPath))
			throw new FileNotFoundException($"未找到私钥: {kid}");

		string pemContent = File.ReadAllText(privateKeyPath);
		var rsa = RSA.Create();
		rsa.ImportFromPem(pemContent.ToCharArray());

		_keyCache[kid] = rsa;
		return rsa;
	}

	public RSA LoadPublicKey(string kid)
	{
		if (_keyCache.ContainsKey(kid))
			return _keyCache[kid];

		string jwkPath = Path.Combine(_keyDirectory, $"{kid}_jwk.json");
		if (File.Exists(jwkPath))
		{
			string json = File.ReadAllText(jwkPath);
			var jwk = JsonSerializer.Deserialize<JwkKey>(json);
			return ImportPublicKeyFromJwk(jwk);
		}

		throw new FileNotFoundException($"未找到公钥: {kid}");
	}

	private void SaveKeyPair(string kid, JwkKey jwk, string privateKeyPem, RSA rsa)
	{

		string jwkJson = JsonSerializer.Serialize(jwk, new JsonSerializerOptions
		{
			WriteIndented = true,
			DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
		});
		File.WriteAllText(Path.Combine(_keyDirectory, $"{kid}_jwk.json"), jwkJson);


		File.WriteAllText(Path.Combine(_keyDirectory, $"{kid}_private.pem"), privateKeyPem);

		
		string publicKeyPem = rsa.ExportRSAPublicKeyPem();
		File.WriteAllText(Path.Combine(_keyDirectory, $"{kid}_public.pem"), publicKeyPem);

		SaveJwksDocument();
	}

	private void SaveJwksDocument()
	{
		var document = GetJwksDocument();
		string jwksJson = JsonSerializer.Serialize(document, new JsonSerializerOptions
		{
			WriteIndented = true
		});
		File.WriteAllText(Path.Combine(_keyDirectory, "jwks.json"), jwksJson);
	}

	private static string Base64UrlEncode(byte[] input)
	{
		string base64 = Convert.ToBase64String(input);
		return base64.Replace('+', '-').Replace('/', '_').TrimEnd('=');
	}

	private static byte[] Base64UrlDecode(string input)
	{
		string base64 = input.Replace('-', '+').Replace('_', '/');
		switch (base64.Length % 4)
		{
			case 2: base64 += "=="; break;
			case 3: base64 += "="; break;
		}
		return Convert.FromBase64String(base64);
	}
}
public class MinecraftStyleJwtGenerator
{
	public readonly MinecraftStyleKeyManager _keyManager;

	public MinecraftStyleJwtGenerator()
	{
		_keyManager = new MinecraftStyleKeyManager();
	}

	public (string,AsymmetricCipherKeyPair) GenerateToken(string existingKid)
	{
		var generateClientKey = CryptoUtils.GenerateClientKey();
		string kid;
		RSA rsa;
		JwkKey jwk;

			kid = existingKid;
			jwk = GetJwkFromKid(kid);
			try
			{
				rsa = _keyManager.LoadPrivateKey(kid);
		}	
		catch (FileNotFoundException e)
		{
			var (newJwk, privateKeyPem) = _keyManager.CreateNewKeyPair();
			kid = newJwk.Kid;
			jwk = newJwk;
			rsa = _keyManager.LoadPrivateKey(kid);
		}
			

		var header = new Dictionary<string, object>
		{
			{ "alg", "RS256" },
			{ "kid", kid },
			{ "typ", "JWT" }
		};
		
		var payload = new Dictionary<string, object>
		{
			{ "iss", "https://authorization.franchise.minecraft-services.net/" },
			{ "aud", "api://auth-minecraft-services/multiplayer" },
			{ "iat", DateTimeOffset.UtcNow.ToUnixTimeSeconds() },
			{ "exp", DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds() },
			{ "jti", Guid.NewGuid().ToString("N") },
			{ "sub", "1B4BAC17CC3B53B6" },
			{ "ipt", "PlayFab" },
			{ "mid", "89E497D6112B6ECD" },
			{ "tid", "20CA2" },
			{ "pfcd", 1717928313 },
			{ "cpk", SubjectPublicKeyInfoFactory.CreateSubjectPublicKeyInfo(generateClientKey.Public).GetEncoded().EncodeBase64() },
			{ "xid", "2535432876358542" },
			{ "xname", "YoumiHa" }
		};
		return (GenerateJwtString(rsa, header, payload),generateClientKey);
	}

	private JwkKey GetJwkFromKid(string kid)
	{
		string jwkPath = Path.Combine("minecraft_keys", $"{kid}_jwk.json");
		if (File.Exists(jwkPath))
		{
			string json = File.ReadAllText(jwkPath);
			return JsonSerializer.Deserialize<JwkKey>(json);
		}
		throw new FileNotFoundException($"未找到JWK: {kid}");
	}

	private string GenerateJwtString(RSA rsa, Dictionary<string, object> header, Dictionary<string, object> payload)
	{
		string headerJson = JsonSerializer.Serialize(header);
		string payloadJson = JsonSerializer.Serialize(payload);

		string encodedHeader = Base64UrlEncode(Encoding.UTF8.GetBytes(headerJson));
		string encodedPayload = Base64UrlEncode(Encoding.UTF8.GetBytes(payloadJson));

		string dataToSign = $"{encodedHeader}.{encodedPayload}";
		byte[] signatureBytes = rsa.SignData(
			Encoding.UTF8.GetBytes(dataToSign),
			HashAlgorithmName.SHA256,
			RSASignaturePadding.Pkcs1
		);
		string encodedSignature = Base64UrlEncode(signatureBytes);

		return $"{encodedHeader}.{encodedPayload}.{encodedSignature}";
	}

	private string Base64UrlEncode(byte[] input)
	{
		string base64 = Convert.ToBase64String(input);
		return base64.Replace('+', '-').Replace('/', '_').TrimEnd('=');
	}
}