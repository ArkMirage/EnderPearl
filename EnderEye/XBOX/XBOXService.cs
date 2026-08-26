using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Server.Kestrel.Core.Internal.Http;
using Protocol;
using XboxAuthNet;
using XboxAuthNet.OAuth;
using XboxAuthNet.OAuth.CodeFlow;
using XboxAuthNet.XboxLive;
using XboxAuthNet.XboxLive.Requests;

namespace EnderEye.XBOX
{
	public static class XBOXService
	{
		private static readonly HttpClient httpClient = new HttpClient();
		private const string LoginsPath = "auths.json";
		private static readonly JsonArray loginArray = new JsonArray();
		public static readonly ConcurrentDictionary<string, MicrosoftOAuthResponse> Users = new ConcurrentDictionary<string, MicrosoftOAuthResponse>();

		static XBOXService()
		{
			Directory.CreateDirectory("users");
		}

		public static async Task StartLoginEach()
		{
			await LoadLoginArrayAsync();

			if (loginArray.Count == 0)
				return;

			var apiClient = new CodeFlowLiveApiClient("00000000441cc96b", XboxAuthConstants.XboxScope, httpClient);
			var oauth = new CodeFlowBuilder(apiClient).Build();

			foreach (string filePath in loginArray)
			{
				try
				{
					var res = ReadSession(filePath);
					if (!string.IsNullOrEmpty(res?.RefreshToken))
					{
						res = await oauth.AuthenticateSilently(res.RefreshToken);
						File.WriteAllTextAsync(filePath,  JsonSerializer.Serialize(res).ToString());
					}
					Users[filePath] = res;
				}
				catch (Exception ex)
				{
				}
			}
		}

		private static async Task LoadLoginArrayAsync()
		{
			if (!File.Exists(LoginsPath))
			{
				await File.WriteAllTextAsync(LoginsPath, loginArray.ToJsonString());
				return;
			}

			var json = await File.ReadAllTextAsync(LoginsPath);
			var array = JsonArray.Parse(json)?.AsArray();
			if (array != null)
			{
				loginArray.Clear();
				foreach (var item in array)
					loginArray.Add(item?.DeepClone());
			}
		}

		private static string GenerateRandomString(int length = 16)
		{
			const string chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";
			var data = new byte[length];
			using (var rng = RandomNumberGenerator.Create())
			{
				rng.GetBytes(data);
			}
			var result = new StringBuilder(length);
			foreach (byte b in data)
			{
				result.Append(chars[b % chars.Length]);
			}
			return result.ToString();
		}

		public static async Task AddUser()
		{
			var apiClient = new CodeFlowLiveApiClient("00000000441cc96b", XboxAuthConstants.XboxScope, httpClient);
			var oauth = new CodeFlowBuilder(apiClient).Build();

			string fileName = GenerateRandomString() + ".json";
			string filePath = Path.Combine("users", fileName);

			MicrosoftOAuthResponse res = ReadSession(null);
			if (!string.IsNullOrEmpty(res?.RefreshToken))
			{
				res = await oauth.AuthenticateSilently(res.RefreshToken);
			}
			else
			{
				res = await oauth.AuthenticateInteractively();
			}

			await WriteSessionAsync(res, filePath);
			Users[filePath] = res;
		}

		public static async Task RemoveUser(MicrosoftOAuthResponse res)
		{
			foreach (var kvp in Users)
			{
				if (kvp.Value == res)
				{
					try
					{
						File.Delete(kvp.Key);
						Users.TryRemove(kvp.Key, out _);
						loginArray.Remove(kvp.Key);
						await File.WriteAllTextAsync(LoginsPath, loginArray.ToJsonString());
					}
					catch (Exception ex)
					{
						// 保留原始行为：吞异常或重新抛出
						throw;
					}
					break;
				}
			}
		}

