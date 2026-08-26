using Jose;

namespace Protocol.Utils.Crypto
{
	public class HandshakeData
	{
		public string salt { get; set; }

		public string signedToken { get; set; }
	}

	public class CertificateData
	{
		public long Nbf { get; set; }

		public ExtraData ExtraData { get; set; }

		public long RandomNonce { get; set; }

		public string Iss { get; set; }

		public long Exp { get; set; }

		public long Iat { get; set; }

		public bool CertificateAuthority { get; set; }

		public string IdentityPublicKey { get; set; }
	}

	public class ExtraData
	{
		public string Identity { get; set; }

		public string DisplayName { get; set; }

		public string Xuid { get; set; }

		public string TitleId { get; set; }
		public string SandboxId { get; set; }
	}
}