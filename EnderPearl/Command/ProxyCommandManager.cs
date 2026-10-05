namespace EnderPearl.Command;

/// <summary>
/// The one command manager: console commands and in-game commands live here together, and it is the
/// only way either place learns what exists.
///
/// <para>It declares nothing of its own. The proxy's built-in commands arrive through
/// <see cref="Register"/> from <see cref="SystemCommands"/>, a plugin's arrive through the same call,
/// and a plugin therefore needs no registration path the built-in set does not already use. Scope is
/// what keeps the two places apart: a lookup asks for the scope it is in, so <c>/stop</c> is unknown in
/// chat and <c>/server</c> is unknown at the terminal.</para>
/// </summary>
public sealed class ProxyCommandManager
{
	private readonly List<ProxyCommand> commands = new();
	private readonly Dictionary<string, Word> byName = new(StringComparer.Ordinal);

	/// <summary>One registered word: the command it runs, and where it is understood.</summary>
	private sealed class Word
	{
		public required ProxyCommand Command { get; init; }
		public required CommandScope Scopes { get; init; }
	}

	/// <summary>
	/// Registers a command under every name it answers to.
	///
	/// <para>A word two commands claim is refused outright: silently letting the later one win would
	/// make which command runs depend on registration order, which only shows up as the wrong command
	/// doing the wrong thing. A plugin that collides with a built-in therefore learns about it at
	/// registration rather than at the terminal, or worse, in chat.</para>
	///
	/// <para>The shape of the declaration is checked here too, because the pieces that have to agree are
	/// spread across the command and each of its names: a word reaching outside its command's scope, or a
	/// first name that does not follow it, would make the command unreachable somewhere it claims to
	/// live — silently, as an empty autocomplete or an "unknown command".</para>
	/// </summary>
	public void Register(ProxyCommand command)
	{
		if (command == null)
		{
			throw new ArgumentNullException(nameof(command));
		}
		for (int index = 0; index < command.Names.Count; index++)
		{
			CommandName name = command.Names[index];
			if (byName.ContainsKey(name.Name))
			{
				throw new ArgumentException("'" + name.Name + "' already runs another command");
			}
			CommandScope scopes = name.Effective(command.Scopes);
			if ((scopes & ~command.Scopes) != 0)
			{
				throw new ArgumentException(
					"'" + name.Name + "' answers in " + scopes + ", outside the command's " + command.Scopes);
			}
			if (index == 0 && scopes != command.Scopes)
			{
				throw new ArgumentException(
					"a command's first name must answer in " + command.Scopes + ", where the command lives; "
					+ "give '" + name.Name + "' the whole scope or move it after the command's own name");
			}
		}
		commands.Add(command);
		foreach (CommandName name in command.Names)
		{
			byName.Add(name.Name, new Word
			{
				Command = command,
				Scopes = name.Effective(command.Scopes)
			});
		}
	}

	/// <summary>Everything registered, in registration order — which is the order listings print.</summary>
	public IReadOnlyList<ProxyCommand> Commands()
	{
		return commands;
	}

	/// <summary>
	/// What is available in one place: <see cref="CommandScope.Console"/> for the terminal,
	/// <see cref="CommandScope.Game"/> for chat. Passing <see cref="CommandScope.Both"/> answers with
	/// everything, for a caller that wants the whole set rather than one place's view of it.
	/// </summary>
	public IReadOnlyList<ProxyCommand> Commands(CommandScope scope)
	{
		List<ProxyCommand> available = new();
		foreach (ProxyCommand command in commands)
		{
			if ((command.Scopes & scope) != 0)
			{
				available.Add(command);
			}
		}
		return available;
	}

	/// <summary>The command a typed line names in this scope, or null when nothing there answers to it.</summary>
	public ProxyCommand? Find(string? commandLine, CommandScope scope)
	{
		string name = CommandName(commandLine);
		if (name.Length == 0)
		{
			return null;
		}
		if (!byName.TryGetValue(name, out Word? word))
		{
			return null;
		}
		// The word's own scope rather than the command's: /say is the terminal's shorthand for /alert,
		// and asking for it in chat has to answer "not here" so the backend keeps its own /say.
		return (word.Scopes & scope) != 0 ? word.Command : null;
	}

	/// <summary>
	/// The command name a line names, without its leading slash, its arguments or its case.
	///
	/// <para>Public because more than one consumer reads the name before deciding anything — the
	/// interceptor has to know which command a line is about before it can hand it to a backend or run
	/// it, and the terminal has to do the same — and two copies of this parsing that disagreed would
	/// route a command one way and log it another.</para>
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