using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Protocol.Packets;

namespace EnderPearl.Auth
{
	/// <summary>
	/// Reads and writes the raw <c>LoginPacket.ConnectionRequest</c> blob:
	///
	/// <pre>
	///   int32 (LE) length + auth-payload JSON
	///   int32 (LE) length + skin JWT
	/// </pre>
	///
	/// <p>The auth payload itself is either a JSON array of chain JWTs (the legacy Mojang-signed form)
	/// or a JSON object with an <c>AuthenticationType</c> discriminator plus a <c>Token</c> and/or a
	/// <c>Certificate</c> - the modern self-signed OIDC form 1.26.10+ servers expect.</p>
	/// </summary>
	public sealed class LoginConnectionRequest
	{
		public enum AuthenticationType
		{
			FULL = 0,
			GUEST = 1,
			SELF_SIGNED = 2
		}

		public JsonNode? AuthPayload { get; init; }

		public string SkinJwt { get; init; } = "";

		/// <summary>Decodes the raw ConnectionRequest bytes of a received LoginPacket.</summary>
		public static LoginConnectionRequest Decode(ReadOnlyMemory<byte> connectionRequest)
		{
			using var reader = new BinaryReader(new MemoryStream(connectionRequest.ToArray()));
			int certLength = reader.ReadInt32();
			if (certLength < 0 || reader.BaseStream.Length - reader.BaseStream.Position < certLength)
			{
				throw new InvalidDataException("Login auth payload length is out of bounds: " + certLength);
			}
			string authJson = Encoding.UTF8.GetString(reader.ReadBytes(certLength));

			int skinLength = reader.ReadInt32();
			string skinJwt = skinLength >= 0 && reader.BaseStream.Length - reader.BaseStream.Position >= skinLength
				? Encoding.UTF8.GetString(reader.ReadBytes(skinLength))
				: "";

			JsonNode node = JsonNode.Parse(authJson) ?? throw new InvalidDataException("Login auth payload is not JSON");
			return new LoginConnectionRequest
			{
				AuthPayload = node,
				SkinJwt = skinJwt
			};
		}

		/// <summary>The chain tokens when the payload is the legacy JSON-array form; otherwise empty.</summary>
		public IReadOnlyList<string> LegacyChain()
		{
			if (AuthPayload is JsonArray array)
			{
				var tokens = new List<string>();
				foreach (JsonNode? item in array)
				{
					if (item is JsonValue value && value.TryGetValue<string>(out string? token))
					{
						tokens.Add(token);
					}
				}
				return tokens;
			}
			return Array.Empty<string>();
		}

		/// <summary>The modern object-form fields, or nulls when the payload is the legacy array.</summary>
		public (int? Type, string? Token, JsonNode? Certificate) ModernFields()
		{
			if (AuthPayload is JsonObject obj)
			{
				int? type = obj.TryGetPropertyValue("AuthenticationType", out JsonNode? typeNode)
					&& typeNode is JsonValue typeValue
					&& typeValue.TryGetValue<int>(out int parsed)
					? parsed
					: null;
				string? token = obj.TryGetPropertyValue("Token", out JsonNode? tokenNode)
					&& tokenNode is JsonValue tokenValue
					&& tokenValue.TryGetValue<string>(out string? tokenStr)
					? tokenStr
					: null;
				return (type, token, obj["Certificate"]?.DeepClone());
			}
			return (null, null, null);
		}

		/// <summary>Serializes back into raw ConnectionRequest bytes for a forged LoginPacket.</summary>
		public byte[] Encode()
		{
			string authJson = AuthPayload?.ToJsonString(JsonSerializerOptions) ?? "";
			byte[] authBytes = Encoding.UTF8.GetBytes(authJson);
			byte[] skinBytes = Encoding.UTF8.GetBytes(SkinJwt);

			using var ms = new MemoryStream();
			using (var writer = new BinaryWriter(ms))
			{
				writer.Write(authBytes.Length);
				writer.Write(authBytes);
				writer.Write(skinBytes.Length);
				writer.Write(skinBytes);
			}
			return ms.ToArray();
		}

		private static readonly JsonSerializerOptions JsonSerializerOptions = new()
		{
			WriteIndented = false
		};
	}
}
