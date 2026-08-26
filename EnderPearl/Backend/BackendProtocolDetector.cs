using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace EnderPearl.Backend
{
	/// <summary>
	/// Probes a backend's protocol by sending one RakNet unconnected ping over a throwaway UDP socket
	/// and parsing the pong's advertisement string. Used when backend.protocol is "auto".
	/// </summary>
	public sealed class BackendProtocolDetector
	{
		private const byte UNCONNECTED_PING = 0x01;
		private const byte UNCONNECTED_PONG = 0x1c;
		private static readonly byte[] RAKNET_MAGIC =
		{
			0x00, 0xff, 0xff, 0x00,
			0xfe, 0xfe, 0xfe, 0xfe,
			0xfd, 0xfd, 0xfd, 0xfd,
			0x12, 0x34, 0x56, 0x78
		};

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
				catch (Exception exception) when (exception is IOException or SocketException)
				{
					// Java's IOException covered its socket errors; .NET's SocketException is a separate
					// hierarchy, and letting it through here would end the retry loop on the first try.
					lastException = new IOException(exception.Message, exception);
				}
			}
			if (lastException != null)
			{
				throw lastException;
			}
			throw new IOException("Backend did not return a Bedrock pong: " + address);
		}

		private PongResult? Ping(IPEndPoint address)
		{
			byte[] request = BuildRequest();
			// Match the target's family so IPv6-configured backends are reachable too (Java's probe
			// socket followed the resolved address).
			using var socket = new Socket(address.Address.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
			socket.ReceiveTimeout = timeoutMillis;
			socket.SendTo(request, address);

			var response = new byte[4096];
			int length = socket.Receive(response);
			return ParsePong(response, length);
		}

		private static byte[] BuildRequest()
		{
			long pingTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
			long guid = Interlocked.Increment(ref requestCounter) | (Random.Shared.NextInt64() << 20);
			var ms = new MemoryStream();
			using (var w = new BinaryWriter(ms))
			{
				w.Write((byte)UNCONNECTED_PING);
				WriteBigEndian(w, pingTime);
				w.Write(RAKNET_MAGIC);
				WriteBigEndian(w, guid);
			}
			return ms.ToArray();
		}

		private static void WriteBigEndian(BinaryWriter writer, long value)
		{
			for (int shift = 56; shift >= 0; shift -= 8)
			{
				writer.Write((byte)((value >> shift) & 0xff));
			}
		}

		private static long requestCounter;

		internal static PongResult? ParsePong(byte[] response, int length)
		{
			if (length < 1 + 8 + 8 + RAKNET_MAGIC.Length + 2 || response[0] != UNCONNECTED_PONG)
			{
				return null;
			}

			int magicOffset = 1 + 8 + 8;
			for (int i = 0; i < RAKNET_MAGIC.Length; i++)
			{
				if (response[magicOffset + i] != RAKNET_MAGIC[i])
				{
					return null;
				}
			}

			int lengthOffset = magicOffset + RAKNET_MAGIC.Length;
			int pongLength = ((response[lengthOffset] & 0xff) << 8) | (response[lengthOffset + 1] & 0xff);
			int pongOffset = lengthOffset + 2;
			if (pongLength <= 0 || pongOffset + pongLength > length)
			{
				return null;
			}

			return ParseAdvertisement(response.AsSpan(pongOffset, pongLength));
		}

		/// <summary>
		/// Parses the Bedrock pong advertisement: edition;motd;protocol;version;players;max;serverId;subMotd;gameType;...
		/// </summary>
		internal static PongResult? ParseAdvertisement(ReadOnlySpan<byte> data)
		{
			string advertisement = System.Text.Encoding.UTF8.GetString(data.ToArray());
			string[] fields = advertisement.Split(';');
			if (fields.Length < 4)
			{
				return null;
			}
			if (!int.TryParse(fields[2], out int protocolVersion))
			{
				return null;
			}
			string version = fields[3];
			return new PongResult(protocolVersion, version);
		}
	}
}
