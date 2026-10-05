using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading;
using NetherNet.Endpoint;

namespace EnderPearl.Backend
{
	/// <summary>
	/// Probes a backend's protocol by requesting its NetherNet status over HTTP
	/// (<c>GET /v1/join</c>) and reading the advertised protocol and version.
	/// </summary>
	public sealed class BackendProtocolDetector
	{
		private readonly int timeoutMillis;
		private readonly int attempts;

		public BackendProtocolDetector() : this(1_500, 2)
		{
		}

		internal BackendProtocolDetector(int timeoutMillis, int attempts)
		{
			if (timeoutMillis <= 0)
			{
				throw new ArgumentException("timeoutMillis must be positive");
			}
			if (attempts <= 0)
			{
				throw new ArgumentException("attempts must be positive");
			}
			this.timeoutMillis = timeoutMillis;
			this.attempts = attempts;
		}

		public sealed record PongResult(int ProtocolVersion, string Version);

		public PongResult Detect(IPEndPoint address)
		{
			IOException? lastException = null;
			for (int attempt = 0; attempt < attempts; attempt++)
			{
				try
				{
					PongResult? pong = Ping(address);
					if (pong != null)
					{
						return pong;
					}
				}
				catch (Exception exception)
				{
					lastException = new IOException(exception.Message, exception);
				}
			}
			if (lastException != null)
			{
				throw lastException;
			}
			throw new IOException("Backend did not return a Bedrock status: " + address);
		}

		private PongResult? Ping(IPEndPoint address)
		{
			string host = address.AddressFamily == AddressFamily.InterNetworkV6
				? "[" + address.Address + "]"
				: address.Address.ToString();
			string url = $"http://{host}:{address.Port}";

			var client = new Client(new ClientConfig());
			using var cts = new CancellationTokenSource(timeoutMillis);
			Status status;
			try
			{
				status = client.StatusAsync(cts.Token, url).GetAwaiter().GetResult();
			}
			catch (JsonException)
			{
				// A 200 with no JSON body: the backend speaks NetherNet but advertises no status.
				// Not a failure - the caller falls back to assuming the proxy's own protocol.
				return null;
			}
			return new PongResult(status.Protocol, status.Version);
		}
	}
}