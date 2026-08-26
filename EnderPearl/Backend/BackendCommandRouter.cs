using System;
using System.Collections.Generic;
using EnderPearl.Command;
using EnderPearl.Config;
using EnderPearl.Permission;
using EnderPearl.Logging;

namespace EnderPearl.Backend
{
	/// <summary>
	/// Runs a proxy command a player typed in chat. Only the commands that act on the caller's own
	/// session live here; everything that acts on the network is in NetworkCommands, so the console
	/// runs the same code.
	/// </summary>
	public sealed class BackendCommandRouter
	{
		private readonly BackendDirectory backendDirectory;
		private readonly BackendSwitcher switcher;
		private readonly NetworkCommands? networkCommands;
		private readonly ProxyPermissions permissions;
		private readonly SecurityConfig security;

		public BackendCommandRouter(
			BackendDirectory backendDirectory,
			BackendSwitcher switcher,
			NetworkCommands? networkCommands,
			ProxyPermissions permissions,
			SecurityConfig? security
		)
		{
			this.backendDirectory = backendDirectory;
			this.switcher = switcher;
			this.networkCommands = networkCommands;
			this.permissions = permissions;
			this.security = security ?? SecurityConfig.Defaults();
		}

		public void Execute(ProxyConnection connection, CommandInterception.Consumed command)
		{
			string name = command.Command.Name;
			CommandSender sender = CommandSender.Of(connection);

			if (networkCommands == null || !networkCommands.Authorize(sender, name))
			{
				return;
			}
			// Administrators are exempt: the cooldown exists so an unattended macro cannot turn one
			// player into a connection flood against a backend.
			bool admin = permissions.IsAdmin(sender.Xuid(), sender.Name());
			if (!admin && !connection.ClaimProxyCommandSlot(security.CommandCooldownMillis))
			{
				sender.SendMessage("You are using proxy commands too quickly. Try again in a moment.");
				return;
			}

			List<string> arguments = CommandArguments.Split(command.OriginalCommandLine);
			switch (name)
			{
				case "hub":
				case "lobby":
					SelfServiceSwitch(connection, backendDirectory.HubBackend());
					break;
				case "server":
					Server(connection, arguments);
					break;
				case "glist":
					networkCommands.Glist(sender);
					break;
				case "send":
					networkCommands.Send(sender, arguments);
					break;
				case "alert":
					networkCommands.Alert(sender, CommandArguments.Remainder(command.OriginalCommandLine));
					break;
				case "perm":
					networkCommands.Permission(sender, arguments);
					break;
				default:
					sender.SendMessage("Unknown proxy command: " + name);
					break;
			}
		}

		private void Server(ProxyConnection connection, List<string> arguments)
		{
			if (arguments.Count == 0)
			{
				var visible = new List<string>();
				foreach (BackendConfig backend in backendDirectory.Backends())
				{
					if (MayJoin(connection, backend.Name))
					{
						visible.Add(backend.Name);
					}
				}
				BackendSwitcher.SendMessage(connection, "Servers: " + string.Join(", ", visible));
				BackendSwitcher.SendMessage(connection,
					"You are on " + connection.BackendName() + ". Use /server <name> to switch.");
				return;
			}
			BackendConfig? requestedBackend = backendDirectory.Find(arguments[0]);
			if (requestedBackend != null)
			{
				SelfServiceSwitch(connection, requestedBackend);
			}
			else
			{
				BackendSwitcher.SendMessage(connection, "Unknown server: " + arguments[0]);
			}
		}

		/// <summary>
		/// A switch the player asked for themselves, which a restricted backend refuses - reported as
		/// "unknown" rather than "not allowed", so a player has no way to learn the backend exists.
		/// </summary>
		private void SelfServiceSwitch(ProxyConnection connection, BackendConfig backend)
		{
			if (!MayJoin(connection, backend.Name))
			{
				Logger.Info(
					$"Refused {connection.ClientLogin.AuthData.DisplayName} ({connection.ClientLogin.AuthData.Xuid}) self-service access to restricted backend {backend.Name}.");
				BackendSwitcher.SendMessage(connection, "Unknown server: " + backend.Name);
				return;
			}
			switcher.SwitchBackend(connection, backend);
		}

		private bool MayJoin(ProxyConnection connection, string backendName)
		{
			return permissions.MayJoinBackend(
				connection.ClientLogin.AuthData.Xuid,
				connection.ClientLogin.AuthData.DisplayName,
				backendName
			);
		}
	}
}
