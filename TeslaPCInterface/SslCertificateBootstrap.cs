using System.Diagnostics;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using System.Text.RegularExpressions;

namespace PrimaryProcess;

/// <summary>
/// Generates a self-signed certificate and binds it to the HTTPS port via http.sys,
/// so HttpListener can serve TLS without running bindSSLCert.bat manually.
/// Requires administrator privileges (same as the batch script).
/// </summary>
internal static class SslCertificateBootstrap
{
    private const string CertFriendlyName = "TeslaPC Dev Cert";
    private const string CertSubject = "CN=TeslaPC";
    private static readonly Guid HttpSysAppId = new("A253521A-C31E-457C-AADD-C0E42A87EA0F");

    public static bool TryEnsureHttpsReady(int httpsPort, int httpPort)
    {
        if (!IsAdministrator())
        {
            Console.WriteLine("[SSL] HTTPS setup requires administrator privileges.");
            Console.WriteLine("[SSL] Run as administrator, or pass --localhost for HTTP-only local testing.");
            return false;
        }

        EnsureUrlReservation($"http://+:{httpPort}/");
        EnsureUrlReservation($"https://+:{httpsPort}/");

        var cert = GetOrCreateCertificate();
        if (cert == null)
        {
            Console.WriteLine("[SSL] Failed to create or load the TeslaPC development certificate.");
            return false;
        }

        string thumbprint = NormalizeThumbprint(cert.Thumbprint);
        if (IsCertificateBound(httpsPort, thumbprint))
        {
            Console.WriteLine($"[SSL] HTTPS already configured on port {httpsPort}.");
            return true;
        }

        RemoveSslBinding(httpsPort);
        if (!BindCertificate(httpsPort, thumbprint))
        {
            Console.WriteLine($"[SSL] Failed to bind certificate to port {httpsPort}.");
            return false;
        }

        Console.WriteLine($"[SSL] HTTPS ready on port {httpsPort} (thumbprint {thumbprint}).");
        Console.WriteLine("[SSL] Browsers will warn about the self-signed certificate.");
        return true;
    }

    private static bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static X509Certificate2? GetOrCreateCertificate()
    {
        try
        {
            using var store = new X509Store(StoreName.My, StoreLocation.LocalMachine);
            store.Open(OpenFlags.ReadWrite);

            foreach (var candidate in store.Certificates.Find(X509FindType.FindBySubjectName, "TeslaPC", validOnly: false))
            {
                if (candidate.FriendlyName == CertFriendlyName && candidate.NotAfter > DateTime.UtcNow)
                {
                    Console.WriteLine("[SSL] Reusing existing TeslaPC development certificate.");
                    return candidate;
                }
            }

            var cert = CreateSelfSignedCertificate();
            store.Add(cert);
            Console.WriteLine("[SSL] Generated new self-signed development certificate.");
            return cert;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SSL] Certificate store error: {ex.Message}");
            return null;
        }
    }

    private static X509Certificate2 CreateSelfSignedCertificate()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(CertSubject, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        request.CertificateExtensions.Add(
            new X509EnhancedKeyUsageExtension(
                new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") },
                critical: false));

        request.CertificateExtensions.Add(
            new X509KeyUsageExtension(
                X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment,
                critical: false));

        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("localhost");
        san.AddDnsName("TeslaPC");
        request.CertificateExtensions.Add(san.Build());

        var created = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddYears(5));
        created.FriendlyName = CertFriendlyName;

        return new X509Certificate2(
            created.Export(X509ContentType.Pfx),
            (string?)null,
            X509KeyStorageFlags.PersistKeySet | X509KeyStorageFlags.Exportable);
    }

    private static bool IsCertificateBound(int port, string expectedThumbprint)
    {
        var result = RunNetsh($"http show sslcert ipport=0.0.0.0:{port}");
        if (result.exitCode != 0)
            return false;

        var match = Regex.Match(result.output, @"(?i)Certificate Hash\s*:\s*([a-f0-9]+)");
        if (!match.Success)
            return false;

        return NormalizeThumbprint(match.Groups[1].Value) == expectedThumbprint;
    }

    private static void RemoveSslBinding(int port)
    {
        RunNetsh($"http delete sslcert ipport=0.0.0.0:{port}");
    }

    private static bool BindCertificate(int port, string thumbprint)
    {
        var result = RunNetsh(
            $"http add sslcert ipport=0.0.0.0:{port} certhash={thumbprint} appid={{{HttpSysAppId}}}");
        if (result.exitCode != 0)
            Console.WriteLine($"[SSL] netsh bind failed (exit {result.exitCode}): {result.output}");
        return result.exitCode == 0;
    }

    private static void EnsureUrlReservation(string url)
    {
        var result = RunNetsh($"http add urlacl url={url} user=Everyone");
        if (result.exitCode == 0)
            Console.WriteLine($"[SSL] Reserved {url}");
    }

    private static string NormalizeThumbprint(string thumbprint) =>
        thumbprint.Replace(" ", string.Empty, StringComparison.Ordinal).ToUpperInvariant();

    private static (int exitCode, string output) RunNetsh(string arguments)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "netsh",
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);
            if (process == null)
                return (-1, "Failed to start netsh process");

            string output = process.StandardOutput.ReadToEnd();
            string error = process.StandardError.ReadToEnd();
            process.WaitForExit(10000);

            string combined = string.IsNullOrEmpty(error) ? output.Trim() : $"{output.Trim()} | {error.Trim()}";
            return (process.ExitCode, combined);
        }
        catch (Exception ex)
        {
            return (-1, ex.Message);
        }
    }
}