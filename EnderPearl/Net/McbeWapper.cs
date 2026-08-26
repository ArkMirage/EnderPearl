using RakNet.Messages;
using System;
using System.Collections.Generic;
using System.Text;
using Protocol.Packets;
using Protocol.Utility.IO;

namespace EnderPearl.Net
{
	public class McbeWrapper
	{
		public ReadOnlyMemory<byte> payload;

		public virtual int PacketId { get; }
		public ReadOnlyMemory<byte> bytes { get; set; }

		public void Decode(ReadOnlyMemory<byte> data)
		{
			bytes = data;
			using (var mem = new MemoryStreamReader(data))
			{
				mem.ReadByte();
				payload = mem.Read(mem.Length - mem.Position);
			}
		}

		public ReadOnlyMemory<byte> Encode()
		{
			if (bytes.IsEmpty)
			{
				using (var mem = new MemoryStream())
				{
					var writer = new MemoryStreamWriter(mem);
					writer.WriteByte(0xfe);
					writer.Write(payload);
					mem.Flush();
					bytes = mem.ToArray();
				}
			}

			return bytes;
		}
	}
}
