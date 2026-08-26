using System;
using System.Collections.Generic;
using System.Text;

namespace EnderEye.XBOX
{
	public class InfoRequestParameters
	{
		public bool GetCharacterInventories { get; set; }
		public bool GetCharacterList { get; set; }
		public bool GetPlayerProfile { get; set; }
		public bool GetPlayerStatistics { get; set; }
		public bool GetTitleData { get; set; }
		public bool GetUserAccountInfo { get; set; }
		public bool GetUserData { get; set; }
		public bool GetUserInventory { get; set; }
		public bool GetUserReadOnlyData { get; set; }
		public bool GetUserVirtualCurrency { get; set; }
		public List<string>? PlayerStatisticNames { get; set; }
		public string? ProfileConstraints { get; set; }
		public List<string>? TitleDataKeys { get; set; }
		public List<string>? UserDataKeys { get; set; }
		public List<string>? UserReadOnlyDataKeys { get; set; }
	}

	public class Request
	{
		public bool CreateAccount { get; set; }
		public string? EncryptedRequest { get; set; }
		public InfoRequestParameters? InfoRequestParameters { get; set; }
		public string? PlayerSecret { get; set; }
		public string? TitleId { get; set; }
		public string? XboxToken { get; set; }
	}
	public class Device
	{
		public string ApplicationType { get; set; }
		public string GameVersion { get; set; }
		public string Id { get; set; }
		public string Memory { get; set; }
		public string Platform { get; set; }
		public string PlayFabTitleId { get; set; }
		public string StorePlatform { get; set; }
		public string Type { get; set; }
	}

	public class User
	{
		public string Token { get; set; }
		public string TokenType { get; set; }
	}

	public class Root
	{
		public Device Device { get; set; }
		public User User { get; set; }
	}
	public class MinecraftConfig
	{
		public string id { get; set; }
		public Dictionary<string, string> parameters { get; set; }
	}

	public class Configurations
	{
		public MinecraftConfig minecraft { get; set; }
	}

	public class Result
	{
		public string authorizationHeader { get; set; }
		public string validUntil { get; set; }
		public string issuedAt { get; set; }
		public List<string> Treatments { get; set; }
		public Configurations configurations { get; set; }
		public string treatmentContext { get; set; }
	}

	public class MCToken
	{
		public Result result { get; set; }
	}

}