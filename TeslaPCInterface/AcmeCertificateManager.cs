using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Certes;
using Certes.Acme;
using Certes.Acme.Resource;

namespace PrimaryProcess;

/// <summary>
/// In-app ACME client: obtains and renews a publicly-trusted Let's Encrypt certificate for the
/// configured host using the DNS-01 challenge, fulfilled via the Cloudflare API. The issued cert
/// is imported into LocalMachine\My, where <see cref="SslCertificateBootstrap"/> binds it to the
/// HTTPS port. No external tools (win-acme / scheduled tasks) required.
///
/// Configuration (only runs when host + token are present):
///   --https-host &lt;host&gt; / TESLAPC_HTTPS_HOST   the certificate hostname (e.g. my.thelpers.com)
///   TESLAPC_CF_TOKEN                               Cloudflare API token (DNS edit on the zone)
///   TESLAPC_ACME_EMAIL                             ACME account contact (optional)
///   TESLAPC_ACME_STAGING=1                         use Let's Encrypt staging (testing; not trusted)
/// </summary>
internal static class AcmeCertificateManager
{
    private const int RenewWhenDaysLeft = 30;
    private static readonly HttpClient Http = new();

    private static string AccountKeyPath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "TeslaPC", "acme-account.pem");

    /// <summary>
    /// Ensures a valid Let's Encrypt cert for <paramref name="host"/> exists in LocalMachine\My,
    /// issuing/renewing via DNS-01 when needed. Returns true if a usable cert is present afterward.
    /// Safe to call repeatedly (no-op when the current cert still has &gt; 30 days).
    /// </summary>
    public static async Task<bool> TryEnsureCertificateAsync(string host, string? email, string? cloudflareToken)
    {
        if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(cloudflareToken))
            return false;

        int daysLeft = GetBestCertificateDaysLeft(host);
        if (daysLeft > RenewWhenDaysLeft)
        {
            Console.WriteLine($"[ACME] Trusted cert for {host} is valid for {daysLeft} more day(s); no action.");
            return true;
        }

        // Issuing is pointless without admin rights: the cert must be imported into
        // LocalMachine\My (machine key set), which requires elevation. Requesting anyway would
        // burn a Let's Encrypt issuance on every start and then fail to store it.
        if (!IsElevated())
        {
            if (daysLeft > 0)
            {
                Console.WriteLine($"[ACME] Cert for {host} expires in {daysLeft} day(s) but renewal requires administrator privileges; keeping the current cert.");
                return true;
            }
            Console.WriteLine($"[ACME] No trusted cert for {host} and not running as administrator; skipping the certificate request.");
            return false;
        }

        Console.WriteLine(daysLeft > 0
            ? $"[ACME] Cert for {host} expires in {daysLeft} day(s); renewing."
            : $"[ACME] No valid trusted cert for {host} found in LocalMachine\\My; requesting one.");

        try
        {
            bool staging = Environment.GetEnvironmentVariable("TESLAPC_ACME_STAGING") == "1";
            var server = staging ? WellKnownServers.LetsEncryptStagingV2 : WellKnownServers.LetsEncryptV2;
            Console.WriteLine($"[ACME] Requesting a certificate for {host} from Let's Encrypt ({(staging ? "staging" : "production")})...");

            var acme = await LoadOrCreateAccountAsync(server, email);

            var order = await acme.NewOrder(new[] { host });
            var authz = (await order.Authorizations()).First();
            var dnsChallenge = await authz.Dns();
            string dnsValue = acme.AccountKey.DnsTxt(dnsChallenge.Token);
            string recordName = $"_acme-challenge.{host}";

            string zoneId = await GetZoneIdAsync(host, cloudflareToken!);
            // A crashed prior attempt can leave the challenge TXT record behind; Cloudflare then
            // rejects the re-create with "an identical record already exists" (LE reuses the pending
            // authorization, so the token/value repeats). Clear any stale records first.
            await DeleteExistingTxtRecordsAsync(zoneId, recordName, cloudflareToken!);
            string recordId = await CreateTxtRecordAsync(zoneId, recordName, dnsValue, cloudflareToken!);
            try
            {
                await WaitForDnsAsync(recordName, dnsValue);

                await dnsChallenge.Validate();
                var auth = await PollAuthorizationAsync(authz);
                if (auth.Status != AuthorizationStatus.Valid)
                {
                    Console.WriteLine($"[ACME] DNS-01 validation failed (status {auth.Status}). Falling back to self-signed.");
                    return false;
                }

                var certKey = KeyFactory.NewKey(KeyAlgorithm.RS256);
                var chain = await order.Generate(new CsrInfo { CommonName = host }, certKey);

                string pfxPassword = Guid.NewGuid().ToString("N");
                byte[] pfx = chain.ToPfx(certKey).Build(host, pfxPassword);
                ImportToMachineStore(pfx, pfxPassword, host);

                Console.WriteLine($"[ACME] Issued and stored a trusted certificate for {host}.");
                return true;
            }
            finally
            {
                await DeleteTxtRecordAsync(zoneId, recordId, cloudflareToken!);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ACME] Certificate request failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Days of validity left on the best trusted cert for <paramref name="host"/> in
    /// LocalMachine\My, or 0 when none is usable. A faulty store entry only skips that entry,
    /// never the whole scan (a foreign cert with a malformed extension must not force a re-issue).
    /// </summary>
    private static int GetBestCertificateDaysLeft(string host)
    {
        try
        {
            using var store = new X509Store(StoreName.My, StoreLocation.LocalMachine);
            store.Open(OpenFlags.ReadOnly);
            X509Certificate2? best = null;
            foreach (var c in store.Certificates)
            {
                try
                {
                    if (c.FriendlyName == "TeslaPC Dev Cert" || c.NotAfter <= DateTime.Now || !c.HasPrivateKey)
                        continue;
                    if (!c.MatchesHostname(host)) continue;
                    if (best == null || c.NotAfter > best.NotAfter) best = c;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[ACME] Skipping unreadable store cert '{c.Subject}': {ex.Message}");
                }
            }
            if (best == null) return 0;
            return Math.Max(0, (int)(best.NotAfter - DateTime.Now).TotalDays);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ACME] Could not read LocalMachine\\My: {ex.Message}");
            return 0;
        }
    }

    private static bool IsElevated()
    {
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        return new System.Security.Principal.WindowsPrincipal(identity)
            .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
    }

    private static async Task<AcmeContext> LoadOrCreateAccountAsync(Uri server, string? email)
    {
        if (File.Exists(AccountKeyPath))
        {
            var key = KeyFactory.FromPem(await File.ReadAllTextAsync(AccountKeyPath));
            return new AcmeContext(server, key);
        }

        var acme = new AcmeContext(server);
        await acme.NewAccount(email ?? "admin@" + (Environment.MachineName.ToLowerInvariant()) + ".local", true);
        System.IO.Directory.CreateDirectory(Path.GetDirectoryName(AccountKeyPath)!);
        await File.WriteAllTextAsync(AccountKeyPath, acme.AccountKey.ToPem());
        return acme;
    }

    private static void ImportToMachineStore(byte[] pfx, string password, string host)
    {
        var cert = X509CertificateLoader.LoadPkcs12(
            pfx, password,
            X509KeyStorageFlags.MachineKeySet | X509KeyStorageFlags.PersistKeySet | X509KeyStorageFlags.Exportable);
        cert.FriendlyName = $"TeslaPC LE ({host})";

        using var store = new X509Store(StoreName.My, StoreLocation.LocalMachine);
        store.Open(OpenFlags.ReadWrite);
        // Remove older LE certs for the same host to avoid pile-up.
        foreach (var existing in store.Certificates)
        {
            if (existing.FriendlyName == $"TeslaPC LE ({host})" && existing.Thumbprint != cert.Thumbprint)
                store.Remove(existing);
        }
        store.Add(cert);
    }

    // ---- Cloudflare API ----

    private static void AuthorizeCloudflare(HttpRequestMessage req, string token) =>
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

    private static async Task<string> GetZoneIdAsync(string host, string token)
    {
        // Find the zone whose name is a suffix of the host (e.g. host my.thelpers.com -> thelpers.com).
        using var req = new HttpRequestMessage(HttpMethod.Get, "https://api.cloudflare.com/client/v4/zones?per_page=50");
        AuthorizeCloudflare(req, token);
        using var resp = await Http.SendAsync(req);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        string? zoneId = null; string? matched = null;
        foreach (var z in doc.RootElement.GetProperty("result").EnumerateArray())
        {
            string name = z.GetProperty("name").GetString() ?? "";
            if (host == name || host.EndsWith("." + name, StringComparison.OrdinalIgnoreCase))
            {
                if (matched == null || name.Length > matched.Length) // most specific zone
                {
                    matched = name;
                    zoneId = z.GetProperty("id").GetString();
                }
            }
        }
        if (zoneId == null) throw new InvalidOperationException($"No Cloudflare zone found for {host}.");
        return zoneId;
    }

    private static async Task<string> CreateTxtRecordAsync(string zoneId, string name, string content, string token)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, $"https://api.cloudflare.com/client/v4/zones/{zoneId}/dns_records");
        AuthorizeCloudflare(req, token);
        req.Content = JsonContent.Create(new { type = "TXT", name, content, ttl = 60 });
        using var resp = await Http.SendAsync(req);
        string body = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode) throw new InvalidOperationException($"Cloudflare create TXT failed: {body}");
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.GetProperty("result").GetProperty("id").GetString()!;
    }

    /// <summary>Deletes every TXT record with the given name (stale challenge leftovers).</summary>
    private static async Task DeleteExistingTxtRecordsAsync(string zoneId, string name, string token)
    {
        try
        {
            using var req = new HttpRequestMessage(
                HttpMethod.Get,
                $"https://api.cloudflare.com/client/v4/zones/{zoneId}/dns_records?type=TXT&name={Uri.EscapeDataString(name)}&per_page=100");
            AuthorizeCloudflare(req, token);
            using var resp = await Http.SendAsync(req);
            if (!resp.IsSuccessStatusCode) return;
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            foreach (var record in doc.RootElement.GetProperty("result").EnumerateArray())
            {
                string? id = record.GetProperty("id").GetString();
                if (id == null) continue;
                Console.WriteLine($"[ACME] Removing stale challenge TXT record for {name}.");
                await DeleteTxtRecordAsync(zoneId, id, token);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ACME] Stale TXT cleanup failed (continuing): {ex.Message}");
        }
    }

    private static async Task DeleteTxtRecordAsync(string zoneId, string recordId, string token)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Delete, $"https://api.cloudflare.com/client/v4/zones/{zoneId}/dns_records/{recordId}");
            AuthorizeCloudflare(req, token);
            using var resp = await Http.SendAsync(req);
        }
        catch { /* best-effort cleanup */ }
    }

    // ---- DNS propagation check via Cloudflare DoH ----

    private static async Task WaitForDnsAsync(string name, string expected)
    {
        for (int i = 0; i < 30; i++) // up to ~90s
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, $"https://cloudflare-dns.com/dns-query?name={Uri.EscapeDataString(name)}&type=TXT");
                req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/dns-json"));
                using var resp = await Http.SendAsync(req);
                if (resp.IsSuccessStatusCode)
                {
                    using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
                    if (doc.RootElement.TryGetProperty("Answer", out var ans))
                    {
                        foreach (var a in ans.EnumerateArray())
                        {
                            string data = (a.GetProperty("data").GetString() ?? "").Trim('"');
                            if (data == expected) return;
                        }
                    }
                }
            }
            catch { /* keep polling */ }
            await Task.Delay(3000);
        }
        Console.WriteLine("[ACME] Warning: TXT record not confirmed via DoH; attempting validation anyway.");
    }

    private static async Task<Authorization> PollAuthorizationAsync(IAuthorizationContext authz)
    {
        for (int i = 0; i < 30; i++)
        {
            var res = await authz.Resource();
            if (res.Status != AuthorizationStatus.Pending) return res;
            await Task.Delay(2000);
        }
        return await authz.Resource();
    }
}
