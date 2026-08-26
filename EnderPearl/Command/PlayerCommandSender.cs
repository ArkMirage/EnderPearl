using EnderPearl.Backend;

namespace EnderPearl.Command
{
	/// <summary>A player running a command from chat.</summary>
	internal sealed class PlayerCommandSender : CommandSender
	{
		private readonly ProxyConnection connection;

		internal PlayerCommandSender(ProxyConnection connection)
		{
			this.connection = connection ?? throw new ArgumentNullException(nameof(connection));
		}

		public string Name()
		{
			return connection.ClientLogin.AuthData.DisplayName;
		}

		public string Xuid()
		{
			return connection.ClientLogin.AuthData.Xuid;
		}

		public bool IsConsole()
		{
			return false;
		}

		public void SendMessage(string message)
		{
			BackendSwitcher.SendMessage(connection, message);
		}

		public ProxyConnection? Connection()
		{
			return connection;
		}
	}
}
