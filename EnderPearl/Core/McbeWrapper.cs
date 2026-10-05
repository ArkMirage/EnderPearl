using System;

namespace EnderPearl.Core;

public class McbeWrapper
{
	public ReadOnlyMemory<byte> payload;

	public virtual int PacketId { get; }

	public ReadOnlyMemory<byte> bytes { get; set; }

	public void Decode(ReadOnlyMemory<byte> data)
	{
		bytes = data;
		payload = data;
	}

	public ReadOnlyMemory<byte> Encode()
	{
		if (bytes.IsEmpty)
		{
			bytes = payload;
		}
		return bytes;
	}
}
