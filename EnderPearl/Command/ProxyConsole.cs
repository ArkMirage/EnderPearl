using System.Text;
using EnderPearl.Core;

namespace EnderPearl.Command
{
	/// <summary>
	/// Reads lines from the proxy's own terminal and runs the command each one names.
	///
	/// <para>This class is the reader and nothing else: it resolves a line to a
	/// <see cref="ProxyCommand"/> through <see cref="ProxyCommandManager"/> and invokes it. Which commands
	/// exist is decided entirely outside it — <see cref="SystemCommands"/> registers the proxy's own at
	/// startup and a plugin registers its own — and none of that reaches here.</para>
	///
	/// <para>Runs on a daemon thread. A proxy started without a terminal — under <c>nohup</c>, as a
	/// service, with stdin closed — reads end-of-stream once and retires the reader, carrying on serving
	/// players rather than blocking the process or spinning on a read that will never return.</para>
	///
	/// <para>The console is an administrator by definition (see <see cref="CommandSender"/>), which makes it
	/// the way out of a proxy nobody can administer: a fresh install with no <c>permissions.admins</c> entry
	/// can grant the first <c>admin</c> node from here.</para>
	/// </summary>
	public sealed class ProxyConsole
	{
		private readonly ProxyCommandManager manager;
		private readonly Stream input;
		private Thread? thread;
		private volatile bool running;

		public ProxyConsole(ProxyCommandManager manager)
			: this(manager, Console.OpenStandardInput())
		{
		}

		private ProxyConsole(ProxyCommandManager manager, Stream input)
		{
			this.manager = manager ?? throw new ArgumentNullException(nameof(manager));
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

		/// <summary>
		/// Runs one line as typed. A leading slash is accepted so an in-game command can be pasted in
		/// unchanged, and a blank line just earns the next prompt.
		/// </summary>
		public void Execute(string? line)
		{
			if (string.IsNullOrWhiteSpace(line))
			{
				return;
			}
			string trimmed = line.Trim();
			if (trimmed.StartsWith("/"))
			{
				trimmed = trimmed.Substring(1);
			}
			// Console scope: a command that only exists in chat, such as /server, cannot be run from here
			// and is reported unknown rather than half-run.
			ProxyCommand? command = manager.Find(trimmed, CommandScope.Console);
			if (command == null)
			{
				Logger.Error("Unknown command: " + ProxyCommandManager.CommandName(trimmed) + ". Type 'help' for commands.");
				return;
			}
			command.Handler(CommandSender.Console(), trimmed, CommandArguments.Split(trimmed));
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
						// End of stream is how a proxy without a terminal ends: there is no further input
						// to wait for, and pretending otherwise would log an error a second forever.
						Logger.Info("Console input closed; the proxy keeps running.");
						return;
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
	}
}