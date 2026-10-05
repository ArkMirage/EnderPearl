using EnderPearl.Backend;
using EnderPearl.Core;
using EnderPearl.Player;

namespace EnderPearl.Command;

/// <summary>
/// Whoever ran a command, and where its output goes.
///
/// <para>Exists so <c>/glist</c>, <c>/alert</c>, <c>/send</c> and <c>/perm</c> have one
/// implementation each rather than one for chat and one for the console. The console is deliberately
/// not a special case inside those commands — it is a sender that happens to be an administrator and
/// prints to stdout. A null <see cref="connection"/> is the console; anything else wraps a player.</para>
/// </summary>
public sealed class CommandSender
{
	private readonly ProxyConnection? connection;

	private CommandSender(ProxyConnection? connection)
	{
		this.connection = connection;
	}

	public static CommandSender Console()
	{
		return new CommandSender(null);
	}

	public static CommandSender Of(ProxyConnection connection)
	{
		return new CommandSender(connection ?? throw new ArgumentNullException(nameof(connection)));
	}

	public string Name()
	{
		return connection == null ? "CONSOLE" : connection.ClientLogin.AuthData.DisplayName;
	}

	/// <summary>The XUID this sender is authorised as, or an empty string for the console.</summary>
	public string Xuid()
	{
		return connection == null ? "" : connection.ClientLogin.AuthData.Xuid;
	}

	/// <summary>The console answers true and bypasses every permission check.</summary>
	public bool IsConsole()
	{
		return connection == null;
	}

	public void SendMessage(string message)
	{
		if (connection == null)
		{
			Logger.Info(message);
		}
		else
		{
			BackendSwitcher.SendMessage(connection, message);
		}
	}

	/// <summary>The player who ran the command, or null for the console.</summary>
	public ProxyConnection? Connection()
	{
		return connection;
	}
}