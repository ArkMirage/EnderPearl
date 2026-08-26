using EnderPearl.Backend;

namespace EnderPearl.Command
{
	/// <summary>
	/// Whoever ran a command, and where its output goes.
	///
	/// <para>Exists so <c>/glist</c>, <c>/alert</c>, <c>/send</c> and <c>/perm</c> have one
	/// implementation each rather than one for chat and one for the console. The console is deliberately
	/// not a special case inside those commands — it is a sender that happens to be an administrator and
	/// prints to stdout.</para>
	/// </summary>
	public interface CommandSender
	{
		string Name();

		/// <summary>The XUID this sender is authorised as, or an empty string for the console.</summary>
		string Xuid();

		/// <summary>The console answers true and bypasses every permission check.</summary>
		bool IsConsole();

		void SendMessage(string message);

		/// <summary>The player who ran the command, or null for the console.</summary>
		ProxyConnection? Connection() => null;

		static CommandSender Console()
		{
			return ConsoleSender.INSTANCE;
		}

		static CommandSender Of(ProxyConnection connection)
		{
			return new PlayerCommandSender(connection);
		}
	}
}
