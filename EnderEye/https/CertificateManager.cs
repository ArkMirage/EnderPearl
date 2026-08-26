using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace EnderEye.https
{
	public class CertificateManager
	{
	
		public bool InstallCertificateToLocalMachineRoot(string certificatePath)
		{
			try
			{
				// 1. 加载证书
				var certificate = LoadCertificate(certificatePath);
				if (certificate == null)
				{
					throw new Exception("Cant load cert file");
				}

				return InstallCertificate(certificate, StoreName.Root, StoreLocation.LocalMachine);
			}
			catch
			{
				throw;
			}
		}

	
		private X509Certificate2 LoadCertificate(string certificatePath)
		{
			if (!File.Exists(certificatePath))
			{
				throw new FileNotFoundException($"not exist: {certificatePath}");
			}

			try
			{
				
				string extension = Path.GetExtension(certificatePath).ToLower();

				switch (extension)
				{
					case ".cer":
					case ".crt":
						return new X509Certificate2(certificatePath);
					default:
						throw new NotSupportedException($"UnSupport cert: {extension}");
				}
			}
			catch (Exception ex)
			{
				throw;
			}
		}

		private bool InstallCertificate(X509Certificate2 certificate,
									  StoreName storeName,
									  StoreLocation storeLocation)
		{
			X509Store store = null;

			try
			{
				store = new X509Store(storeName, storeLocation);
				store.Open(OpenFlags.ReadWrite);

				var existingCerts = store.Certificates.Find(
					X509FindType.FindByThumbprint,
					certificate.Thumbprint,
					false
				);

				if (existingCerts.Count > 0)
				{
					return true;
				}

				store.Add(certificate);

				return true;
			}
			catch (System.Security.SecurityException)
			{
				throw;
			}
			catch (Exception ex)
			{
				throw;
			}
			finally
			{
				store?.Close();
			}
		}
	}
}
