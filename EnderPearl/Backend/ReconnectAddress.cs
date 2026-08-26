namespace EnderPearl.Backend
{
	/// <summary>
	/// A host and port to send a reconnecting client to.
	///
	/// <para>Its own type because the strings this is parsed from are not as simple as they look. A Bedrock
	/// client's <c>ServerAddress</c> claim carries the port — <c>play.example.com:19132</c> — while
	/// <c>TransferPacket</c> wants the host on its own and the port as a number. Handing the client the
	/// joined form back produces "invalid IP address" and drops it off the proxy entirely, which is worse
	/// than not offering the move at all.</para>
	///
	/// <para>IPv6 is the reason this cannot just split on the last colon: a bare <c>::1</c> is all colons
	/// and no port, and <c>[::1]:19132</c> puts the host in brackets. Both reach a proxy on the same
	/// machine, which is exactly where this gets exercised first.</para>
	/// </summary>
	public sealed record ReconnectAddress(string Host, int Port)
	{
		/// <param name="raw"><c>host</c>, <c>host:port</c>, <c>[v6]</c> or <c>[v6]:port</c></param>
		/// <param name="fallbackPort">used when the string carries no usable port</param>
		/// <returns>the parsed address, or null when there is no host to speak of</returns>
		public static ReconnectAddress? Parse(string? raw, int fallbackPort)
		{
			if (raw == null)
			{
				return null;
			}
			string value = raw.Trim();
			if (value.Length == 0)
			{
				return null;
			}

			if (value.StartsWith("["))
			{
				int close = value.IndexOf(']');
				if (close < 0)
				{
					return null;
				}
				string host = value.Substring(1, close - 1);
				int port = value.Length > close + 1 && value[close + 1] == ':'
					? ParsePort(value.Substring(close + 2), fallbackPort)
					: fallbackPort;
				return host.Trim().Length == 0 ? null : new ReconnectAddress(host, port);
			}

			int separator = value.IndexOf(':');
			// More than one colon and no brackets is a bare IPv6 literal, not a host and port.
			if (separator < 0 || value.IndexOf(':', separator + 1) >= 0)
			{
				return new ReconnectAddress(value, fallbackPort);
			}

			string plainHost = value.Substring(0, separator);
			if (plainHost.Trim().Length == 0)
			{
				return null;
			}
			return new ReconnectAddress(plainHost, ParsePort(value.Substring(separator + 1), fallbackPort));
		}

		private static int ParsePort(string value, int fallbackPort)
		{
			if (!int.TryParse(value.Trim(), out int port))
			{
				return fallbackPort;
			}
			// A nonsense port is not worth refusing the move over; the listener's own is right in
			// every ordinary install.
			return port > 0 && port <= 65_535 ? port : fallbackPort;
		}
	}
}
