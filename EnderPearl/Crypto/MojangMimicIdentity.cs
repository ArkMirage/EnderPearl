using System;
using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using EnderPearl.Logging;

namespace EnderPearl.Crypto
{
	/// <summary>
	/// The proxy's stable RSA signing identity for Mojang-signature mimicry. Key material is created
	/// once and persisted beside the config (certificate PFX), so the published <c>kid</c> stays
	/// valid across restarts.
	///
	/// <p>The JWKS entry mirrors Microsoft's own schema exactly: kid/x5t are the uppercase-hex SHA-1
	/// thumbprint of the certificate DER (matching the format of the genuine keys document), which
	/// maximizes compatibility with however BDS indexes the key set.</p>
	/// </summary>
	public sealed class MojangMimicIdentity
	{
		private const int KeySizeBits = 2048;
		private const string PfxPassword = "enderpearl-mimic";

		// ---- 正版令牌捕获 -------------------------------------------------------------
		// 玩家带来的正版 franchise 令牌是伪造的"标准答案"：镜像其载荷逐字节内容，
		// 仅替换签名与 kid，任何环境/形状校验都无法区分。
		private static volatile bool mimicActive;
		private static volatile string? genuineHeaderJson;
		private static volatile string? genuinePayloadJson;

		public static void SetMimicActive() => mimicActive = true;
		internal static bool MimicActive => mimicActive;
		public static string? GenuineHeaderJson => genuineHeaderJson;
		public static string? GenuinePayloadJson => genuinePayloadJson;

		public static void CaptureGenuine(string token)
		{
			if (!mimicActive || genuinePayloadJson != null) return;
			try
			{
				var parts = token.Split('.');
				if (parts.Length < 2) return;
				genuineHeaderJson = DecodeSegment(parts[0]);
				genuinePayloadJson = DecodeSegment(parts[1]);
				Logger.Info("captured a genuine franchise token as the mimic template.");
			}
			catch { }
		}

		private static string DecodeSegment(string segment)
		{
			var b64 = segment.Replace('-', '+').Replace('_', '/');
			switch (b64.Length % 4) { case 2: b64 += "=="; break; case 3: b64 += "="; break; }
			return System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(b64));
		}

		// ---- 签名身份 -----------------------------------------------------------------

		public RSA Rsa { get; }
		public string Kid { get; }        // 大写十六进制 SHA-1(cert DER)，与微软格式一致
		public string ModulusBase64Url { get; }
		public string ExponentBase64Url { get; }

		private MojangMimicIdentity(RSA rsa, string kid, string modulusB64Url, string exponentB64Url)
		{
			Rsa = rsa;
			Kid = kid;
			ModulusBase64Url = modulusB64Url;
			ExponentBase64Url = exponentB64Url;
		}

		public static MojangMimicIdentity LoadOrCreate(string configDirectory)
		{
			mimicActive = true;

			string directory = Path.Combine(configDirectory, "signing-keys");
			Directory.CreateDirectory(directory);
			string pfxPath = Path.Combine(directory, "mojang-mimic.pfx");

			X509Certificate2 cert;
			if (File.Exists(pfxPath))
			{
				cert = new X509Certificate2(pfxPath, PfxPassword,
					X509KeyStorageFlags.Exportable | X509KeyStorageFlags.PersistKeySet);
			}
			else
			{
				using RSA rsa = RSA.Create(KeySizeBits);
				var request = new CertificateRequest("CN=EnderPearl Mimic", rsa,
					HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
				request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
				request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
				cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(10));
				File.WriteAllBytes(pfxPath, cert.Export(X509ContentType.Pfx, PfxPassword));
			}
			
			RSA signingRsa = cert.GetRSAPrivateKey()
				?? throw new InvalidOperationException("mimic certificate carries no private key");
			byte[] der = cert.Export(X509ContentType.Cert);
			string kid = Sha1UpperHex(der);

			using RSA publicKey = cert.GetRSAPublicKey()!;
			RSAParameters parameters = publicKey.ExportParameters(false);

			var identity = new MojangMimicIdentity(signingRsa, kid,
				Base64Url(parameters.Modulus!), Base64Url(parameters.Exponent!));

			return identity;
		}

		/// <summary>
		/// The JWK set this identity publishes - field-for-field identical in shape to the genuine
		/// upstream entries ({kty, use, kid, x5t, n, e}), so any parser that accepts theirs accepts
		/// ours.
		/// </summary>
		public string BuildJwksJson()
		{
			return "{\"keys\":[{" +
				"\"kty\":\"RSA\"," +
				"\"use\":\"sig\"," +
				"\"kid\":\"" + Kid + "\"," +
				"\"x5t\":\"" + Kid + "\"," +
				"\"n\":\"" + ModulusBase64Url + "\"," +
				"\"e\":\"" + ExponentBase64Url + "\"}]}";
		}

		private static string Sha1UpperHex(byte[] der)
		{
			byte[] digest = SHA1.HashData(der);
			return Convert.ToHexString(digest);
		}

		private static string Base64Url(byte[] data)
		{
			return Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');
		}
	}
}
