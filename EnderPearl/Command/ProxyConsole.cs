using System.Text;
using EnderPearl.Logging;

namespace EnderPearl.Command
{
	/// <summary>
	/// Reads commands from the proxy's own terminal.
	///
	/// <para>Runs on a daemon thread so a proxy started without a console — under <c>nohup</c>, as a
	/// service, with stdin closed — simply sees end-of-stream and carries on serving players rather than
	/// blocking a process on a read that will never return.</para>
	///
	/// <para>The console is an administrator by definition (see <see cref="ConsoleSender"/>), which makes it the
	/// way out of a proxy nobody can administer: a fresh install with no <c>permissions.admins</c> entry
	/// can grant the first <c>admin</c> node from here.</para>
	/// </summary>
	public sealed class ProxyConsole
	{
		private readonly NetworkCommands networkCommands;
		private readonly Action shutdown;
		private readonly Stream input;
		private volatile bool running;
		private Thread? thread;

		public ProxyConsole(NetworkCommands networkCommands, Action shutdown)
			: this(networkCommands, shutdown, Console.OpenStandardInput())
		{
		}

		internal ProxyConsole(NetworkCommands networkCommands, Action shutdown, Stream input)
		{
			this.networkCommands = networkCommands ?? throw new ArgumentNullException(nameof(networkCommands));
			this.shutdown = shutdown ?? throw new ArgumentNullException(nameof(shutdown));
			this.input = input ?? throw new ArgumentNullException(nameof(input));
		}

		public void Start()
		{
			if (thread != null)
			{
				return;
			}
			running = true;
			thread = new Thread(ReadLoop)
			{
				Name = "proxy-console",
				IsBackground = true
			};
			thread.Start();
			Logger.Info("Console ready. Type 'help' for commands.");
		}

		public void Stop()
		{
			running = false;
		}

		private void ReadLoop()
		{
			try
			{
				using StreamReader reader = new(input, Encoding.UTF8);
				while (running)
				{
					Console.Write(">");
					string? line = reader.ReadLine();
					if (line == null)
					{
						Logger.Error("Unknown command!");
						continue;
					}

					if (line == string.Empty)
					{
						Logger.Error("Unknown command!");
						continue;
					}
					try
					{
						Execute(line);
					}
					catch (Exception exception)
					{
						// One bad command must not take the console down for the rest of the run.
						Logger.Error($"Command failed: {exception.GetType().Name}: {exception.Message}");
					}
				}
			}
			catch (IOException exception)
			{
				Logger.Info($"Console closed: {exception.Message}.");
			}
		}

		/// <summary>Internal for tests. Accepts a leading slash so pasting an in-game command works.</summary>
		internal void Execute(string? line)
		{
			if (string.IsNullOrWhiteSpace(line))
			{
				return;
			}
			CommandSender sender = CommandSender.Console();
			string trimmed = line.Trim();
			if (trimmed.StartsWith("/"))
			{
				trimmed = trimmed.Substring(1);
			}
			// CommandArguments drops the leading command word, exactly as it does for a chat command.
			string command = trimmed.Split((char[]?)null, 2)[0].ToLowerInvariant();
			List<string> arguments = CommandArguments.Split(trimmed);

			switch (command)
			{
				case "help":
				case "?":
					Help(sender);
					break;
				case "glist":
				case "list":
					networkCommands.Glist(sender);
					break;
				case "send":
					networkCommands.Send(sender, arguments);
					break;
				case "alert":
				case "say":
					networkCommands.Alert(sender, CommandArguments.Remainder(trimmed));
					break;
				case "perm":
				case "permission":
					networkCommands.Permission(sender, arguments);
					break;
				case "stop":
				case "end":
					sender.SendMessage("Stopping the proxy.");
					shutdown();
					break;
				default:
					Logger.Error("Unknown command: " + command + ". Type 'help' for commands.");
					break;
			}
		}

		private void Help(CommandSender sender)
		{
			sender.SendMessage("glist                      - who is online, and where");
			sender.SendMessage("send <player|all> <server> - move a player");
			sender.SendMessage("alert <message>            - broadcast to everyone");
			sender.SendMessage("perm set <player> <node>   - grant a permission");
			sender.SendMessage("perm unset <player> <node> - revoke a permission");
			sender.SendMessage("perm info <player>         - what a player may do");
			sender.SendMessage("perm list                  - every runtime grant");
			sender.SendMessage("stop                       - shut the proxy down");
		}
	}
}
