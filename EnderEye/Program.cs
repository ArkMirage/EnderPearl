using EnderEye.https;

namespace EnderEye
{
	internal class Program
	{
		static void Main(string[] args)
		{
			const string domain = "client.discovery.minecraft-services.net";
			const int port = 443;

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
	}
}
