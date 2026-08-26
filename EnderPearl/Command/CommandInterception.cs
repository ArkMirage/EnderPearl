namespace EnderPearl.Command
{
	/// <summary>
	/// What the interceptor decided to do with a command request: either the proxy consumes it, or it
	/// is forwarded to the backend untouched.
	/// </summary>
	public abstract class CommandInterception
	{
		protected CommandInterception(string originalCommandLine)
		{
			OriginalCommandLine = originalCommandLine;
		}

		/// <summary>The command line exactly as the client sent it.</summary>
		public string OriginalCommandLine { get; }

		/// <summary>A proxy-owned command: the backend never sees this packet.</summary>
		public sealed class Consumed : CommandInterception
		{
			public Consumed(ProxyCommand command, string originalCommandLine) : base(originalCommandLine)
			{
				Command = command ?? throw new ArgumentNullException(nameof(command));
			}

			public ProxyCommand Command { get; }
		}

		/// <summary>Not one of ours: relayed verbatim.</summary>
		public sealed class Forward : CommandInterception
		{
			public Forward(string originalCommandLine) : base(originalCommandLine)
			{
			}
		}
	}
}
