using EnderPearl.Backend;
using EnderPearl.Config;
using EnderPearl.Permission;
using EnderPearl.Session;
using EnderPearl.Logging;

namespace EnderPearl.Command
{
	/// <summary>
	/// The commands that act on the network rather than on the caller: <c>/glist</c>, <c>/send</c>,
	/// <c>/alert</c> and <c>/perm</c>.
	///
	/// <para>Written against <see cref="CommandSender"/> so the console and an in-game administrator run exactly
	/// the same code. Permission is checked here, at execution, and not only when the command tree is
	/// built — hiding a command from autocomplete does not stop a client sending the packet.</para>
	/// </summary>
	public sealed class NetworkCommands
	{
		private readonly ConnectedPlayerRegistry? connectedPlayers;
		private readonly BackendDirectory backendDirectory;
		private readonly BackendSwitcher switcher;
		private readonly ProxyPermissions permissions;
		private readonly ProxyCommandRegistry? commandRegistry;
		private readonly Action onPermissionsChanged;

		public NetworkCommands(
			ConnectedPlayerRegistry? connectedPlayers,
			BackendDirectory backendDirectory,
			BackendSwitcher switcher,
			ProxyPermissions permissions,
			ProxyCommandRegistry? commandRegistry,
			Action? onPermissionsChanged
		)
		{
			this.connectedPlayers = connectedPlayers;
			this.backendDirectory = backendDirectory ?? throw new ArgumentNullException(nameof(backendDirectory));
			this.switcher = switcher ?? throw new ArgumentNullException(nameof(switcher));
			this.permissions = permissions ?? throw new ArgumentNullException(nameof(permissions));
			this.commandRegistry = commandRegistry;
			this.onPermissionsChanged = onPermissionsChanged ?? delegate
			{
			};
		}

		/// <returns>false when the sender may not run this command, having been told so</returns>
		public bool Authorize(CommandSender sender, string commandName)
		{
			if (sender.IsConsole() || permissions.Allows(sender.Xuid(), sender.Name(), commandName))
			{
				return true;
			}
			Logger.Info($"Denied /{commandName} from {sender.Name()} ({sender.Xuid()}): not permitted.");
			sender.SendMessage("You do not have permission to use /" + commandName + ".");
			return false;
		}

		// ------------------------------------------------------------------ glist

		/// <summary>Who is online and where, grouped by backend so an empty backend is visible as empty.</summary>
		public void Glist(CommandSender sender)
		{
			if (connectedPlayers == null)
			{
				sender.SendMessage("The player list is unavailable.");
				return;
			}
			LinkedHashMap<string, List<string>> byBackend = new();
			foreach (BackendConfig backend in backendDirectory.Backends())
			{
				byBackend.Add(backend.Name, new List<string>());
			}
			int total = 0;
			foreach (ProxyConnection player in connectedPlayers.Connections())
			{
				// Registration happens at login, before a backend has been chosen, so someone who is
				// still handshaking has no backend name to group under yet.
				string backendName = player.BackendName() == null ? "connecting" : player.BackendName()!;
				GetOrCompute(byBackend, backendName)
					.Add(player.ClientLogin.AuthData.DisplayName);
				total++;
			}
			foreach (KeyValuePair<string, List<string>> entry in byBackend)
			{
				sender.SendMessage(string.Format(
					"[{0}] ({1}): {2}",
					entry.Key,
					entry.Value.Count,
					entry.Value.Count == 0 ? "-" : string.Join(", ", entry.Value)
				));
			}
			sender.SendMessage(total + " player(s) online.");
		}

		private static List<string> GetOrCompute(LinkedHashMap<string, List<string>> map, string key)
		{
			if (!map.TryGetValue(key, out List<string>? names))
			{
				names = new List<string>();
				map.Add(key, names);
			}
			return names;
		}

		// ------------------------------------------------------------------- send

