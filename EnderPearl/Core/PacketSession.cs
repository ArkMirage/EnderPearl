using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using Protocol.Connection.Compression;
using Protocol.Packets;
using Protocol.Utility.IO;
using EnderPearl.Core;

namespace EnderPearl.Core;

public enum CompressionAlgorithm
{
	ZLib = 0,
	Snappy = 1,
	None = 255
}

public class PacketSession
{
	public bool mOpenCompression { get; set; }
	public CompressionAlgorithm mCompressionAlgorithm { get; set; } = CompressionAlgorithm.None;

	/// <summary>
	/// Pre-auth batch limiter hook: the maximum decompressed batch size in bytes currently allowed, or
	/// 0 for unlimited. The listener wires this to lift as soon as the login succeeds.
	/// </summary>
	public Func<long>? MaxInboundBatchBytesProvider { get; set; }

	public required Action<List<IPacket>> OnPackets;

	/// <summary>
	/// ENDERPEARL_VERIFY_REENCODE=1: after decoding every packet, re-encode it from the parsed fields and
	/// diff against the original wire bytes, so any asymmetry between the library's Read and Write shows up
	/// as a byte divergence. Output is throttled to the first few mismatches per packet type.
	/// </summary>
	public static readonly bool VerifyReencode =
		Environment.GetEnvironmentVariable("ENDERPEARL_VERIFY_REENCODE") == "1";

	private static readonly Dictionary<int, int> ReencodeMismatchCounts = new();
	private const int MAX_REENCODE_REPORTS_PER_TYPE = 3;

	public void HandleMinecraftGamePacket(McbeWrapper wrapper)
	{
		List<IPacket> outPackets = mOpenCompression
			? HandleCompressed(wrapper.payload)
			: ReadPackets(UnlimitedBatch(wrapper.payload));
		OnPackets(outPackets);
	}

	private List<IPacket> HandleCompressed(ReadOnlyMemory<byte> payload)
	{
		var compressId = (CompressionAlgorithm)payload.Span[0];
		if (compressId == CompressionAlgorithm.None)
		{
			return ReadPackets(payload[1..]);
		}
		ReadOnlyMemory<byte> memory = Zlib.Decompress(payload[1..].Span).AsMemory();
		CheckPreAuthBatchLimit(memory.Length);
		return ReadPackets(memory);
	}

	private ReadOnlyMemory<byte> UnlimitedBatch(ReadOnlyMemory<byte> payload)
	{
		CheckPreAuthBatchLimit(payload.Length);
		return payload;
	}

	private void CheckPreAuthBatchLimit(long size)
	{
		long maxBytes = MaxInboundBatchBytesProvider?.Invoke() ?? 0;
		if (maxBytes > 0 && size > maxBytes)
		{
			throw new IOException($"Pre-login batch of {size} bytes exceeds the maximum of {maxBytes} bytes");
		}
	}

	private List<IPacket> ReadPackets(ReadOnlyMemory<byte> memory)
	{
		var packets = new List<IPacket>();
		using var reader = new MemoryStreamReader(memory);

		var id = -1;
		while (reader.Position < memory.Length)
		{
			uint len = 0;
			long pos = reader.Position;
			try
			{
				len = VarInt.ReadUInt32(reader);
				pos = reader.Position;
				if (len == 0 || pos + len > memory.Length)
				{
					// Truncated or malformed batch tail: keep what decoded cleanly instead of tearing the
					// session down over it.
					break;
				}
				ReadOnlyMemory<byte> frame = memory.Slice((int)reader.Position, (int)len);
				id = (int)VarInt.ReadUInt32(reader);

				IPacket packet = PacketRegistry.CreatePacket(id);
				packet.Decode(frame);
				if (VerifyReencode)
				{
					VerifyReencodeBytes(id, packet, frame);
				}
				packets.Add(packet);
			}
			catch (Exception exception)
			{
				if (len == 0 || pos + len > memory.Length)
				{
					// The frame boundary is unknown, so there is nothing to resynchronise on: stop here
					// rather than guessing, but say so instead of dropping the tail in silence.
					Logger.Error($"Unreadable packet frame at offset {pos}: {exception.Message}");
					return packets;
				}
				// One frame failed to decode. Skip exactly that frame and keep the rest of the batch.
				Logger.Error($"Dropped packet id {id} ({len} bytes at offset {pos}): {exception.Message}");
			}
			// Step over this frame on every path that did not bail out early.
			reader.Position = pos + len;
		}
		if (reader.Length > reader.Position)
		{
			throw new Exception("Have more data");
		}
		return packets;
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
			if (reencoded.AsSpan().SequenceEqual(orig))
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
		catch (Exception exception)
		{
			Logger.Info($"[REENCODE ERROR] id={id} {packet.GetType().Name}: {exception.Message}");
		}
	}

	public McbeWrapper PackPackets(List<IPacket> packets, CompressionAlgorithm compression = CompressionAlgorithm.None)
	{
		byte[] WritePackets(MemoryStream stream)
		{
			foreach (IPacket packet in packets)
			{
				ReadOnlyMemory<byte> data = packet.Encode();
				if (!data.IsEmpty)
				{
					VarInt.WriteUInt32(stream, (uint)data.Length);
					stream.Write(data.Span);
				}
			}
			stream.Flush();
			return stream.ToArray();
		}

		var wrapper = new McbeWrapper();
		using var stream = new MemoryStream();

		if (!mOpenCompression)
		{
			wrapper.payload = WritePackets(stream);
			return wrapper;
		}

		switch (compression)
		{
			case CompressionAlgorithm.ZLib:
				wrapper.payload = WithPrefix(compression, Zlib.Compress(WritePackets(stream)));
				break;
			case CompressionAlgorithm.Snappy:
				wrapper.payload = WithPrefix(compression, Snappy.Compress(WritePackets(stream)));
				break;
			case CompressionAlgorithm.None:
				stream.WriteByte((byte)compression);
				wrapper.payload = WritePackets(stream);
				break;
			default:
				throw new IOException("Unknown Compression mode");
		}

		return wrapper;
	}

	private static byte[] WithPrefix(CompressionAlgorithm compression, byte[] body)
	{
		var payload = new byte[body.Length + 1];
		payload[0] = (byte)compression;
		Buffer.BlockCopy(body, 0, payload, 1, body.Length);
		return payload;
	}
}
