using System.Diagnostics;
using System.Security.AccessControl;
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
    private static readonly Guid HttpSysAppId = new("A253521A-C31E-457C-AADD-C0E42A87EA0F");

    public static bool TryEnsureHttpsReady(int httpsPort, int httpPort, string? trustedHost = null)
    {
        if (IsHttpsAlreadyConfigured(httpsPort, httpPort, trustedHost))
        {
            Console.WriteLine($"[SSL] HTTPS already configured on port {httpsPort} (reusing existing http.sys bindings).");
            return true;
        }

        if (!IsAdministrator())
        {
            Console.WriteLine("[SSL] HTTPS setup requires administrator privileges.");
            Console.WriteLine("[SSL] Run as administrator, or pass --localhost for HTTP-only local testing.");
            return false;
        }

        EnsureUrlReservation($"http://+:{httpPort}/");
        EnsureUrlReservation($"https://+:{httpsPort}/");

        // Prefer a real, publicly-trusted certificate for the configured host (provisioned
        // externally, e.g. by win-acme via Let's Encrypt DNS-01). Falls back to the self-signed
        // dev certificate when no trusted cert is present (first boot, localhost, or no host set).
        if (!string.IsNullOrWhiteSpace(trustedHost) && TryUseTrustedCertificate(httpsPort, trustedHost!))
            return true;

        RemoveBrokenCertificates();

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
            cert.Dispose();
            return true;
        }

        PrepareCertificateForHttpSys(cert, thumbprint);
        RemoveSslBinding(httpsPort);

        if (TryBindCertificate(httpsPort, thumbprint))
        {
            LogHttpsReady(httpsPort, thumbprint);
            cert.Dispose();
            return true;
        }

        Console.WriteLine("[SSL] Certificate bind failed; recreating development certificate...");
        RemoveCertificate(thumbprint);
        cert.Dispose();

        cert = CreateSelfSignedCertificate();
        if (cert == null)
        {
            Console.WriteLine("[SSL] Failed to recreate the TeslaPC development certificate.");
            return false;
        }

        thumbprint = NormalizeThumbprint(cert.Thumbprint);
        PrepareCertificateForHttpSys(cert, thumbprint);
        RemoveSslBinding(httpsPort);

        if (!TryBindCertificate(httpsPort, thumbprint))
        {
            Console.WriteLine($"[SSL] Failed to bind certificate to port {httpsPort}.");
            cert.Dispose();
            return false;
        }

        LogHttpsReady(httpsPort, thumbprint);
        cert.Dispose();
        return true;
    }

    private static void LogHttpsReady(int httpsPort, string thumbprint)
    {
        Console.WriteLine($"[SSL] HTTPS ready on port {httpsPort} (thumbprint {thumbprint}).");
        Console.WriteLine("[SSL] Browsers will warn about the self-signed certificate.");
    }

    /// <summary>
    /// Binds a publicly-trusted certificate for <paramref name="host"/> if one is present in
    /// LocalMachine\My (e.g. issued by win-acme / Let's Encrypt). Returns false to fall back to
    /// the self-signed dev certificate.
    /// </summary>
    private static bool TryUseTrustedCertificate(int httpsPort, string host)
    {
        var cert = GetTrustedCertificate(host);
        if (cert == null)
        {
            Console.WriteLine($"[SSL] No trusted certificate for '{host}' in LocalMachine\\My yet; using self-signed.");
            return false;
        }

        try
        {
            string thumbprint = NormalizeThumbprint(cert.Thumbprint);
            if (IsCertificateBound(httpsPort, thumbprint))
            {
                Console.WriteLine($"[SSL] HTTPS already bound to the trusted certificate for {host}.");
                return true;
            }

            PrepareCertificateForHttpSys(cert, thumbprint);
            RemoveSslBinding(httpsPort);
            if (TryBindCertificate(httpsPort, thumbprint))
            {
                Console.WriteLine($"[SSL] HTTPS ready on port {httpsPort} using the trusted certificate for {host}.");
                Console.WriteLine($"[SSL]   issuer: {cert.Issuer}");
                Console.WriteLine($"[SSL]   valid until: {cert.NotAfter:yyyy-MM-dd}");
                return true;
            }

            Console.WriteLine($"[SSL] Failed to bind the trusted certificate for {host}; falling back to self-signed.");
            return false;
        }
        finally
        {
            cert.Dispose();
        }
    }

    /// <summary>
    /// Finds the newest valid, private-key-bearing certificate in LocalMachine\My whose SAN/CN
    /// matches <paramref name="host"/>, excluding our own self-signed dev cert.
    /// </summary>
    private static X509Certificate2? GetTrustedCertificate(string host)
    {
        try
        {
            using var store = new X509Store(StoreName.My, StoreLocation.LocalMachine);
            store.Open(OpenFlags.ReadOnly);

            X509Certificate2? best = null;
            foreach (var candidate in store.Certificates)
            {
                if (candidate.FriendlyName == CertFriendlyName
                    || candidate.NotAfter <= DateTime.Now
                    || candidate.NotBefore > DateTime.Now
                    || !candidate.HasPrivateKey
                    || !HasAccessiblePrivateKey(candidate)
                    || !candidate.MatchesHostname(host))
                {
                    continue;
                }

                if (best == null || candidate.NotAfter > best.NotAfter)
                {
                    best?.Dispose();
                    best = new X509Certificate2(candidate);
                }
            }
            return best;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SSL] Error searching for a trusted certificate: {ex.Message}");
            return null;
        }
    }

    private static bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static void RemoveBrokenCertificates()
    {
        try
        {
            using var store = new X509Store(StoreName.My, StoreLocation.LocalMachine);
            store.Open(OpenFlags.ReadWrite);

            foreach (var candidate in store.Certificates)
            {
                if (candidate.FriendlyName != CertFriendlyName)
                    continue;

                if (!HasAccessiblePrivateKey(candidate))
                {
                    Console.WriteLine($"[SSL] Removing broken certificate {NormalizeThumbprint(candidate.Thumbprint)}.");
                    store.Remove(candidate);
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SSL] Warning: certificate cleanup failed: {ex.Message}");
        }
    }

    private static X509Certificate2? GetOrCreateCertificate()
    {
        try
        {
            using var store = new X509Store(StoreName.My, StoreLocation.LocalMachine);
            store.Open(OpenFlags.ReadWrite);

            foreach (var candidate in store.Certificates)
            {
                if (candidate.FriendlyName != CertFriendlyName
                    || candidate.NotAfter <= DateTime.UtcNow
                    || !candidate.HasPrivateKey
                    || !HasAccessiblePrivateKey(candidate))
                {
                    continue;
                }

                Console.WriteLine("[SSL] Reusing existing TeslaPC development certificate.");
                return new X509Certificate2(candidate);
            }

            return CreateSelfSignedCertificate();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SSL] Certificate store error: {ex.Message}");
            return null;
        }
    }

    private static X509Certificate2? CreateSelfSignedCertificate()
    {
        var result = RunPowerShellScript(
            "$ErrorActionPreference = 'Stop'; " +
            "$cert = New-SelfSignedCertificate " +
            "-Subject 'CN=TeslaPC' " +
            "-DnsName 'localhost','TeslaPC' " +
            "-CertStoreLocation 'Cert:\\LocalMachine\\My' " +
            "-NotAfter (Get-Date).AddYears(5) " +
            $"-FriendlyName '{CertFriendlyName}' " +
            "-KeyExportPolicy Exportable; " +
            "Write-Output $cert.Thumbprint");

        if (result.exitCode != 0 || string.IsNullOrWhiteSpace(result.output))
        {
            Console.WriteLine($"[SSL] Certificate generation failed (exit {result.exitCode}): {result.output}");
            return null;
        }

        string? thumbprint = TryParseThumbprint(result.output);
        if (thumbprint == null)
        {
            Console.WriteLine($"[SSL] Certificate generation returned an invalid thumbprint: {result.output}");
            return null;
        }

        var loaded = TryLoadCertificate(thumbprint);
        if (loaded == null)
        {
            Console.WriteLine("[SSL] Certificate was created but could not be loaded from the store.");
            return null;
        }

        Console.WriteLine("[SSL] Generated new self-signed development certificate.");
        return loaded;
    }

    private static void PrepareCertificateForHttpSys(X509Certificate2 certificate, string thumbprint)
    {
        var repair = RunProcess("certutil", $"-repairstore my {thumbprint}");
        if (repair.exitCode != 0)
            Console.WriteLine($"[SSL] Warning: certutil repairstore failed (exit {repair.exitCode}): {repair.output}");

        GrantHttpSysPrivateKeyAccess(certificate);
    }

    private static bool HasAccessiblePrivateKey(X509Certificate2 certificate)
    {
        try
        {
            return certificate.GetRSAPrivateKey() != null;
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    private static void GrantHttpSysPrivateKeyAccess(X509Certificate2 certificate)
    {
        string? keyPath = TryGetPrivateKeyFilePath(certificate);
        if (keyPath == null || !File.Exists(keyPath))
        {
            Console.WriteLine("[SSL] Warning: could not locate the certificate private key file.");
            return;
        }

        try
        {
            var fileInfo = new FileInfo(keyPath);
            var security = fileInfo.GetAccessControl();
            GrantReadAccess(security, WellKnownSidType.NetworkServiceSid);
            GrantReadAccess(security, WellKnownSidType.LocalSystemSid);
            GrantReadAccess(security, WellKnownSidType.LocalServiceSid);
            fileInfo.SetAccessControl(security);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SSL] Warning: Set-Acl failed, falling back to icacls: {ex.Message}");
            var result = RunProcess(
                "icacls",
                $"\"{keyPath}\" /grant \"NETWORK SERVICE:R\" \"NT AUTHORITY\\SYSTEM:R\" \"NT AUTHORITY\\LOCAL SERVICE:R\"");
            if (result.exitCode != 0)
                Console.WriteLine($"[SSL] Warning: could not grant http.sys access to the private key: {result.output}");
        }
    }

    private static void GrantReadAccess(FileSecurity security, WellKnownSidType sidType)
    {
        var sid = new SecurityIdentifier(sidType, null);
        security.AddAccessRule(new FileSystemAccessRule(
            sid,
            FileSystemRights.Read,
            AccessControlType.Allow));
    }

    private static string? TryGetPrivateKeyFilePath(X509Certificate2 certificate)
    {
        string? uniqueName = null;
        try
        {
            using var rsa = certificate.GetRSAPrivateKey();
            uniqueName = rsa switch
            {
                RSACng cng => cng.Key.UniqueName,
                RSACryptoServiceProvider csp => csp.CspKeyContainerInfo.UniqueKeyContainerName,
                _ => null
            };
        }
        catch (CryptographicException)
        {
            return null;
        }

        if (string.IsNullOrEmpty(uniqueName))
            return null;

        // A key surfaced as RSACng may still physically live in the legacy CAPI
        // MachineKeys folder (and vice versa), so probe both locations and return
        // whichever file actually exists.
        string commonAppData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        string[] candidates =
        {
            Path.Combine(commonAppData, "Microsoft", "Crypto", "Keys", uniqueName),
            Path.Combine(commonAppData, "Microsoft", "Crypto", "RSA", "MachineKeys", uniqueName),
        };

        return Array.Find(candidates, File.Exists);
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

    private static bool IsHttpsAlreadyConfigured(int httpsPort, int httpPort, string? trustedHost) =>
        HasHealthySslBinding(httpsPort, trustedHost)
        && HasUrlReservation($"http://+:{httpPort}/")
        && HasUrlReservation($"https://+:{httpsPort}/");

    /// <summary>
    /// True only when 8443's binding references a certificate that still exists in
    /// LocalMachine\My, is not expired, and — when a trusted host is configured — is not
    /// superseded by a newer trusted cert (e.g. after an ACME renewal, whose import removes the
    /// old cert and would otherwise leave http.sys serving a hash with no cert behind it).
    /// </summary>
    private static bool HasHealthySslBinding(int port, string? trustedHost)
    {
        var result = RunNetsh($"http show sslcert ipport=0.0.0.0:{port}");
        if (result.exitCode != 0)
            return false;

        var match = Regex.Match(result.output, @"(?i)Certificate Hash\s*:\s*([a-f0-9]+)");
        if (!match.Success)
            return false;

        string boundThumbprint = NormalizeThumbprint(match.Groups[1].Value);
        try
        {
            using var store = new X509Store(StoreName.My, StoreLocation.LocalMachine);
            store.Open(OpenFlags.ReadOnly);

            var matches = store.Certificates.Find(X509FindType.FindByThumbprint, boundThumbprint, validOnly: false);
            using var boundCert = matches.Count > 0 ? new X509Certificate2(matches[0]) : null;
            if (boundCert == null || boundCert.NotAfter <= DateTime.Now)
            {
                Console.WriteLine($"[SSL] Bound certificate {boundThumbprint} is missing from the store or expired; rebinding.");
                return false;
            }

            if (!string.IsNullOrWhiteSpace(trustedHost))
            {
                using var best = GetTrustedCertificate(trustedHost!);
                if (best != null
                    && NormalizeThumbprint(best.Thumbprint) != boundThumbprint
                    && best.NotAfter > boundCert.NotAfter)
                {
                    Console.WriteLine($"[SSL] A newer trusted certificate for {trustedHost} is available; rebinding.");
                    return false;
                }
            }

            return true;
        }
        catch (Exception ex)
        {
            // Can't inspect the store (e.g. access) — keep the old reuse behavior.
            Console.WriteLine($"[SSL] Could not verify the bound certificate ({ex.Message}); reusing the binding.");
            return true;
        }
    }

    private static bool HasUrlReservation(string url)
    {
        var result = RunNetsh("http show urlacl");
        if (result.exitCode != 0)
            return false;

        string escaped = Regex.Escape(url);
        return Regex.IsMatch(result.output, $@"(?i)Reserved URL\s*:\s*{escaped}\s*$", RegexOptions.Multiline);
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

    private static string? TryParseThumbprint(string output)
    {
        var match = Regex.Match(output, @"(?i)\b([0-9A-F]{40})\b");
        return match.Success ? NormalizeThumbprint(match.Groups[1].Value) : null;
    }

    private static X509Certificate2? TryLoadCertificate(string thumbprint)
    {
        for (int attempt = 0; attempt < 5; attempt++)
        {
            using var store = new X509Store(StoreName.My, StoreLocation.LocalMachine);
            store.Open(OpenFlags.ReadOnly);

            var matches = store.Certificates.Find(X509FindType.FindByThumbprint, thumbprint, validOnly: false);
            if (matches.Count > 0)
                return new X509Certificate2(matches[0]);

            foreach (var candidate in store.Certificates)
            {
                if (candidate.FriendlyName == CertFriendlyName && HasAccessiblePrivateKey(candidate))
                    return new X509Certificate2(candidate);
            }

            Thread.Sleep(200);
        }

        return null;
    }

    private static (int exitCode, string output) RunPowerShellScript(string script)
    {
        try
        {
            string encoded = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script));
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
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

    private static (int exitCode, string output) RunProcess(string fileName, string arguments)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);
            if (process == null)
                return (-1, $"Failed to start {fileName}");

            string output = process.StandardOutput.ReadToEnd();
            string error = process.StandardError.ReadToEnd();
            process.WaitForExit(30000);

            string combined = string.IsNullOrEmpty(error) ? output.Trim() : $"{output.Trim()} | {error.Trim()}";
            return (process.ExitCode, combined);
        }
        catch (Exception ex)
        {
            return (-1, ex.Message);
        }
    }

    private static (int exitCode, string output) RunNetsh(string arguments) =>
        RunProcess("netsh", arguments);
}