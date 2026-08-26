using System;
using System.Security.Cryptography;

namespace EnderPearl.Crypto
{
	/// <summary>
	/// One EC P-384 key pair usable for both ES384 signing and ECDH agreement - the C# equivalent of a
	/// Java <c>KeyPair</c>, where one key serves KeyAgreement and JsonWebSignature alike. .NET models
	/// the two algorithms as separate types, so the pair is created once from shared parameters and
	/// handed around as this holder.
	/// </summary>
	public sealed class ECDsaHolder : IDisposable
	{
		public ECDsa Signer { get; }

		public ECDiffieHellman Agreement { get; }

		private ECDsaHolder(ECDsa signer, ECDiffieHellman agreement)
		{
			Signer = signer;
			Agreement = agreement;
		}

		/// <summary>Generates a fresh P-384 key pair (Java: KeyPairGenerator "EC" secp384r1).</summary>
		public static ECDsaHolder Generate()
		{
			ECDsa signer = ECDsa.Create(ECCurve.NamedCurves.nistP384);
			try
			{
				var agreement = ECDiffieHellman.Create(signer.ExportParameters(true));
				return new ECDsaHolder(signer, agreement);
			}
			catch (Exception)
			{
				signer.Dispose();
				throw;
			}
		}

		/// <summary>The base64 X.509 SubjectPublicKeyInfo blob carried in JWT x5u headers and cpk claims.</summary>
		public string PublicKeyBase64()
		{
			return Convert.ToBase64String(Signer.ExportSubjectPublicKeyInfo());
		}

		public void Dispose()
		{
			Signer.Dispose();
			Agreement.Dispose();
		}
	}
}
