using System;

namespace EnderPearl.Backend
{
	/// <summary>Raised when a client/backend version pair cannot be served. This build serves exactly one pair.</summary>
	public sealed class UnsupportedVersionPairException : Exception
	{
		public UnsupportedVersionPairException(string message) : base(message)
		{
		}

		public UnsupportedVersionPairException(string message, Exception cause) : base(message, cause)
		{
		}
	}
}
