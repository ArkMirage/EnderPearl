using System;
using System.IO;
using EnderPearl.Config;
using EnderPearl.Crypto;
using EnderPearl.Listener;
using EnderPearl.Logging;
using EnderPearl.Permission;

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
		private static int Main(string[] args)
		{
			try
			{
				string configPath = args.Length > 0 ? args[0] : "config.json";
				Logger.Install(configPath);
				ProxyConfig config = ProxyConfig.LoadOrCreate(configPath);
				string? absoluteConfig = Path.GetFullPath(configPath);
				string configDirectory = Path.GetDirectoryName(absoluteConfig) ?? ".";
				// Runtime grants live beside the config they extend, so a deployment copies one directory.
				string permissionsPath = Path.Combine(configDirectory, "permissions.json");
				
				MojangMimicIdentity mimic = MojangMimicIdentity.LoadOrCreate(configDirectory);
				KeyServiceHost.Start(config.KeyForgePort, mimic);

				var listener = new BedrockProxyListener(
					config,
					ProxyPermissions.Load(config.Permissions, permissionsPath),
					mimic
				);

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
	}
}
