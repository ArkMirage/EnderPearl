using System;
using System.IO;
using EnderPearl.Config;
using EnderPearl.Auth;
using EnderPearl.Frontend;
using EnderPearl.Core;
using EnderPearl.Permission;
using EnderPearl.Server;

namespace EnderPearl
{
	/// <summary>
	/// EnderPearl: a Velocity-style proxy for Minecraft Bedrock with Endstone/BDS backends.
	///
	/// <p>Run it with nothing beside it and it is exactly that - a Bedrock proxy speaking
	/// Bedrock 1.26.40 (protocol 2168).</p>
	/// </summary>
	internal static class Program
	{
		/// <summary>
		/// Printed once the log is open and before anything else does work, so the terminal shows what
		/// is running before any of it starts talking.
		/// </summary>
		private const string Banner =
			"""
			 _____ _   _ ____  _____ ____  ____   _    ____  _
			| ____| \ | |  _ \| ____|  _ \|  _ \ / \  |  _ \| |
			|  _| |  \| | | | |  _| | |_) | |_) / _ \ | |_) | |
			| |___| |\  | |_| | |___|  _ <|  _ < ___ \|  _ <| |___
			|_____|_| \_|____/|_____|_| \_\_| \_\_/ \_\_| \_\_____|

			""";

		private static int Main(string[] args)
		{
			try
			{
				string configPath = args.Length > 0 ? args[0] : "config.json";
				Logger.Install(configPath);
				PrintBanner();
				ProxyConfig config = ProxyConfig.LoadOrCreate(configPath);
				string? absoluteConfig = Path.GetFullPath(configPath);
				string configDirectory = Path.GetDirectoryName(absoluteConfig) ?? ".";
				// Runtime grants live beside the config they extend, so a deployment copies one directory.
				string permissionsPath = Path.Combine(configDirectory, "permissions.json");
				
				MojangMimicIdentity mimic = MojangMimicIdentity.LoadOrCreate(configDirectory);
				// Before anything reads proxy-wide state: the listener's own construction and the console
				// both consult it, and a failure here is still inside this try/catch, so it is logged.
				ProxyServer.Initialize(
					config,
					ProxyPermissions.Load(config.Policy.Permissions, permissionsPath),
					mimic
				);
				KeyServiceHost.Start(config.KeyForgePort, mimic);

				var listener = new BedrockProxyListener(configDirectory);

				Console.CancelKeyPress += (_, eventArgs) =>
				{
					eventArgs.Cancel = true;
					listener.Stop();
				};
				AppDomain.CurrentDomain.ProcessExit += (_, _) => listener.Stop();

				listener.Start();
				listener.AwaitShutdown();
				return 0;
			}
			catch (Exception failure)
			{
				Logger.Error($"Fatal: {failure}");
				return 1;
			}
		}

		/// <summary>
		/// Logged a line at a time: one multi-line message would carry the level tag on its first line
		/// only, which pushes that line out of the art's alignment. Giving every line the tag keeps the
		/// block square - it just sits inset like any other log output.
		/// </summary>
		private static void PrintBanner()
		{
			// A raw string literal keeps the source file's line endings; normalising first stops a CRLF
			// checkout from leaving a stray carriage return on every logged line.
			foreach (string line in Banner.ReplaceLineEndings("\n").Split('\n'))
			{
				Logger.Info(line);
			}
		}
	}
}
