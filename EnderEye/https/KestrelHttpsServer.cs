// KestrelHttpsServer.cs
using System;
using System.IO;
using System.Net;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using EnderEye.https;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;


public class KestrelHttpsServer
{
	private readonly string _domain;
	private readonly int _port;
	private X509Certificate2 _certificate;
	public static List<JwkKey> JwkKeys = new List<JwkKey>();
	// 保护 JwkKeys：EnderPearlKeyRefresher 后台追加与 HTTP 请求序列化并发访问
	public static readonly object JwkKeysLock = new object();
	// 官方 JWKS 地址：返回授权服务当前用于签名会话令牌的公钥
	private const string RemoteJwksUrl = "https://authorization.franchise.minecraft-services.net/.well-known/keys";
	private static readonly HttpClient JwksHttpClient = new HttpClient
	{
		Timeout = TimeSpan.FromSeconds(10)
	};

	public KestrelHttpsServer(string domain, int port = 443)
	{
		_domain = domain;
		_port = port;
		LoadRemoteJwkKeys();
	}

	/// <summary>
	/// 从官方 JWKS 地址拉取公钥并替换密钥列表，拉取失败时保留现有列表。
	/// </summary>
	private static void LoadRemoteJwkKeys()
	{
		try
		{
			var json = JwksHttpClient.GetStringAsync(RemoteJwksUrl).GetAwaiter().GetResult();
			var remote = JsonSerializer.Deserialize<JwksDocument>(json);
			if (remote?.Keys == null || remote.Keys.Count == 0)
			{
				Console.WriteLine($"[jwks] No keys returned from {RemoteJwksUrl}, keeping existing keys");
				return;
			}

			int loaded;
			lock (JwkKeysLock)
			{
				JwkKeys.Clear();
				foreach (var key in remote.Keys)
				{
					if (key != null && !string.IsNullOrEmpty(key.Kid))
					{
						JwkKeys.Add(key);
					}
				}
				loaded = JwkKeys.Count;
			}

			Console.WriteLine($"[jwks] Loaded {loaded} key(s) from {RemoteJwksUrl}");
		}
		catch (Exception ex)
		{
			Console.WriteLine($"[jwks] Failed to fetch keys from {RemoteJwksUrl}: {ex.Message}, keeping existing keys");
		}
	}

	public void Start()
	{
		_certificate = CertificateCreator.LoadCertificate(_domain);
		var DisVery = new DisVery()
		{
			result = new Result
			{
				serviceEnvironments = new ServiceEnvironments
				{
					auth = new AuthEnvironment
					{
						prod = new EnvironmentDetail
						{
							serviceUri = "https://client.discovery.minecraft-services.net",
							issuer = "https://client.discovery.minecraft-services.net",
							playfabTitleId = "20CA2",
							eduPlayFabTitleId = "6955F"
						}
					}
				},
				supportedEnvironments = new Dictionary<string, List<string>>
				{
					{ "1.21.132", new List<string> { "prod" } }
				}
			}
		};
		var config = new OidcConfiguration
		{
			ClaimsSupported = new List<string>
			{
				"aud", "iss", "sub", "exp", "ipt", "did", "dip", "atyp", "mem", "cap",
				"hmt", "ver", "plat", "dtyp", "lang", "lc", "rc", "age", "ugeo", "ofl",
				"qfl", "prop", "erole", "eoid", "etid", "ssrc", "itr", "pre", "pmid",
				"xid", "mid", "pfcd", "tid", "xname", "pid", "pname", "nid", "nname", "cpk"
			},
			IdTokenSigningAlgValuesSupported = new List<string> { "RS256" },
			Issuer = "https://client.discovery.minecraft-services.net/",
			JwksUri = "https://client.discovery.minecraft-services.net/.well-known/keys",
			ResponseTypesSupported = new List<string> { "token id_token" },
			SubjectTypesSupported = new List<string> { "pairwise" },
			TokenEndpoint = "https://client.discovery.minecraft-services.net/api/v1.0/session/start",
			TokenEndpointAuthMethodsSupported = new List<string> { "private_key_jwt" }
		};
		var jwks = new JwksRoot
		{
			Keys = JwkKeys
		};
		Console.WriteLine("Starting Kestrel HTTPS Server...");
		Console.WriteLine($"Domain: {_domain}");
		Console.WriteLine($"Port: {_port}");

		var host = new WebHostBuilder()
			.UseKestrel(options =>
			{
				options.Listen(System.Net.IPAddress.Loopback, _port, listenOptions =>
				{
					listenOptions.UseHttps(_certificate);
				});

				options.Listen(System.Net.IPAddress.Parse("0.0.0.0"), _port, listenOptions =>
				{
					listenOptions.UseHttps(_certificate);
				});
			})
			.Configure(app =>
			{
				app.Use(async (context, next) =>
				{
					Console.WriteLine($"Receive Get: {context.Request.Method} {context.Request.Path}");

					context.Response.Headers["Access-Control-Allow-Origin"] = "*";
					context.Response.Headers["Access-Control-Allow-Methods"] = "GET, POST, OPTIONS";
					context.Response.Headers["Access-Control-Allow-Headers"] = "Content-Type, Authorization";

					if (context.Request.Method == "OPTIONS")
					{
						context.Response.StatusCode = 200;
						return;
					}

					await next();
				});

				app.Run(async context =>
				{
					var path = context.Request.Path.Value;


				
					if (path.Contains("api/v1.0/discovery/MinecraftDedicatedServer/"))
					{
						await context.Response.WriteAsync(JsonSerializer.Serialize(DisVery));
					}
					else if (path.Contains(".well-known/openid-configuration"))
					{
						await context.Response.WriteAsync(JsonSerializer.Serialize(config));
					}
					else if (path.Contains(".well-known/keys"))
					{
						string jwksJson;
						lock (JwkKeysLock)
						{
							jwksJson = JsonSerializer.Serialize(jwks);
						}
						await context.Response.WriteAsync(jwksJson);
					}
					else
					{
						var backResponse = getBackResponse("client.discovery.minecraft-services.net", "13.107.253.49",path);
						await context.Response.WriteAsync(backResponse);
					}
					
				});
			})
			.Build();
		
		host.StartAsync();
	}
	public static string getBackResponse(string host,string ip,string path)
	{

		HttpWebRequest request = (HttpWebRequest)WebRequest.Create($"https://{ip}{path}");
		request.Host = host;
		request.Method = "GET";

		ServicePointManager.ServerCertificateValidationCallback =
			(sender, cert, chain, sslPolicyErrors) => true;

		try
		{
			using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
			using (StreamReader reader = new StreamReader(response.GetResponseStream()))
			{
				string content = reader.ReadToEnd();
				return content;
			}
		}
		catch (Exception ex)
		{
			Console.WriteLine(ex);
			return string.Empty;
		}
	}
}