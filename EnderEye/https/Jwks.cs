using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json.Serialization;

namespace EnderEye.https
{
	public class JwksRoot
	{
		[JsonPropertyName("keys")]
		public List<JwkKey> Keys { get; set; }
	}
}