		public void Send(CommandSender sender, List<string> arguments)
		{
			if (arguments.Count < 2)
			{
				sender.SendMessage("Usage: /send <player|all> <server>");
				return;
			}
			if (connectedPlayers == null)
			{
				sender.SendMessage("The player list is unavailable.");
				return;
			}
			string targetName = arguments[0];
			string backendName = arguments[1];
			BackendConfig? backend = backendDirectory.Find(backendName);
			if (backend == null)
			{
				sender.SendMessage("Unknown server: " + backendName);
				return;
			}

			if (ProxyPlayerEnum.ALL.Equals(targetName, StringComparison.OrdinalIgnoreCase))
			{
				int moved = 0;
				foreach (ProxyConnection player in connectedPlayers.Connections())
				{
					if (IsInGame(player) && !backend.Name.Equals(player.BackendName() ?? "null", StringComparison.OrdinalIgnoreCase))
					{
						switcher.SwitchBackend(player, backend);
						moved++;
					}
				}
				sender.SendMessage("Sending " + moved + " player(s) to " + backend.Name + ".");
				return;
			}

			ProxyConnection? target = connectedPlayers.FindByName(targetName);
			if (target != null)
			{
				if (!IsInGame(target))
				{
					sender.SendMessage(target.ClientLogin.AuthData.DisplayName
						+ " is still connecting and cannot be moved yet.");
					return;
				}
				sender.SendMessage(string.Format(
					"Sending {0} to {1}.",
					target.ClientLogin.AuthData.DisplayName,
					backend.Name
				));
				switcher.SwitchBackend(target, backend);
			}
			else
			{
				sender.SendMessage("No player named '" + targetName + "' is online.");
			}
		}

		// ------------------------------------------------------------------ alert

		public void Alert(CommandSender sender, string message)
		{
			if (message == null || message.Trim().Length == 0)
			{
				sender.SendMessage("Usage: /alert <message>");
				return;
			}
			if (connectedPlayers == null)
			{
				sender.SendMessage("The player list is unavailable.");
				return;
			}
			string broadcast = "[Alert] " + message;
			int delivered = 0;
			foreach (ProxyConnection player in connectedPlayers.Connections())
			{
				if (IsInGame(player))
				{
					BackendSwitcher.SendMessage(player, broadcast);
					delivered++;
				}
			}
			Logger.Info($"{sender.Name()} broadcast an alert to {delivered} player(s): {message}");
			sender.SendMessage("Alert sent to " + delivered + " player(s).");
		}

		// ------------------------------------------------------------------- perm

		/// <summary>
		/// <c>/perm set|unset|info|list [player] [node]</c>.
		///
		/// <para>The console can always run this, which is what stops a proxy becoming unadministrable: an
		/// operator with no <c>permissions.admins</c> entry grants themselves <c>admin</c> from the
		/// terminal and carries on in game.</para>
		/// </summary>
		public void Permission(CommandSender sender, List<string> arguments)
		{
			if (arguments.Count == 0)
			{
				PermissionUsage(sender);
				return;
			}
			string action = arguments[0].ToLowerInvariant();
			switch (action)
			{
				case "list":
				{
					PermissionList(sender);
					break;
				}
				case "info":
				{
					if (arguments.Count < 2)
					{
						sender.SendMessage("Usage: /perm info <player>");
						return;
					}
					PermissionInfo(sender, arguments[1]);
					break;
				}
				case "set":
				case "unset":
				{
					if (arguments.Count < 3)
					{
						sender.SendMessage("Usage: /perm " + action + " <player> <node>");
						return;
					}
					PermissionWrite(sender, "set".Equals(action, StringComparison.Ordinal), arguments[1], arguments[2]);
					break;
				}
				default:
				{
					PermissionUsage(sender);
					break;
				}
			}
		}

		private void PermissionUsage(CommandSender sender)
		{
			sender.SendMessage("Usage: /perm set|unset <player> <node>, /perm info <player>, /perm list");
			sender.SendMessage("Nodes: " + string.Join(", ", KnownNodes()));
		}

