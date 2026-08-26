using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json.Serialization;

namespace EnderEye.XBOX
{

	public class TokenResult
	{
		/// <summary>
		/// 签名的令牌
		/// </summary>
		[JsonPropertyName("signedToken")]
		public string SignedToken { get; set; }

		/// <summary>
		/// 令牌有效期
		/// </summary>
		[JsonPropertyName("validUntil")]
		public string ValidUntil { get; set; }

		/// <summary>
		/// 令牌签发时间
		/// </summary>
		[JsonPropertyName("issuedAt")]
		public string IssuedAt { get; set; }
	}

	public class keyRes
	{
		/// <summary>
		/// 结果信息
		/// </summary>
		[JsonPropertyName("result")]
		public TokenResult Result { get; set; }
	}
	public class PublicKeyRequest
	{
		/// <summary>
		/// 公钥
		/// </summary>
		public string PublicKey { get; set; }
	}
}
