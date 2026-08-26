
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using Protocol.Codec.Connection.Encryption;
using Protocol.Connection.Compression;
using Protocol.Packets;
using Protocol.Utility.IO;
using RakNet;
using EnderPearl.Logging;

namespace EnderPearl.Net
{
	public enum CompressionAlgorithm
	{
		ZLib = 0,
		Snappy = 1,
		None = 255
	}
	public class PacketSession
	{
		public bool mOpenCompression { get; set; }
		public bool mOpenCrypto { get; set; }
		public CompressionAlgorithm mCompressionAlgorithm { get; set; } = CompressionAlgorithm.None;
		public CryptoManager? mCryptoManager { get;set; }

		/// <summary>
		/// Pre-auth batch limiter hook (EnderPearl security.PreAuthBatchLimiter): returns the maximum
		/// decompressed batch size in bytes currently allowed, or 0 for unlimited. The listener wires
		/// this to lift as soon as the login succeeds.
		/// </summary>
		public Func<long>? MaxInboundBatchBytesProvider { get; set; }

		public required Action<List<IPacket>> OnPackets;

		/// <summary>
		/// ENDERPEARL_VERIFY_REENCODE=1: after decoding every packet, re-encode it from the parsed fields
		/// and diff against the original wire bytes. Any asymmetry between the library's Read and Write
		/// shows up here as a byte divergence - the definitive test for "decode corrupts re-encode".
		/// Output is throttled to the first few mismatches per packet type.
		/// </summary>
		public static readonly bool VerifyReencode =
			Environment.GetEnvironmentVariable("ENDERPEARL_VERIFY_REENCODE") == "1";

		private static readonly Dictionary<int, int> ReencodeMismatchCounts = new();
		private const int MAX_REENCODE_REPORTS_PER_TYPE = 3;

		public void HandleMinecraftGamePacket(McbeWrapper _wrapper)
		{
			if (mOpenCrypto)
			{
				_wrapper.payload = mCryptoManager?.Decrypt(_wrapper.payload.ToArray());
			}

			List<IPacket> outPackets;
			if (mOpenCompression)
			{
				byte compress_id = _wrapper.payload.Span[0];
				var compressId = (CompressionAlgorithm)compress_id;
				if (compressId == CompressionAlgorithm.None)
				{
					List<IPacket> packets = ReadPackets(_wrapper.payload[1..]);
					outPackets = packets;
				}
				else
				{
					var memory = Zlib.Decompress(_wrapper.payload[1..].Span).AsMemory();
					CheckPreAuthBatchLimit(memory.Length);
					List<IPacket> packets = ReadPackets(memory);
					outPackets = packets;
				}

			}
			else
			{
				CheckPreAuthBatchLimit(_wrapper.payload.Length);
				List<IPacket> packets = ReadPackets(_wrapper.payload);
				outPackets = packets;
			}

			OnPackets(outPackets);
		}

		private void CheckPreAuthBatchLimit(long size)
		{
			var provider = MaxInboundBatchBytesProvider;
			if (provider == null)
			{
				return;
			}
			long maxBytes = provider();
			if (maxBytes > 0 && size > maxBytes)
			{
				throw new IOException($"Pre-login batch of {size} bytes exceeds the maximum of {maxBytes} bytes");
			}
		}

		private List<IPacket> ReadPackets(ReadOnlyMemory<byte> _memory)
		{
			List<IPacket> _return = new List<IPacket>();
			using var reader = new MemoryStreamReader(_memory);

			while (reader.Position < _memory.Length)
			{
				uint len = 0;
				long pos = reader.Position;
				try
				{
					len = VarInt.ReadUInt32(reader);
					pos = reader.Position;
					if (len == 0 || pos + len > _memory.Length)
					{
						// Truncated or malformed batch tail: keep what decoded cleanly instead of
						// tearing the session down over it.
						break;
					}
					ReadOnlyMemory<byte> internalBuffer = _memory.Slice((int)(reader.Position), (int)len);
					// Packet id prefix is an UNSIGNED varint.
					int id = (int)VarInt.ReadUInt32(reader);

					IPacket packet = PacketRegistry.CreatePacket(id);
					packet.Decode(internalBuffer);
					if (VerifyReencode)
					{
						VerifyReencodeBytes(id, packet, internalBuffer);
					}
					_return.Add(packet);
				}
				catch
				{
					return _return;
				}
				reader.Position = pos + len;
			}
			if (reader.Length > reader.Position)
			{
				throw new Exception("Have more data");
			}
			return _return;
		}

