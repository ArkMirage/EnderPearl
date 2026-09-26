using EnderEye.Hosts;
using EnderEye.https;

namespace EnderEye
{
	internal class Program
	{
		static void Main(string[] args)
		{
			const string domain = "client.discovery.minecraft-services.net";
			const int port = 443;

			var hosts = new HostsManager();

			// -clear: 只清理 hosts 中残留的映射后退出，不启动服务
			if (args.Contains("-clear", StringComparer.OrdinalIgnoreCase))
			{
				Console.WriteLine("[hosts] -clear mode: removing entry and exiting");
				hosts.RemoveLoopbackEntry(domain);
				Console.WriteLine("[hosts] Done. Press any key to exit.");
				Console.ReadKey();
				return;
			}

			hosts.EnsureLoopbackEntry(domain);

			// 兜底：Ctrl+C、关闭控制台窗口等不会走 finally 的退出路径
			AppDomain.CurrentDomain.ProcessExit += (_, _) => hosts.RemoveLoopbackEntry(domain);
			Console.CancelKeyPress += (_, e) =>
			{
				e.Cancel = true;
				Console.WriteLine("[hosts] Ctrl+C received, cleaning up");
				hosts.RemoveLoopbackEntry(domain);
				Environment.Exit(0);
			};

			try
			{
				if (!File.Exists($"{domain}.pfx"))
				{
					CertificateCreator.CreateSelfSignedCertificate(domain);
					var installer = new CertificateManager();
					installer.InstallCertificateToLocalMachineRoot("client.discovery.minecraft-services.net.cer");
				}
				var server = new KestrelHttpsServer(domain, port);
				server.Start();
				EnderPearlKeyRefresher.Start();
				Console.ReadKey();
			}
			finally
			{
				hosts.RemoveLoopbackEntry(domain);
			}
		}
	}
}
