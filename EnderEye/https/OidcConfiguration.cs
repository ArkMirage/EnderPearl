using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json.Serialization;

namespace EnderEye.https
{
	public class OidcConfiguration
	{
		[JsonPropertyName("claims_supported")]
		public List<string> ClaimsSupported { get; set; }

		[JsonPropertyName("id_token_signing_alg_values_supported")]
		public List<string> IdTokenSigningAlgValuesSupported { get; set; }

		[JsonPropertyName("issuer")]
		public string Issuer { get; set; }

		[JsonPropertyName("jwks_uri")]
		public string JwksUri { get; set; }

		[JsonPropertyName("response_types_supported")]
		public List<string> ResponseTypesSupported { get; set; }

		[JsonPropertyName("subject_types_supported")]
		public List<string> SubjectTypesSupported { get; set; }

		[JsonPropertyName("token_endpoint")]
		public string TokenEndpoint { get; set; }

		[JsonPropertyName("token_endpoint_auth_methods_supported")]
		public List<string> TokenEndpointAuthMethodsSupported { get; set; }
	}
}
