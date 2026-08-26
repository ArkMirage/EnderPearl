// CreateCertificate.cs
using System;
using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

public class CertificateCreator
{
	public static void CreateSelfSignedCertificate(string domainName, string password = "123456")
	{
		using (RSA rsa = RSA.Create(2048))
		{
			var request = new CertificateRequest(
				$"CN={domainName}",
				rsa,
				HashAlgorithmName.SHA256,
				RSASignaturePadding.Pkcs1
			);

			var sanBuilder = new SubjectAlternativeNameBuilder();
			sanBuilder.AddDnsName(domainName);
			sanBuilder.AddIpAddress(System.Net.IPAddress.Parse("127.0.0.1"));
			request.CertificateExtensions.Add(sanBuilder.Build());

			request.CertificateExtensions.Add(
				new X509EnhancedKeyUsageExtension(
					new OidCollection
					{
						new Oid("1.3.6.1.5.5.7.3.1")
                    },
					false
				)
			);

			request.CertificateExtensions.Add(
				new X509KeyUsageExtension(
					X509KeyUsageFlags.DigitalSignature |
					X509KeyUsageFlags.KeyEncipherment,
					false
				)
			);

			request.CertificateExtensions.Add(
				new X509BasicConstraintsExtension(false, false, 0, false)
			);

			var certificate = request.CreateSelfSigned(
				DateTimeOffset.UtcNow.AddDays(-1),
				DateTimeOffset.UtcNow.AddYears(98)
			);

			var certBytes = certificate.Export(X509ContentType.Pfx, password);

			string certPath = $"{domainName}.pfx";
			File.WriteAllBytes(certPath, certBytes);

			var publicCertBytes = certificate.Export(X509ContentType.Cert);
			string publicCertPath = $"{domainName}.cer";
			File.WriteAllBytes(publicCertPath, publicCertBytes);

		}
	}

	public static X509Certificate2 LoadCertificate(string domainName, string password = "123456")
	{
		string certPath = $"{domainName}.pfx";

		if (!File.Exists(certPath))
		{
			CreateSelfSignedCertificate(domainName, password);
		}

		return new X509Certificate2(certPath, password, X509KeyStorageFlags.Exportable);
	}
}