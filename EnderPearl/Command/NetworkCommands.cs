using EnderPearl.Backend;
using EnderPearl.Config;
using EnderPearl.Permission;
using EnderPearl.Player;
using EnderPearl.Core;
using EnderPearl.Server;

namespace EnderPearl.Command
{
	/// <summary>
	/// What the proxy's own commands do: <c>/glist</c>, <c>/send</c>, <c>/alert</c>, <c>/perm</c>, the
	/// terminal's <c>/kick</c>, and the self-service switches <c>/hub</c>, <c>/lobby</c> and <c>/server</c>.
	///
	/// <para><see cref="SystemCommands"/> registers these; nothing here knows how a line reached it. Written
	/// against <see cref="CommandSender"/> so the terminal and chat run exactly the same code, and a command
	/// that needs a connection of its own takes it from the sender rather than the terminal being a special
	/// case inside it.</para>
	/// </summary>
	public sealed class NetworkCommands
	{
		/// <summary>What a kick with no reason of its own tells the player.</summary>
		private const string DEFAULT_KICK_REASON = "Kicked by an operator";

		public required BackendSwitcher Switcher { get; init; }
		public ProxyCommandManager? CommandManager { get; init; }

		// ------------------------------------------------------------------ glist

		/// <summary>Who is online and where, grouped by backend so an empty backend is visible as empty.</summary>
		public void Glist(CommandSender sender)
		{
			LinkedHashMap<string, List<string>> byBackend = new();
			foreach (BackendConfig backend in ProxyServer.BackendDirectory.Backends())
			{
				byBackend.Add(backend.Name, new List<string>());
			}
			int total = 0;
			foreach (ProxyConnection player in ProxyServer.ConnectedPlayers.Connections())
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
			string targetName = arguments[0];
			string backendName = arguments[1];
			BackendConfig? backend = ProxyServer.BackendDirectory.Find(backendName);
			if (backend == null)
			{
				sender.SendMessage("Unknown server: " + backendName);
				return;
			}

			if (ProxyPlayerEnum.ALL.Equals(targetName, StringComparison.OrdinalIgnoreCase))
			{
				int moved = 0;
				foreach (ProxyConnection player in ProxyServer.ConnectedPlayers.Connections())
				{
					if (IsInGame(player) && !backend.Name.Equals(player.BackendName() ?? "null", StringComparison.OrdinalIgnoreCase))
					{
						Switcher.SwitchBackend(player, backend);
						moved++;
					}
				}
				sender.SendMessage("Sending " + moved + " player(s) to " + backend.Name + ".");
				return;
			}

			ProxyConnection? target = ProxyServer.ConnectedPlayers.FindByName(targetName);
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
				Switcher.SwitchBackend(target, backend);
			}
			else
			{
				sender.SendMessage("No player named '" + targetName + "' is online.");
			}
		}

		// -------------------------------------------------------------------- kick

		/// <summary>
		/// <c>/kick &lt;player&gt; [reason]</c>: put a player back at the title screen.
		///
		/// <para>Registered for the terminal alone. The kick is a client disconnect carrying the reason,
		/// which the player reads on the disconnect screen; closing the frontend leg is the whole job,
		/// because the teardown that follows closes the backend leg with it. Note that this is the proxy
		/// disconnecting a client, the opposite direction to a backend's own disconnect packet - see
		/// BackendRelayPacketHandler.Disconnect.cs.</para>
		/// </summary>
		public void Kick(CommandSender sender, List<string> arguments)
		{
			if (arguments.Count == 0)
			{
				sender.SendMessage("Usage: /kick <player> [reason]");
				return;
			}
			string targetName = arguments[0];
			ProxyConnection? target = ProxyServer.ConnectedPlayers.FindByName(targetName);
			if (target == null)
			{
				sender.SendMessage("No player named '" + targetName + "' is online.");
				return;
			}
			// A free-text reason: the rest of the line, so it may contain spaces and does not need quoting.
			string reason = arguments.Count > 1
				? string.Join(" ", arguments.GetRange(1, arguments.Count - 1))
				: DEFAULT_KICK_REASON;
			string displayName = target.ClientLogin.AuthData.DisplayName;
			Logger.Info($"{sender.Name()} kicked {displayName} ({target.ClientLogin.AuthData.Xuid}): {reason}");
			sender.SendMessage("Kicked " + displayName + ".");
			target.Client.Disconnect(reason);
		}

		// -------------------------------------------------------------- hub / lobby

		/// <summary>
		/// <c>/hub</c> and <c>/lobby</c>: the caller sends themselves to the fallback hub.
		///
		/// <para>Registered for chat only, so a connection is always there to move; the terminal's sender has
		/// none and is told so rather than being allowed to reach a null session.</para>
		/// </summary>
		public void Hub(CommandSender sender)
		{
			ProxyConnection? connection = SenderConnection(sender);
			if (connection == null)
			{
				return;
			}
			SelfServiceSwitch(sender, connection, ProxyServer.BackendDirectory.HubBackend());
		}

		// ------------------------------------------------------------------ server