		public static async Task<string> GetSignedToken(string mcToken, string b64k)
		{
			using var client = new HttpClient();
			var request = new PublicKeyRequest { PublicKey = b64k };
			client.DefaultRequestHeaders.Add("authorization", mcToken);
			client.DefaultRequestHeaders.Add("session-id", Guid.NewGuid().ToString());

			var httpContent = CreateHttpContent(request);
			var response = await client.PostAsync("https://authorization.franchise.minecraft-services.net/api/v1.0/multiplayer/session/start", httpContent);

			if (!response.IsSuccessStatusCode)
				throw new HttpRequestException("Get Minecraft Key error");

			var json = await response.Content.ReadAsStringAsync();
			var keyRes = JsonSerializer.Deserialize<keyRes>(json);
			return keyRes.Result.SignedToken;
		}

		private static MicrosoftOAuthResponse ReadSession(string sessionFilePath)
		{
			if (string.IsNullOrEmpty(sessionFilePath) || !File.Exists(sessionFilePath))
				return null;

			var json = File.ReadAllText(sessionFilePath);
			return JsonSerializer.Deserialize<MicrosoftOAuthResponse>(json);
		}

		private static async Task WriteSessionAsync(MicrosoftOAuthResponse response, string sessionFilePath)
		{
			var json = JsonSerializer.Serialize(response);
			await File.WriteAllTextAsync(sessionFilePath, json);

			
			if (!loginArray.Contains(sessionFilePath))
			{
				loginArray.Add(sessionFilePath);
				await File.WriteAllTextAsync(LoginsPath, loginArray.ToJsonString());
			}
		}

		private static HttpContent CreateHttpContent(object content)
		{
			if (content == null)
				return null;

			var data = JsonSerializer.SerializeToUtf8Bytes(content, new JsonSerializerOptions { WriteIndented = false });
			var httpContent = new ByteArrayContent(data);
			httpContent.Headers.ContentType = new MediaTypeHeaderValue("application/json");
			return httpContent;
		}

		public static async Task<string> XboxLive(MicrosoftOAuthResponse res)
		{
			var xboxAuthClient = new XboxAuthClient(httpClient);
			var userToken = await xboxAuthClient.RequestUserToken(res.AccessToken);
			var xsts = await xboxAuthClient.RequestXsts(userToken.Token, "https://b980a380.minecraft.playfabapi.com/");

			string xboxToken = $"XBL3.0 x={xsts.XuiClaims.UserHash};{xsts.Token}";

			using var client = new HttpClient();
			var loginRequest = new Request
			{
				CreateAccount = true,
				EncryptedRequest = null,
				InfoRequestParameters = new InfoRequestParameters
				{
					GetCharacterInventories = false,
					GetCharacterList = false,
					GetPlayerProfile = true,
					GetPlayerStatistics = false,
					GetTitleData = false,
					GetUserAccountInfo = true,
					GetUserData = false,
					GetUserInventory = false,
					GetUserReadOnlyData = false,
					GetUserVirtualCurrency = false,
					PlayerStatisticNames = null,
					ProfileConstraints = null,
					TitleDataKeys = null,
					UserDataKeys = null,
					UserReadOnlyDataKeys = null
				},
				PlayerSecret = null,
				TitleId = "20CA2",
				XboxToken = xboxToken
			};

			var loginContent = CreateHttpContent(loginRequest);
			var loginResponse = await client.PostAsync("https://20ca2.playfabapi.com/Client/LoginWithXbox", loginContent);

			if (!loginResponse.IsSuccessStatusCode)
				throw new HttpRequestException("Can't login to xbox");

			var loginJson = await loginResponse.Content.ReadAsStringAsync();
			var playFabResponse = JsonSerializer.Deserialize<PlayFabLoginResponse>(loginJson);

			var sessionRequest = new RootObject
			{
				User = new UserInfo() { Token = playFabResponse.Data.SessionTicket }
			};
			var sessionContent = CreateHttpContent(sessionRequest);
			var sessionResponse = await client.PostAsync("https://authorization.franchise.minecraft-services.net/api/v1.0/session/start", sessionContent);

			if (!sessionResponse.IsSuccessStatusCode)
				throw new HttpRequestException("Can't login to xbox");

			var sessionJson = await sessionResponse.Content.ReadAsStringAsync();
			var mcToken = JsonSerializer.Deserialize<MCToken>(sessionJson);
			return mcToken.result.authorizationHeader;
		}
	}

}
