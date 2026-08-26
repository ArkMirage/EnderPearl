using System;
using System.Collections.Generic;
using System.Text;
using Org.BouncyCastle.Crypto;

namespace Protocol.Utils.Crypto
{
	public class CryptoContext
	{
		public bool UseEncryption;
		public IBufferedCipher Decryptor { get; set; }
		public IBufferedCipher Encryptor { get; set; }
		public long SendCounter = -1;

		public AsymmetricCipherKeyPair ClientKey { get; set; }
		public byte[] Key { get; set; }
	}
}
