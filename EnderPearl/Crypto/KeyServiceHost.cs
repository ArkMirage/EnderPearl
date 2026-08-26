using System;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;

namespace EnderPearl.Crypto
{
	/// <summary>
	/// A loopback-only HTTP endpoint that publishes this proxy's mimic signing identity as a JWKS
	/// document. 末影之眼 (Ender Eye) polls it and serves the same set at the intercepted
	/// authorization.franchise.minecraft-services.net/.well-known/keys URL, which is what makes
	/// RS256 tokens signed here verify as if Microsoft had issued them.
	///
	/// <p>Bound to 127.0.0.1 only: the key set is public material, but there is no reason to expose
	/// it beyond the machine, and loopback HTTP prefixes do not require URL ACL administration.</p>
	/// </summary>
	public static class KeyServiceHost
	{
		public static void Start(int port, MojangMimicIdentity identity)
		{
			var listener = new HttpListener();
			listener.Prefixes.Add($"http://127.0.0.1:{port}/keys/");
			listener.Prefixes.Add($"http://127.0.0.1:{port}/.well-known/keys/");
			listener.Start();

			string jwks = identity.BuildJwksJson();
			byte[] body = Encoding.UTF8.GetBytes(jwks);

			var thread = new Thread(() =>
			{
				while (listener.IsListening)
				{
					HttpListenerContext context;
					try
					{
						context = listener.GetContext();
					}
					catch (Exception)
					{
						return;
					}
					try
					{
						context.Response.ContentType = "application/json";
						context.Response.ContentLength64 = body.Length;
						context.Response.OutputStream.Write(body, 0, body.Length);
						context.Response.OutputStream.Close();
					}
					catch (Exception)
					{
						try { context.Response.Abort(); } catch { }
					}
				}
			})
			{
				Name = "key-service",
				IsBackground = true
			};
			thread.Start();
		}
	}
}
