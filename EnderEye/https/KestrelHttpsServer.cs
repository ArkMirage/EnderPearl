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

	public KestrelHttpsServer(string domain, int port = 443)
	{
		_domain = domain;
		_port = port;
		JwkKeys.AddRange(new List<JwkKey>
	{
		new JwkKey
		{
			Kty = "RSA",
			Use = "sig",
			Kid = "1B48C3A4B3BE81C67B8C9AF5BB276A726143813D",
			X5t = "1B48C3A4B3BE81C67B8C9AF5BB276A726143813D",
			N = "rjsdwRAdLbk_D4-oK2_vaxfs0_uzACex3HkLzFWbruTeqANv2_wkMx7EeoW8fKr_36wjyxEAFMtzm_cJ7wbefy8bTo3NPoyHyPye59PD-GdMFhxPI92wNWIHQX9-KxIGZYcbAlEH_p64MmbPcxlQz5WDioOt3zqn-X8TMlBE0Nyycj5lu_AXBCLdE7JHS-nI3uKQkw73XqH_r9JfcIp5kRIfbbdchPZvoh6WVQHTc6DcqdD-_mZgsrNY-wxG09uSpm7XqwFBs7UH3RydVh8Ps2zo5Gv8tfw4T7_NgDi_TbKvxDf4iTXovSHrN1dfNk0tW5i0mXGZ2sutO1ddv8K8zQ",
			E = "AQAB"
		},
		new JwkKey
		{
			Kty = "RSA",
			Use = "sig",
			Kid = "B28879DEAAD152E5CCBCDC0BB8AB15A39D2B6FCA",
			X5t = "B28879DEAAD152E5CCBCDC0BB8AB15A39D2B6FCA",
			N = "o50tCAjp3FLwXJeFhZQJCQW2SF1_VBakDqFuG6C9hl_KbMLc7bTwG906XCQMZKoUnJx3Xgu_dAQK_e9Vi1VRNsWHAt5uXqOrsVgp1C20xgN_cjfh8bqSEA5LlgqHHvcAZwWKicJUJA0yF9jW81HgSV-svNsWuZ0s5qcRK1sJELkmWoUknsiKLEowsE7WZUcg0Yjk6dlQ0_3Bp8zVQxSsjdBQyZ4xsx-DGwgl7Hs3MSXhZd_h9k-pcLb4qOfElCqyKITVZTjEfX44VgQUjIp2kBqIDwIzCkbxO0jKSsUO6jU4VYd-M33hAOV0EkPQfXsNii9mwulqewgzrxeMdviBHQ",
			E = "AQAB"
		},
		new JwkKey
		{
			Kty = "RSA",
			Use = "sig",
			Kid = "CEA73269DB19FFB3006D300E50BB608CC8310FD5",
			X5t = "CEA73269DB19FFB3006D300E50BB608CC8310FD5",
			N = "st3IOVmfJYZA7WwWdS7HTquyORB6sVqeRcccLadcHaQqK2wzG6kiwas7DRRDyTgvCBPTlxTcD1ZqZKHMcUH1fipi2mhZ3jCydRCEUs1TzblsLKUWJogp-LrFNxRfanhwT7dEsbdjaPg1cwmQBjNrmh8VjGgoXNxqVI9Cb2-zWFsz9BLRnUkgZJ7cihFAGgWKYwHIVhN3Y_m3loUht307kK3FetfDaVHTb-O7BQAliJwooYCV_3a7nB-jO9X5SFyxzGmWbSB1Nuf3r7Am4T2yJEJoZKjr9BGUNd61hEWqVdMDCXQgAl0zekuatIHLi534-GOXaZk6TxUFTdz7knmPNQ",
			E = "AQAB"
		},
		new JwkKey
		{
			Kty = "RSA",
			Use = "sig",
			Kid = "0496888BAA1AB10B855F6F890BC12AF009A7176B",
			X5t = "0496888BAA1AB10B855F6F890BC12AF009A7176B",
			N = "wDQ4W6Kjz6x3p6FpdRLFlHbdmYsefns1DG9YIeyhrLtF6nu0VsO5kY9EGDnLUMI0y3j2lvPCvbbkb-Ez78Yaa_Ua7dg9gJp0Ef2tuRlJlydTzLZNIIde-Fj1ocucs0fEImpi6UN_5iwWXy93sMGBWeVR02lhI8jUNjBbyXyLcUOxV-bM64RrCrscSs2n-W7OdHgnzGdmHnzl-fsuB-X-DobbRLVg7tu6d9EEt7__GsHHSVPeCYGA0m78FEdbxCoeqA1FEsP-m9azjkfr12TpeTulNgT8wkcnmyRr3uMwcQkcK5gTGo2fKpjKdCm4ZMqgPaU5YobI31HITfNB873SnQ",
			E = "AQAB"
		},
		new JwkKey
		{
			Kty = "RSA",
			Use = "sig",
			Kid = "7E158FF6D4124A0F5FF1069F49316EE22FC12633",
			X5t = "7E158FF6D4124A0F5FF1069F49316EE22FC12633",
			N = "2jjPHWpe1QU6GCfxuZIVcoeJ-WnpngkgDgWyJVSFZJEajV0T3LxB6exMarHofkjSAEd99b9pWZxvCmzgNaBiKBAdTilLVnzcTkYEFNHQK0eMR321Ra8mYgVF_9M-4pXnUXRKLwXiu7ML0C_gV2hbHAfzUbJF66x2mlVj45w2zzuq8JtqSab0QHFmNH12eK88ZbtFmxupslScDlnPCIxuf5F_aYuWv0KkGMd_h57RQ8OFHvKcnA5exFWXskQ5vZy58DL3CyJUWXZZu2qlD-OXQ5Cs6IHDzhUMxbdli_QVEgn_OPql58lsQrNTQgWcVPKJrX0tGdCc-GObq9o4k1g0xQ",
			E = "AQAB"
		},
		new JwkKey
		{
			Kty = "RSA",
			Use = "sig",
			Kid = "C762E10600DE422ED48E620E83C5298E750AB0C3",
			X5t = "C762E10600DE422ED48E620E83C5298E750AB0C3",
			N = "rX8sVF0I0G9Q20vx6xGIrcKUD_p0dcRWtIZvHbV-8ZshLOEJ_yA4pPn4gThI6jtjHhGOZ5IMKKJnrv5WuyAR69zVn2IDCOyx9YkkYWg0Akbv4CuDPkyNuqMZIyFzAcFHsTBkcqCYn35wHwdA1dmbefStNhD85J8wYPRijDUFlWs1lWU-PlWIZlux2GYm5-L42p5YmArAn7aefxP_ZddzXEQjfk5__KwLRqZkN25g_Ik2uizBq0jiJZ1mUwduU86tHFFpbJl1oHEBa2uPlcZm3M_Zr0arfwKd8K80zOO2ipVCzyGM0s11WgatSfcxaMbR2VBZSgQaDpKFy7a4YhaHCQ",
			E = "AQAB"
		},
		new JwkKey
		{
			Kty = "RSA",
			Use = "sig",
			Kid = "B78CA4061336AA4259E621F5B352192FD94A946B",
			X5t = "B78CA4061336AA4259E621F5B352192FD94A946B",
			N = "o56ihG-wGNGmRR82T2Zqol6Z2hmaFTB7BaKw9vYZnDM6c-D0H1SstfwJ8ZA-2RtLEgAPNvd8VJXoJZ-TKXWXA2Hmw3GdMmMX-Wpe0BHDJ69oM7jvlfCBX54tb-z3xi7T0FDsKqUVLLwlZhs3L_4T93BZpweUw7HEC4607OC-LZ2u74SsUW3NCebVjewVRsrV3elIcCbjJbo3NYKc4Ky-_q7KPqyTyTnwQHvFswegxvmOlWJtiItdUbvWButDZdr4BWiU0uj8b4OONrNaNjeuFvTrJHwHlrUm3ACO4ZOj3d1hrYI9qCgxcSAOfuFeFjuPmMvz_UlDnTS5BDaaj8GhyQ",
			E = "AQAB"
		}
	});
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