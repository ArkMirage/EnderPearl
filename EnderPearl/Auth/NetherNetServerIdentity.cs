using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using NetherNet;

namespace EnderPearl.Auth;

/// <summary>
/// The proxy's persistent NetherNet server identity: a P-384 private key kept under
/// <c>.nethernet/identity.pem</c> beside the config, so clients see one stable identity
/// instead of a fresh trust prompt on every restart.
/// </summary>
public static class NetherNetServerIdentity
{
	public const string DirectoryName = ".nethernet";
	public const string FileName = "identity.pem";

	public static string PathFor(string configDirectory)
	{
		return Path.Combine(configDirectory, DirectoryName, FileName);
	}

	public static ECDsa LoadOrCreate(string configDirectory)
	{
		string directory = Path.Combine(configDirectory, DirectoryName);
		Directory.CreateDirectory(directory);
		string path = Path.Combine(directory, FileName);

		if (File.Exists(path))
		{
			ECDsa existing = ECDsa.Create();
			existing.ImportFromPem(File.ReadAllText(path));
			return existing;
		}

		ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP384);
		File.WriteAllText(path, PemEncoding.Write("PRIVATE KEY", key.ExportPkcs8PrivateKey()));
		return key;
	}

	public static Func<CancellationToken, Task<Identity>> Issuer(string configDirectory)
	{
		ECDsa key = LoadOrCreate(configDirectory);
		return _ => Task.FromResult(Identity.GenerateServerIdentity(key, "self"));
	}
}