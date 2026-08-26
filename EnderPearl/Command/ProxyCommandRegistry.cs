using EnderPearl.Config;

namespace EnderPearl.Command
{
	/// <summary>
	/// Every command the proxy owns, keyed by lowercase name.
	/// </summary>
	public sealed class ProxyCommandRegistry
	{
		private readonly LinkedHashMap<string, ProxyCommand> commands = new();

		public ProxyCommandRegistry(IEnumerable<ProxyCommand> commands)
		{
			if (commands == null)
			{
				throw new ArgumentNullException(nameof(commands));
			}
			foreach (ProxyCommand command in commands)
			{
				this.commands.Add(command.Name.ToLowerInvariant(), command);
			}
		}

		public static ProxyCommandRegistry Defaults()
		{
			return new ProxyCommandRegistry(ProxyCommands.Defaults());
		}

		public ICollection<ProxyCommand> Commands()
		{
			List<ProxyCommand> snapshot = new();
			foreach (ProxyCommand command in commands.Values)
			{
				snapshot.Add(command);
			}
			return snapshot;
		}

		public ProxyCommand? Find(string? commandLine)
		{
			string name = CommandName(commandLine);
			if (name.Length == 0)
			{
				return null;
			}
			return commands.TryGetValue(name, out ProxyCommand? command) ? command : null;
		}

		/// <summary>
		/// The command name a client typed, without its leading slash, its arguments or its case.
		///
		/// <para>Public because the interceptor has to read the name before it can decide who handles the
		/// line — a name a backend has taken over is forwarded rather than looked up here — and two
		/// copies of this parsing that disagreed would route a command one way and log it another.</para>
		/// </summary>
		public static string CommandName(string? commandLine)
		{
			if (commandLine == null)
			{
				return "";
			}
			string command = commandLine.Trim();
			if (command.StartsWith("/"))
			{
				command = command.Substring(1);
			}
			int firstSpace = command.IndexOf(' ');
			if (firstSpace >= 0)
			{
				command = command.Substring(0, firstSpace);
			}
			return command.ToLowerInvariant();
		}
	}
}