		private void PermissionList(CommandSender sender)
		{
			LinkedHashMap<string, IReadOnlySet<string>> subjects = permissions.Subjects();
			if (subjects.Count == 0)
			{
				sender.SendMessage("Nobody has been granted anything at runtime.");
			}
			else
			{
				foreach (KeyValuePair<string, IReadOnlySet<string>> subject in subjects)
				{
					sender.SendMessage(subject.Key + ": " + string.Join(", ", Sorted(subject.Value)));
				}
			}
			IReadOnlySet<string> configured = permissions.Config.Admins;
			if (configured.Count > 0)
			{
				sender.SendMessage("From config (permissions.admins, not editable here): "
					+ string.Join(", ", Sorted(configured)));
			}
		}

		private void PermissionInfo(CommandSender sender, string subject)
		{
			IReadOnlySet<string> nodes = permissions.NodesOf(subject);
			sender.SendMessage(subject + (nodes.Count == 0
				? " has no runtime permissions."
				: ": " + string.Join(", ", Sorted(nodes))));
			// Resolved answers matter more than the raw nodes: the config grants are invisible above,
			// and an "admin" node makes every other line redundant.
			bool admin = permissions.IsAdmin(subject, subject);
			sender.SendMessage("  administrator: " + (admin ? "true" : "false"));
			foreach (string command in CommandNames())
			{
				if (permissions.IsAdminCommand(command))
				{
					sender.SendMessage("  /" + command + ": "
						+ YesNo(permissions.Allows(subject, subject, command)));
				}
			}
			foreach (string backend in BackendNames())
			{
				if (permissions.IsAdminBackend(backend))
				{
					sender.SendMessage("  server " + backend + ": "
						+ YesNo(permissions.MayJoinBackend(subject, subject, backend)));
				}
			}
		}

		private void PermissionWrite(CommandSender sender, bool granting, string subject, string node)
		{
			string normalized = node.Trim().ToLowerInvariant();
			if (!KnownNodes().Contains(normalized))
			{
				// A typo would otherwise be stored happily and never take effect, which looks exactly
				// like the permission system being broken.
				sender.SendMessage("Unknown permission node: " + node);
				sender.SendMessage("Nodes: " + string.Join(", ", KnownNodes()));
				return;
			}
			try
			{
				bool changed = granting
					? permissions.Grant(subject, normalized)
					: permissions.Revoke(subject, normalized);
				if (!changed)
				{
					sender.SendMessage(granting
						? subject + " already has " + normalized + "."
						: subject + " does not have " + normalized + ".");
					return;
				}
			}
			catch (ArgumentException exception)
			{
				sender.SendMessage("Cannot store that: " + exception.Message);
				return;
			}
			Logger.Info($"{sender.Name()} {(granting ? "granted" : "revoked")} {normalized} for {subject}.");
			sender.SendMessage((granting ? "Granted " : "Revoked ") + normalized + " for " + subject + ".");
			// The command tree advertises what a player may use, so it has to be rebuilt for anyone
			// whose access just changed — otherwise the grant only takes effect on their next join.
			onPermissionsChanged();
		}

		public List<string> KnownNodes()
		{
			return ProxyPermissions.KnownNodes(CommandNames(), BackendNames());
		}

		private List<string> CommandNames()
		{
			if (commandRegistry == null)
			{
				return new List<string>();
			}
			List<string> names = new();
			foreach (ProxyCommand command in commandRegistry.Commands())
			{
				names.Add(command.Name);
			}
			return names;
		}

		private List<string> BackendNames()
		{
			return new List<string>(backendDirectory.BackendNames());
		}

		/// <summary>Java's TreeSet display ordering for a set of strings.</summary>
		private static List<string> Sorted(IReadOnlySet<string> values)
		{
			List<string> sorted = new(values);
			sorted.Sort(StringComparer.Ordinal);
			return sorted;
		}

		/// <summary>The console prints Java booleans, which are lower case.</summary>
		private static string YesNo(bool value)
		{
			return value ? "true" : "false";
		}

		/// <summary>
		/// Whether a player can be moved or messaged. Registration happens at login, so the registry
		/// also holds sessions that are still negotiating and have neither a backend to leave nor a
		/// codec to encode a message with.
		/// </summary>
		private static bool IsInGame(ProxyConnection connection)
		{
			return connection.Client().IsConnected && connection.HasClientJoinedWorld();
		}
	}
}
