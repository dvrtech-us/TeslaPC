using System.Diagnostics;
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
        if (TryBindCertificate(httpsPort, thumbprint))
        {
            LogHttpsReady(httpsPort, thumbprint);
            return true;
        }

        Console.WriteLine("[SSL] Certificate bind failed; recreating certificate with http.sys-compatible key...");
        RemoveCertificate(thumbprint);
        cert = CreateSelfSignedCertificate();
        if (cert == null)
        {
            Console.WriteLine("[SSL] Failed to recreate the TeslaPC development certificate.");
            return false;
        }

        thumbprint = NormalizeThumbprint(cert.Thumbprint);
        GrantHttpSysPrivateKeyAccess(thumbprint);
        RemoveSslBinding(httpsPort);

        if (!TryBindCertificate(httpsPort, thumbprint))
        {
            Console.WriteLine($"[SSL] Failed to bind certificate to port {httpsPort}.");
            return false;
        }

        LogHttpsReady(httpsPort, thumbprint);
        return true;
    }

    private static void LogHttpsReady(int httpsPort, string thumbprint)
    {
        Console.WriteLine($"[SSL] HTTPS ready on port {httpsPort} (thumbprint {thumbprint}).");
        Console.WriteLine("[SSL] Browsers will warn about the self-signed certificate.");
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
                if (candidate.FriendlyName == CertFriendlyName
                    && candidate.NotAfter > DateTime.UtcNow
                    && candidate.HasPrivateKey)
                {
                    Console.WriteLine("[SSL] Reusing existing TeslaPC development certificate.");
                    GrantHttpSysPrivateKeyAccess(NormalizeThumbprint(candidate.Thumbprint));
                    return candidate;
                }
            }

            return CreateSelfSignedCertificate();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SSL] Certificate store error: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Uses PowerShell's New-SelfSignedCertificate so the private key is created in a form
    /// http.sys can access. CertificateRequest + MachineKeySet alone still fails with error 1312
    /// on many Windows installs.
    /// </summary>
    private static X509Certificate2? CreateSelfSignedCertificate()
    {
        var result = RunPowerShell(
            "$cert = New-SelfSignedCertificate " +
            "-DnsName 'localhost','TeslaPC' " +
            "-CertStoreLocation 'Cert:\\LocalMachine\\My' " +
            "-NotAfter (Get-Date).AddYears(5) " +
            $"-FriendlyName '{CertFriendlyName}' " +
            "-KeyExportPolicy Exportable " +
            "-KeySpec KeyExchange; " +
            "$cert.Thumbprint");

        if (result.exitCode != 0 || string.IsNullOrWhiteSpace(result.output))
        {
            Console.WriteLine($"[SSL] Certificate generation failed (exit {result.exitCode}): {result.output}");
            return null;
        }

        string thumbprint = NormalizeThumbprint(result.output.Trim());
        GrantHttpSysPrivateKeyAccess(thumbprint);

        using var store = new X509Store(StoreName.My, StoreLocation.LocalMachine);
        store.Open(OpenFlags.ReadOnly);
        var matches = store.Certificates.Find(X509FindType.FindByThumbprint, thumbprint, validOnly: false);
        if (matches.Count == 0)
        {
            Console.WriteLine("[SSL] Certificate was created but could not be loaded from the store.");
            return null;
        }

        Console.WriteLine("[SSL] Generated new self-signed development certificate.");
        return matches[0];
    }

    private static void GrantHttpSysPrivateKeyAccess(string thumbprint)
    {
        var result = RunPowerShell(
            "$cert = Get-ChildItem -Path ('Cert:\\LocalMachine\\My\\' + '" + thumbprint + "') -ErrorAction SilentlyContinue; " +
            "if (-not $cert) { exit 1 }; " +
            "$rsa = [System.Security.Cryptography.X509Certificates.RSACertificateExtensions]::GetRSAPrivateKey($cert); " +
            "if ($null -eq $rsa) { exit 1 }; " +
            "if ($rsa -is [System.Security.Cryptography.RSACryptoServiceProvider]) { " +
            "  $path = Join-Path $env:ProgramData ('Microsoft\\Crypto\\RSA\\MachineKeys\\' + $rsa.CspKeyContainerInfo.UniqueKeyContainerName); " +
            "} elseif ($rsa -is [System.Security.Cryptography.RSACng]) { " +
            "  $path = Join-Path $env:ProgramData ('Microsoft\\Crypto\\Keys\\' + $rsa.Key.UniqueName); " +
            "} else { exit 0 }; " +
            "if (Test-Path $path) { " +
            "  icacls $path /grant 'NETWORK SERVICE:R' 'NT AUTHORITY\\SYSTEM:R' | Out-Null; " +
            "  exit $LASTEXITCODE " +
            "}; " +
            "exit 0");

        if (result.exitCode != 0)
            Console.WriteLine($"[SSL] Warning: could not grant http.sys access to the certificate private key: {result.output}");
    }

    private static void RemoveCertificate(string thumbprint)
    {
        try
        {
            using var store = new X509Store(StoreName.My, StoreLocation.LocalMachine);
            store.Open(OpenFlags.ReadWrite);
            var matches = store.Certificates.Find(X509FindType.FindByThumbprint, thumbprint, validOnly: false);
            foreach (var cert in matches)
                store.Remove(cert);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SSL] Warning: failed to remove stale certificate: {ex.Message}");
        }
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

    private static bool TryBindCertificate(int port, string thumbprint)
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

    private static (int exitCode, string output) RunPowerShell(string script)
    {
        try
        {
            string encoded = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script));
            var psi = new ProcessStartInfo
            {
                FileName = "powershell",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -EncodedCommand {encoded}",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);
            if (process == null)
                return (-1, "Failed to start PowerShell");

            string output = process.StandardOutput.ReadToEnd().Trim();
            string error = process.StandardError.ReadToEnd().Trim();
            process.WaitForExit(30000);

            string combined = string.IsNullOrEmpty(error) ? output : $"{output} | {error}";
            return (process.ExitCode, combined);
        }
        catch (Exception ex)
        {
            return (-1, ex.Message);
        }
    }

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