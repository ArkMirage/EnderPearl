using EnderPearl.Command;
using EnderPearl.Core;
using EnderPearl.Player;
using EnderPearl.Server;

namespace EnderPearl.Relay
{
	/// <summary>
	/// Runs a proxy command a player typed in chat.
	///
	/// <para>It owns nothing but the two things that are about the caller rather than the command: whether
	/// this player may run it, and how often they may. The command itself comes from
	/// <see cref="CommandInterception.Consumed"/>, which the interceptor answered out of the
	/// <see cref="ProxyCommandManager"/>, so the same declaration that describes a command in the terminal
	/// and in the command tree is also what runs it here.</para>
	/// </summary>
	public sealed class BackendCommandRouter
	{
		public void Execute(ProxyConnection connection, CommandInterception.Consumed command)
		{
			CommandSender sender = CommandSender.Of(connection);
			// The command's own name, not whichever word was typed: a permission node is named after the
			// command, and an alternate that reaches chat is still that permission being exercised.
			string name = command.Command.Names[0].Name;

			if (!Authorize(sender, name))
			{
				return;
			}
			// Administrators are exempt: the cooldown exists so an unattended macro cannot turn one
			// player into a connection flood against a backend.
			bool admin = ProxyServer.Permissions.IsAdmin(sender.Xuid(), sender.Name());
			if (!admin && !connection.ClaimProxyCommandSlot(ProxyServer.Config.Security.CommandCooldownMillis))
			{
				sender.SendMessage("You are using proxy commands too quickly. Try again in a moment.");
				return;
			}
			command.Command.Handler(sender, command.OriginalCommandLine, CommandArguments.Split(command.OriginalCommandLine));
		}

		/// <summary>
		/// Whether the sender may run this command.
		///
		/// <para>Checked at execution and not only when the command tree was built — hiding a command from
		/// autocomplete does not stop a client sending the packet.</para>
		/// </summary>
		/// <returns>false when the sender may not run this command, having been told so</returns>
		public bool Authorize(CommandSender sender, string commandName)
		{
			if (sender.IsConsole() || ProxyServer.Permissions.Allows(sender.Xuid(), sender.Name(), commandName))
			{
				return true;
			}
			Logger.Info($"Denied /{commandName} from {sender.Name()} ({sender.Xuid()}): not permitted.");
			sender.SendMessage("You do not have permission to use /" + commandName + ".");
			return false;
		}
	}
}