		/// <summary><c>/server</c> with no argument lists what the caller may reach; with one, it switches.</summary>
		public void Server(CommandSender sender, List<string> arguments)
		{
			ProxyConnection? connection = SenderConnection(sender);
			if (connection == null)
			{
				return;
			}
			if (arguments.Count == 0)
			{
				List<string> visible = new();
				foreach (BackendConfig backend in ProxyServer.BackendDirectory.Backends())
				{
					if (MayJoin(connection, backend.Name))
					{
						visible.Add(backend.Name);
					}
				}
				sender.SendMessage("Servers: " + string.Join(", ", visible));
				sender.SendMessage("You are on " + connection.BackendName() + ". Use /server <name> to switch.");
				return;
			}
			BackendConfig? requested = ProxyServer.BackendDirectory.Find(arguments[0]);
			if (requested == null)
			{
				sender.SendMessage("Unknown server: " + arguments[0]);
				return;
			}
			SelfServiceSwitch(sender, connection, requested);
		}

		/// <summary>
		/// A switch the player asked for themselves, which a restricted backend refuses — reported as
		/// "unknown" rather than "not allowed", so a player has no way to learn the backend exists.
		/// </summary>
		private void SelfServiceSwitch(CommandSender sender, ProxyConnection connection, BackendConfig backend)
		{
			if (!MayJoin(connection, backend.Name))
			{
				Logger.Info(
					$"Refused {connection.ClientLogin.AuthData.DisplayName} ({connection.ClientLogin.AuthData.Xuid}) self-service access to restricted backend {backend.Name}.");
				sender.SendMessage("Unknown server: " + backend.Name);
				return;
			}
			Switcher.SwitchBackend(connection, backend);
		}

		private bool MayJoin(ProxyConnection connection, string backendName)
		{
			return ProxyServer.Permissions.MayJoinBackend(
				connection.ClientLogin.AuthData.Xuid,
				connection.ClientLogin.AuthData.DisplayName,
				backendName
			);
		}

		/// <summary>
		/// The connection behind a sender, or a refusal. Only the terminal can be without one, and no
		/// command that needs a session is registered for the terminal.
		/// </summary>
		private static ProxyConnection? SenderConnection(CommandSender sender)
		{
			ProxyConnection? connection = sender.Connection();
			if (connection == null)
			{
				sender.SendMessage("Only a player can be sent to a server.");
			}
			return connection;
		}

		// ------------------------------------------------------------------ alert

		public void Alert(CommandSender sender, string message)
		{
			if (message == null || message.Trim().Length == 0)
			{
				sender.SendMessage("Usage: /alert <message>");
				return;
			}
			string broadcast = "[Alert] " + message;
			int delivered = 0;
			foreach (ProxyConnection player in ProxyServer.ConnectedPlayers.Connections())
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
		/// operator with no <c>Permissions.admins</c> entry grants themselves <c>admin</c> from the
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
		}

		private void PermissionList(CommandSender sender)
		{
			LinkedHashMap<string, IReadOnlySet<string>> subjects = ProxyServer.Permissions.Subjects();
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
			IReadOnlySet<string> configured = ProxyServer.Permissions.Config.Admins;
			if (configured.Count > 0)
			{
				sender.SendMessage("From config (Permissions.admins, not editable here): "
					+ string.Join(", ", Sorted(configured)));
			}
		}

		private void PermissionInfo(CommandSender sender, string subject)
		{
			IReadOnlySet<string> nodes = ProxyServer.Permissions.NodesOf(subject);
			sender.SendMessage(subject + (nodes.Count == 0
				? " has no runtime Permissions."
				: ": " + string.Join(", ", Sorted(nodes))));
			// Resolved answers matter more than the raw nodes: the config grants are invisible above,
			// and an "admin" node makes every other line redundant.
			bool admin = ProxyServer.Permissions.IsAdmin(subject, subject);
			sender.SendMessage("  administrator: " + (admin ? "true" : "false"));
			foreach (string command in CommandNames())
			{
				if (ProxyServer.Permissions.IsAdminCommand(command))
				{
					sender.SendMessage("  /" + command + ": "
						+ YesNo(ProxyServer.Permissions.Allows(subject, subject, command)));
				}
			}
			foreach (string backend in BackendNames())
			{
				if (ProxyServer.Permissions.IsAdminBackend(backend))
				{
					sender.SendMessage("  server " + backend + ": "
						+ YesNo(ProxyServer.Permissions.MayJoinBackend(subject, subject, backend)));
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
					? ProxyServer.Permissions.Grant(subject, normalized)
					: ProxyServer.Permissions.Revoke(subject, normalized);
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
			ProxyServer.PlayerEnum.Broadcast();
		}

		public List<string> KnownNodes()
		{
			return ProxyPermissions.KnownNodes(CommandNames(), BackendNames());
		}

		/// <summary>
		/// The names a player can reach, which are the ones a permission node makes sense for. Terminal-only
		/// commands are left out, and so are the terminal-only alternates of the ones that are in both places:
		/// no player can be granted or denied a word their chat never accepts. The command's own first name is
		/// the one that answers in chat, so that is the one a node is named after.
		/// </summary>
		private List<string> CommandNames()
		{
			if (CommandManager == null)
			{
				return new List<string>();
			}
			List<string> names = new();
			foreach (ProxyCommand command in CommandManager.Commands(CommandScope.Game))
			{
				names.Add(command.Names[0].Name);
			}
			return names;
		}

		private List<string> BackendNames()
		{
			return new List<string>(ProxyServer.BackendDirectory.BackendNames());
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
			return connection.Client.IsConnected && connection.HasClientJoinedWorld();
		}
	}
}
