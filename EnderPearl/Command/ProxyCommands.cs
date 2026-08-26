namespace EnderPearl.Command
{
	/// <summary>
	/// The command set the proxy ships with.
	/// </summary>
	public static class ProxyCommands
	{
		public static List<ProxyCommand> Defaults()
		{
			return new List<ProxyCommand>
			{
				new("hub", "Send yourself to the fallback hub"),
				new("lobby", "Send yourself to the fallback lobby"),
				new("server", "List or switch backend servers"),
				new("glist", "List every player on the network and where they are"),
				new("send", "Move another player to a backend server"),
				new("alert", "Broadcast a message to every player on the network"),
				new("perm", "Grant or revoke proxy permissions")
			};
		}
	}
}
