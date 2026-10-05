namespace EnderPearl.Command;

/// <summary>
/// One command the proxy owns, in either place it can be run from: the words that trigger it, where it
/// is available, how help and the in-game command tree describe it, the arguments it takes, and what it
/// does.
///
/// <para>A command is a declaration, not a branch. Everything the proxy can run arrives through
/// <see cref="ProxyCommandManager.Register"/> — the built-in set from <see cref="SystemCommands"/>, a
/// plugin's from the plugin — and both the terminal and the in-game command tree are driven from what
/// was registered. A command therefore cannot be added in one place and forgotten in the other, which
/// is exactly what happened while the two had a switch each.</para>
/// </summary>
public sealed class ProxyCommand
{
	private IReadOnlyList<CommandName> names = Array.Empty<CommandName>();

	/// <summary>
	/// Every word that runs this command.
	///
	/// <para>The first is the command's own name, and answers wherever the command lives; the ones after
	/// it are alternates, each free to narrow itself to one place. That is how <c>/say</c> stays the
	/// terminal's shorthand for <c>/alert</c> without chat claiming a word Minecraft already uses.</para>
	/// </summary>
	public required IReadOnlyList<CommandName> Names
	{
		get => names;
		init => names = Normalise(value);
	}

	/// <summary>
	/// Where the command lives, and the default for words that declare nothing themselves. Keeping a
	/// command for <see cref="CommandScope.Console"/> alone is how <c>/stop</c> stays a terminal-only
	/// word the backends never see.</summary>
	public required CommandScope Scopes { get; init; }

	/// <summary>The command as the terminal's help spells it, arguments included.</summary>
	public required string Usage { get; init; }

	/// <summary>One line, used by help and sent to the client as the command's description.</summary>
	public required string Description { get; init; }

	/// <summary>
	/// The arguments in order, which is also the completion the in-game command tree advertises. Empty
	/// for a command that takes none.
	/// </summary>
	public IReadOnlyList<CommandParameter> Parameters { get; init; } = Array.Empty<CommandParameter>();

	/// <summary>
	/// Runs it. The sender carries who asked — the console, or a player whose connection is
	/// <see cref="CommandSender.Connection"/> — and the line arrives as typed with any leading slash
	/// stripped, so a free-text argument such as <c>/alert</c>'s reads the remainder of the line
	/// instead of an argument token.
	/// </summary>
	public required Action<CommandSender, string, List<string>> Handler { get; init; }

	private static IReadOnlyList<CommandName> Normalise(IReadOnlyList<CommandName>? names)
	{
		if (names == null || names.Count == 0)
		{
			throw new ArgumentException("a command needs at least one name");
		}
		return new List<CommandName>(names);
	}
}