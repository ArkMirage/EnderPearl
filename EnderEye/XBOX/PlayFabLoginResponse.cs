using System;
using System.Collections.Generic;
using System.Text;

namespace EnderEye.XBOX
{
	using System;
	using System.Collections.Generic;
	using System.Text.Json.Serialization;

	public class PlayFabLoginResponse
	{
		[JsonPropertyName("code")]
		public int Code { get; set; }

		[JsonPropertyName("status")]
		public string Status { get; set; }

		[JsonPropertyName("data")]
		public PlayFabLoginData Data { get; set; }
	}

	public class PlayFabLoginData
	{
		[JsonPropertyName("SessionTicket")]
		public string SessionTicket { get; set; }

		[JsonPropertyName("PlayFabId")]
		public string PlayFabId { get; set; }

		[JsonPropertyName("NewlyCreated")]
		public bool NewlyCreated { get; set; }

		[JsonPropertyName("SettingsForUser")]
		public SettingsForUser SettingsForUser { get; set; }

		[JsonPropertyName("LastLoginTime")]
		public DateTime LastLoginTime { get; set; }

		[JsonPropertyName("InfoResultPayload")]
		public InfoResultPayload InfoResultPayload { get; set; }

		[JsonPropertyName("EntityToken")]
		public EntityToken EntityToken { get; set; }

		[JsonPropertyName("TreatmentAssignment")]
		public TreatmentAssignment TreatmentAssignment { get; set; }
	}

	public class SettingsForUser
	{
		[JsonPropertyName("NeedsAttribution")]
		public bool NeedsAttribution { get; set; }

		[JsonPropertyName("GatherDeviceInfo")]
		public bool GatherDeviceInfo { get; set; }

		[JsonPropertyName("GatherFocusInfo")]
		public bool GatherFocusInfo { get; set; }
	}

	public class InfoResultPayload
	{
		[JsonPropertyName("AccountInfo")]
		public AccountInfo AccountInfo { get; set; }

		[JsonPropertyName("UserInventory")]
		public List<object> UserInventory { get; set; }

		[JsonPropertyName("UserDataVersion")]
		public int UserDataVersion { get; set; }

		[JsonPropertyName("UserReadOnlyDataVersion")]
		public int UserReadOnlyDataVersion { get; set; }

		[JsonPropertyName("CharacterInventories")]
		public List<object> CharacterInventories { get; set; }

		[JsonPropertyName("PlayerProfile")]
		public PlayerProfile PlayerProfile { get; set; }
	}

	public class AccountInfo
	{
		[JsonPropertyName("PlayFabId")]
		public string PlayFabId { get; set; }

		[JsonPropertyName("Created")]
		public DateTime Created { get; set; }

		[JsonPropertyName("TitleInfo")]
		public TitleInfo TitleInfo { get; set; }

		[JsonPropertyName("PrivateInfo")]
		public PrivateInfo PrivateInfo { get; set; }

		[JsonPropertyName("XboxInfo")]
		public XboxInfo XboxInfo { get; set; }

		[JsonPropertyName("ServerCustomIdInfo")]
		public ServerCustomIdInfo ServerCustomIdInfo { get; set; }
	}

	public class TitleInfo
	{
		[JsonPropertyName("Origination")]
		public string Origination { get; set; }

		[JsonPropertyName("Created")]
		public DateTime Created { get; set; }

		[JsonPropertyName("LastLogin")]
		public DateTime LastLogin { get; set; }

		[JsonPropertyName("FirstLogin")]
		public DateTime FirstLogin { get; set; }

		[JsonPropertyName("isBanned")]
		public bool IsBanned { get; set; }

		[JsonPropertyName("TitlePlayerAccount")]
		public TitlePlayerAccount TitlePlayerAccount { get; set; }
	}

	public class TitlePlayerAccount
	{
		[JsonPropertyName("Id")]
		public string Id { get; set; }

		[JsonPropertyName("Type")]
		public string Type { get; set; }

		[JsonPropertyName("TypeString")]
		public string TypeString { get; set; }
	}

	public class PrivateInfo
	{
		// 私有信息字段，根据实际需要添加属性
		// 示例中为空对象
	}

	public class XboxInfo
	{
		[JsonPropertyName("XboxUserId")]
		public string XboxUserId { get; set; }

		[JsonPropertyName("XboxUserSandbox")]
		public string XboxUserSandbox { get; set; }
	}

	public class ServerCustomIdInfo
	{
		[JsonPropertyName("CustomId")]
		public string CustomId { get; set; }
	}

	public class PlayerProfile
	{
		[JsonPropertyName("PublisherId")]
		public string PublisherId { get; set; }

		[JsonPropertyName("TitleId")]
		public string TitleId { get; set; }

		[JsonPropertyName("PlayerId")]
		public string PlayerId { get; set; }
	}

	public class EntityToken
	{
		[JsonPropertyName("EntityToken")]
		public string Token { get; set; }

		[JsonPropertyName("TokenExpiration")]
		public DateTime TokenExpiration { get; set; }

		[JsonPropertyName("Entity")]
		public Entity Entity { get; set; }
	}

	public class Entity
	{
		[JsonPropertyName("Id")]
		public string Id { get; set; }

		[JsonPropertyName("Type")]
		public string Type { get; set; }

		[JsonPropertyName("TypeString")]
		public string TypeString { get; set; }
	}

	public class TreatmentAssignment
	{
		[JsonPropertyName("Variants")]
		public List<object> Variants { get; set; }

		[JsonPropertyName("Variables")]
		public List<object> Variables { get; set; }
	}
}
