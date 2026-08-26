using EnderPearl.Logging;

namespace EnderPearl.Command
{
	/// <summary>
	/// The operator at the proxy's own terminal.
	///
	/// <para>Unconditionally an administrator: anyone who can type into this process can already stop it,
	/// edit its config and read its keys, so gating the console behind a permission would protect
	/// nothing. It is also the escape hatch — an operator who has revoked their own <c>admin</c> node in
	/// game gets it back from here without editing files.</para>
	/// </summary>
	public sealed class ConsoleSender : CommandSender
	{
		public static readonly ConsoleSender INSTANCE = new();

		private ConsoleSender()
		{
		}

		public string Name()
		{
			return "CONSOLE";
		}

		public string Xuid()
		{
			return "";
		}

		public bool IsConsole()
		{
			return true;
		}

		public void SendMessage(string message)
		{
			Logger.Info(message);
		}
	}
}
