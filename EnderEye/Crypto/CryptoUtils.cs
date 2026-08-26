using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using Jose;
using Org.BouncyCastle.Asn1;
using Org.BouncyCastle.Asn1.Pkcs;
using Org.BouncyCastle.Asn1.X509;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Security;
using Org.BouncyCastle.X509;

namespace Protocol.Utils.Crypto
{
	public static class CryptoUtils
	{
		public static byte[] DecodeBase64Url(this string input)
		{
			return Base64Url.Decode(input);
		}
		public static AsymmetricCipherKeyPair GenerateClientKey()
		{
			var generator = new ECKeyPairGenerator("ECDH");
			generator.Init(new ECKeyGenerationParameters(new DerObjectIdentifier("1.3.132.0.34"), SecureRandom.GetInstance("SHA256PRNG")));
			return generator.GenerateKeyPair();
		}
		public static string EncodeBase64Url(this byte[] input)
		{
			return Base64Url.Encode(input);
		}

		public static byte[] DecodeBase64(this string input)
		{
			return Convert.FromBase64String(input);
		}

		public static string EncodeBase64(this byte[] input)
		{
			return Convert.ToBase64String(input);
		}

		public static byte[] ToDerEncoded(this ECDiffieHellmanPublicKey key)
		{
			byte[] asn = new byte[24] { 0x30, 0x76, 0x30, 0x10, 0x6, 0x7, 0x2a, 0x86, 0x48, 0xce, 0x3d, 0x2, 0x1, 0x6, 0x5, 0x2b, 0x81, 0x4, 0x0, 0x22, 0x3, 0x62, 0x0, 0x4 };

			return asn.Concat(key.ToByteArray().Skip(8)).ToArray();
		}
		private static byte[] FixPublicKey(byte[] publicKeyBlob)
		{
			var keyType = new byte[] { 0x45, 0x43, 0x4b, 0x33 };
			var keyLength = new byte[] { 0x30, 0x00, 0x00, 0x00 };

			return keyType.Concat(keyLength).Concat(publicKeyBlob.Skip(1)).ToArray();
		}

		public static byte[] ImportECDsaCngKeyFromCngKey(byte[] inKey)
		{
			inKey[2] = 83;
			return inKey;
		}
		/// <summary>
		/// KEEP This run on a single thread
		/// </summary>
		/// <param name="payload"></param>
		/// <param name="cryptoContext"></param>
		/// <returns></returns>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static byte[] Encrypt(ReadOnlyMemory<byte> payload, CryptoContext cryptoContext)
		{
			// hash
			int hashPoolLen = 8 + payload.Length + cryptoContext.Key.Length;
			var hashBufferPooled = ArrayPool<byte>.Shared.Rent(hashPoolLen);
			Span<byte> hashBuffer = hashBufferPooled.AsSpan();
			BitConverter.GetBytes(Interlocked.Increment(ref cryptoContext.SendCounter)).CopyTo(hashBuffer.Slice(0, 8));
			payload.Span.CopyTo(hashBuffer.Slice(8));
			cryptoContext.Key.CopyTo(hashBuffer.Slice(8 + payload.Length));
			using var hasher = SHA256.Create();
			Span<byte> validationCheckSum = hasher.ComputeHash(hashBufferPooled, 0, hashPoolLen).AsSpan(0, 8);
			ArrayPool<byte>.Shared.Return(hashBufferPooled);

			IBufferedCipher cipher = cryptoContext.Encryptor;
			var encrypted = new byte[payload.Length + 8];
			int length = cipher.ProcessBytes(payload.ToArray(), encrypted, 0);
			cipher.ProcessBytes(validationCheckSum.ToArray(), encrypted, length);

			return encrypted;
		}
		/// <summary>
		/// KEEP This run on a single thread
		/// </summary>
		/// <param name="payload"></param>
		/// <param name="cryptoContext"></param>
		/// <returns></returns>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static ReadOnlyMemory<byte> Decrypt(ReadOnlyMemory<byte> payload, CryptoContext cryptoContext)
		{
			IBufferedCipher cipher = cryptoContext.Decryptor;

			ReadOnlyMemory<byte> clear = cipher.ProcessBytes(payload.ToArray());
			//TODO: Verify hash!
			return clear.Slice(0, clear.Length - 8);
		}


		public static ECDsa ConvertToSingKeyFormat(AsymmetricCipherKeyPair key)
		{
			ECPublicKeyParameters pubAsyKey = (ECPublicKeyParameters)key.Public;
			ECPrivateKeyParameters privAsyKey = (ECPrivateKeyParameters)key.Private;

			var signParam = new ECParameters
			{
				Curve = ECCurve.NamedCurves.nistP384,
				Q =
				{
					X = pubAsyKey.Q.AffineXCoord.GetEncoded(),
					Y = pubAsyKey.Q.AffineYCoord.GetEncoded()
				}
			};
			signParam.D = FixDSize(privAsyKey.D.ToByteArrayUnsigned(), signParam.Q.X.Length);
			signParam.Validate();

			return ECDsa.Create(signParam);
		}

		public static byte[] FixDSize(byte[] input, int expectedSize)
		{
			if (input.Length == expectedSize)
			{
				return input;
			}

			byte[] tmp;

			if (input.Length < expectedSize)
			{
				tmp = new byte[expectedSize];
				Buffer.BlockCopy(input, 0, tmp, expectedSize - input.Length, input.Length);
				return tmp;
			}

			if (input.Length > expectedSize + 1 || input[0] != 0)
			{
				throw new InvalidOperationException();
			}

			tmp = new byte[expectedSize];
			Buffer.BlockCopy(input, 1, tmp, 0, expectedSize);
			return tmp;
		}
		
	}
}
