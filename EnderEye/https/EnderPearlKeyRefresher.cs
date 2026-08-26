// EnderPearlKeyRefresher.cs
using System;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;

namespace EnderEye.https
{
	/// <summary>
	/// 程序启动时及每 30 秒从 EnderPearl (http://127.0.0.1:19139/keys/) 拉取其模拟签名身份的
	/// 公钥 JWKS，把里面的 key 按 kid 去重后追加进 KestrelHttpsServer 返回给 BDS 的
	/// /.well-known/keys 密钥列表。拉取失败时沿用现有列表并打印一行日志。
	/// </summary>
	public static class EnderPearlKeyRefresher
	{
		private const string EnderPearlJwksUrl = "http://127.0.0.1:19139/keys/";
		private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(30);
		private static readonly HttpClient HttpClient = new HttpClient
		{
			Timeout = TimeSpan.FromSeconds(5)
		};
		private static Timer _timer;

		public static void Start()
		{
			// dueTime = 0：立即执行第一次拉取，之后每 30 秒一次。
			_timer = new Timer(_ => RefreshOnce(), null, TimeSpan.Zero, RefreshInterval);
		}

		public static void RefreshOnce()
		{
			try
			{
				string json = HttpClient.GetStringAsync(EnderPearlJwksUrl).GetAwaiter().GetResult();
				var remote = JsonSerializer.Deserialize<JwksDocument>(json);
				if (remote?.Keys == null || remote.Keys.Count == 0)
				{
					Console.WriteLine("[EnderPearl] 拉取成功但远端未返回任何密钥，沿用现有密钥列表");
					return;
				}

				int added;
				string addedKids;
				lock (KestrelHttpsServer.JwkKeysLock)
				{
					var newKeys = remote.Keys
						.Where(k => k != null && !string.IsNullOrEmpty(k.Kid))
						.Where(k => !KestrelHttpsServer.JwkKeys.Any(existing =>
							string.Equals(existing.Kid, k.Kid, StringComparison.OrdinalIgnoreCase)))
						.ToList();

					foreach (var key in newKeys)
					{
						KestrelHttpsServer.JwkKeys.Add(key);
					}
					added = newKeys.Count;
					addedKids = string.Join(", ", newKeys.Select(k => k.Kid));
				}

				if (added > 0)
				{
					Console.WriteLine($"[EnderPearl] 从 {EnderPearlJwksUrl} 追加 {added} 个新公钥: {addedKids}");
				}
			}
			catch (Exception ex)
			{
				Console.WriteLine($"[EnderPearl] 拉取 JWKS 失败({EnderPearlJwksUrl}): {ex.Message}，沿用现有密钥列表");
			}
		}
	}
}