		private static void VerifyReencodeBytes(int id, IPacket packet, ReadOnlyMemory<byte> original)
		{
			try
			{
				using var mem = new MemoryStream();
				VarInt.WriteInt32(mem, packet.PacketId);
				var writer = new MemoryStreamWriter(mem);
				packet.Write(writer);
				mem.Flush();
				byte[] reencoded = mem.ToArray();

				ReadOnlySpan<byte> orig = original.Span;
				bool same = reencoded.AsSpan().SequenceEqual(orig);
				if (same)
				{
					return;
				}
				lock (ReencodeMismatchCounts)
				{
					ReencodeMismatchCounts.TryGetValue(id, out int seen);
					ReencodeMismatchCounts[id] = seen + 1;
					if (seen >= MAX_REENCODE_REPORTS_PER_TYPE)
					{
						return;
					}
				}
				int min = Math.Min(reencoded.Length, orig.Length);
				int diff = 0;
				while (diff < min && reencoded[diff] == orig[diff])
				{
					diff++;
				}
				string origHex = Convert.ToHexString(orig.Slice(Math.Max(0, diff - 4), Math.Min(12, orig.Length - Math.Max(0, diff - 4))));
				string newHex = Convert.ToHexString(reencoded.AsSpan(Math.Max(0, diff - 4), Math.Min(12, reencoded.Length - Math.Max(0, diff - 4))));
				Logger.Info(
					$"[REENCODE MISMATCH] id={id} {packet.GetType().Name} wireLen={orig.Length} reLen={reencoded.Length} firstDiff@{diff} | wire[..{diff - 4}+]={origHex} re={newHex}");
			}
			catch (Exception e)
			{
				Logger.Info($"[REENCODE ERROR] id={id} {packet.GetType().Name}: {e.Message}");
			}
		}

		public McbeWrapper PackPackets(List<IPacket> _packets, CompressionAlgorithm compression = CompressionAlgorithm.None)
		{
			byte[] WritePackets(MemoryStream memory,ref List<IPacket> _list)
			{
				foreach (var packet in _packets)
				{
					if (!(packet.bytes.IsEmpty))
					{
						VarInt.WriteUInt32(memory, (uint)packet.bytes.Length);
						memory.Write(packet.bytes.Span);
					}
				}
				memory.Flush();
				return memory.ToArray();
			}
			
			using var stream = new MemoryStream();
			long length = 0;
			var wrapper = new McbeWrapper();
			foreach (var packet in _packets)
			{
				try
				{
						length += packet.Encode().Length;
				}
				catch (Exception e)
				{
						Debug.Assert(true);
				}
				
			}

			if (!mOpenCompression)
			{
				byte[] bytes = WritePackets(stream, ref _packets);
				wrapper.payload = bytes;
				return wrapper;
			}

			switch (compression)
			{
				case CompressionAlgorithm.ZLib:
				{
						byte[] bytes = WritePackets(stream, ref _packets);
						byte[] compressed = Zlib.Compress(bytes); 

						byte[] payload = new byte[compressed.Length + 1];
						payload[0] = (byte)compression;                          // 写入压缩算法标识
						Buffer.BlockCopy(compressed, 0, payload, 1, compressed.Length); // 拷贝压缩数据

						wrapper.payload = payload;
						break;
				}
				case CompressionAlgorithm.Snappy:
				{
						byte[] bytes = WritePackets(stream, ref _packets);
						byte[] compressed = Snappy.Compress(bytes);

						byte[] payload = new byte[compressed.Length + 1];
						payload[0] = (byte)compression;                          // 写入压缩算法标识
						Buffer.BlockCopy(compressed, 0, payload, 1, compressed.Length); // 拷贝压缩数据

						wrapper.payload = payload;
						break;
				}
				case CompressionAlgorithm.None:
				{
					stream.WriteByte((byte)compression);
					byte[] bytes = WritePackets(stream, ref _packets);
					wrapper.payload = bytes;
					break;
				}
				default:
					throw new IOException("Unknown Compression mode");
			}

			if (mOpenCrypto)
			{
				wrapper.payload = mCryptoManager?.Encrypt(wrapper.payload.ToArray());
			}
			return wrapper;
		}
	}
}
