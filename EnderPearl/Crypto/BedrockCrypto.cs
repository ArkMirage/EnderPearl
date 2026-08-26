using System;
using System.Security.Cryptography;

namespace EnderPearl.Crypto
{
	/// <summary>
	/// The Bedrock login encryption handshake: an ECDH agreement over secp384r1 keys, hashed together
	/// with a server-chosen random salt into the AES key both sides derive.
	///
	/// <p>Key material is exchanged as base64 X.509 SubjectPublicKeyInfo blobs (the wire format inside
	/// the client's identity JWT <c>cpk</c> claim), and the handshake JWT is ES384 with the signing
	/// public key in the <c>x5u</c> header.</p>
	/// </summary>
	public static class BedrockCrypto
	{
		public static ECDsaHolder CreateKeyPair() => ECDsaHolder.Generate();

		public static byte[] RandomToken()
		{
			byte[] token = new byte[16];
			RandomNumberGenerator.Fill(token);
			return token;
		}

		/// <summary>
		/// The AES-256 session key: SHA-256(salt || ECDH(localPrivate, remotePublic)). Both sides run
		/// the same derivation.
		/// </summary>
		public static byte[] SecretKey(ECDsaHolder localKeyPair, byte[] remotePublicKeyBytes, byte[] token)
		{
			try
			{
				using var remote = ECDiffieHellman.Create();
				remote.ImportSubjectPublicKeyInfo(remotePublicKeyBytes, out _);
				byte[] sharedSecret = localKeyPair.Agreement.DeriveRawSecretAgreement(remote.PublicKey);

				byte[] input = new byte[token.Length + sharedSecret.Length];
				Buffer.BlockCopy(token, 0, input, 0, token.Length);
				Buffer.BlockCopy(sharedSecret, 0, input, token.Length, sharedSecret.Length);
				return SHA256.HashData(input);
			}
			catch (CryptographicException exception)
			{
				throw new InvalidOperationException("Unable to create Bedrock encryption key", exception);
			}
		}

		/// <summary>The ServerToClientHandshake JWT: ES384-signed {salt: base64(token)} with x5u = our public key.</summary>
		public static string HandshakeJwt(ECDsaHolder keyPair, byte[] token)
		{
			var claims = new { salt = Convert.ToBase64String(token) };
			return JwtHelper.EncodeEs384(claims, keyPair.Signer, keyPair.PublicKeyBase64());
		}
	}
}
