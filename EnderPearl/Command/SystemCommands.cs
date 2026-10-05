namespace EnderPearl.Command;

/// <summary>
/// The commands the proxy itself ships with, registered into the host's manager.
///
/// <para>These are registered exactly the way a plugin's commands will be — one
/// <see cref="ProxyCommand"/> per command, its scope, its arguments and its action together — through
/// the same <see cref="ProxyCommandManager.Register"/> a plugin calls. The built-in set is therefore
/// the worked example of plugin registration rather than a special case beside it.</para>
///
/// <para>Anything that acts on a player or on the network delegates to <see cref="NetworkCommands"/>
/// rather than reimplementing it, so an administrator at the terminal and one in chat run the same code
/// for the commands both of them have.</para>
/// </summary>
public static class SystemCommands
{
	/// <summary>
	/// Registers everything the proxy owns: the terminal's own <c>help</c>, <c>stop</c> and <c>kick</c>, and
	/// the in-game <c>hub</c>, <c>lobby</c>, <c>server</c>, <c>glist</c>, <c>send</c>, <c>alert</c> and
	/// <c>perm</c>. The four that make sense in both places are registered for both.
	///
	/// <para>Where the terminal used to have shorthand of its own — <c>?</c>, <c>end</c>, <c>list</c>,
	/// <c>say</c>, <c>permission</c> — those are kept as further names on the same command, narrowed to
	/// <see cref="CommandScope.Console"/>. Narrowing rather than dropping them is what keeps the
	/// shorthands without chat claiming <c>/say</c> or <c>/list</c> from the backends, and it is the
	/// same mechanism a plugin uses when it wants one word somewhere and another word elsewhere.</para>
	/// </summary>
	public static void Register(ProxyCommandManager manager, NetworkCommands networkCommands, Action shutdown)
	{
		if (manager == null)
		{
			throw new ArgumentNullException(nameof(manager));
		}
		if (networkCommands == null)
		{
			throw new ArgumentNullException(nameof(networkCommands));
		}
		if (shutdown == null)
		{
			throw new ArgumentNullException(nameof(shutdown));
		}

		// ------------------------------------------------------------- terminal only

		manager.Register(new ProxyCommand
		{
			Names = new[]
			{
				new CommandName { Name = "help" },
				// The terminal's shorthand, kept from before the command set was unified. It is not
				// offered to chat, where the backend has its own commands to answer with.
				new CommandName { Name = "?" }
			},
			Scopes = CommandScope.Console,
			Usage = "help",
			Description = "list every terminal command",
			Handler = (sender, _, _) => Help(sender, manager)
		});
		manager.Register(new ProxyCommand
		{
			Names = new[]
			{
				new CommandName { Name = "stop" },
				new CommandName { Name = "end" }
			},
			Scopes = CommandScope.Console,
			Usage = "stop",
			Description = "shut the proxy down",
			Handler = (sender, _, _) => Shutdown(sender, shutdown)
		});
		manager.Register(new ProxyCommand
		{
			Names = new[] { new CommandName { Name = "kick" } },
			Scopes = CommandScope.Console,
			Usage = "kick <player> [reason]",
			Description = "put a player back at the title screen",
			Handler = (sender, _, arguments) => networkCommands.Kick(sender, arguments)
		});

		// --------------------------------------------------------------- both places

		manager.Register(new ProxyCommand
		{
			Names = new[]
			{
				new CommandName { Name = "glist" },
				// Terminal-only, and only where it is safe: /list is a vanilla command, so claiming it in
				// chat would take the player list away from every backend.
				new CommandName { Name = "list", Scopes = CommandScope.Console }
			},
			Scopes = CommandScope.Both,
			Usage = "glist",
			Description = "who is online, and where",
			Handler = (sender, _, _) => networkCommands.Glist(sender)
		});
		manager.Register(new ProxyCommand
		{
			Names = new[] { new CommandName { Name = "send" } },
			Scopes = CommandScope.Both,
			Usage = "send <player|all> <server>",
			Description = "move a player to a backend server",
			Parameters = new[]
			{
				new CommandParameter { Kind = CommandParameterKind.Player, Name = "player" },
				new CommandParameter { Kind = CommandParameterKind.BackendName, Name = "server" }
			},
			Handler = (sender, _, arguments) => networkCommands.Send(sender, arguments)
		});
		manager.Register(new ProxyCommand
		{
			Names = new[]
			{
				new CommandName { Name = "alert" },
				// The same word vanilla uses for the same thing, so it is the terminal's alone: in chat
				// the backend's /say is the one the player expects.
				new CommandName { Name = "say", Scopes = CommandScope.Console }
			},
			Scopes = CommandScope.Both,
			Usage = "alert <message>",
			Description = "broadcast a message to every player",
			Parameters = new[]
			{
				new CommandParameter { Kind = CommandParameterKind.FreeText, Name = "message" }
			},
			Handler = (sender, commandLine, _) => networkCommands.Alert(sender, CommandArguments.Remainder(commandLine))
		});
		manager.Register(new ProxyCommand
		{
			Names = new[]
			{
				new CommandName { Name = "perm" },
				new CommandName { Name = "permission", Scopes = CommandScope.Console }
			},
			Scopes = CommandScope.Both,
			Usage = "perm set|unset|info|list|permission ...",
			Description = "grant or revoke proxy permissions",
			// One declaration covers all four forms: the trailing arguments are optional because
			// `list` takes neither of them and `info` takes only the player.
			Parameters = new[]
			{
				new CommandParameter { Kind = CommandParameterKind.PermissionAction, Name = "action" },
				new CommandParameter { Kind = CommandParameterKind.Player, Name = "player", IsOptional = true },
				new CommandParameter { Kind = CommandParameterKind.PermissionNode, Name = "node", IsOptional = true }
			},
			Handler = (sender, _, arguments) => networkCommands.Permission(sender, arguments)
		});

		// ------------------------------------------------------------------ in game

		manager.Register(new ProxyCommand
		{
			Names = new[] { new CommandName { Name = "hub" } },
			Scopes = CommandScope.Game,
			Usage = "hub",
			Description = "send yourself to the fallback hub",
			Handler = (sender, _, _) => networkCommands.Hub(sender)
		});
		manager.Register(new ProxyCommand
		{
			Names = new[] { new CommandName { Name = "lobby" } },
			Scopes = CommandScope.Game,
			Usage = "lobby",
			Description = "send yourself to the fallback lobby",
			Handler = (sender, _, _) => networkCommands.Hub(sender)
		});
		manager.Register(new ProxyCommand
		{
			Names = new[] { new CommandName { Name = "server" } },
			Scopes = CommandScope.Game,
			Usage = "server [name]",
			Description = "list or switch backend servers",
			// Optional rather than a second overload: the same trick /perm uses, so the client
			// completes the server name while still accepting a bare /server.
			Parameters = new[]
			{
				new CommandParameter { Kind = CommandParameterKind.BackendName, Name = "name", IsOptional = true }
			},
			Handler = (sender, _, arguments) => networkCommands.Server(sender, arguments)
		});
	}

	/// <summary>
	/// The terminal's listing, laid out from what the manager holds for the terminal — so it documents
	/// the built-in commands and a plugin's together, and a command cannot be registered for the
	/// console without also being listed here. Commands that only exist in chat are left out, since
	/// they cannot be run from where this listing is read.
	/// </summary>
	private static void Help(CommandSender sender, ProxyCommandManager manager)
	{
		IReadOnlyList<ProxyCommand> commands = manager.Commands(CommandScope.Console);
		int width = 0;
		foreach (ProxyCommand command in commands)
		{
			width = Math.Max(width, command.Usage.Length);
		}
		foreach (ProxyCommand command in commands)
		{
			sender.SendMessage(command.Usage.PadRight(width) + "  " + command.Description);
		}
	}

	private static void Shutdown(CommandSender sender, Action shutdown)
	{
		sender.SendMessage("Stopping the proxy.");
		shutdown();
	}
}