using System.Diagnostics;
using System.Net.NetworkInformation;

namespace TeslaBrowserBypass
{
    /// <summary>
    /// Bypasses Tesla browser's RFC 1918 private IP blocking by assigning a
    /// non-private secondary IP (from the CGNAT 100.64.0.0/10 range) to the
    /// Windows Mobile Hotspot adapter and setting up port forwarding.
    ///
    /// How it works:
    /// 1. Finds the Mobile Hotspot adapter (Microsoft Wi-Fi Direct Virtual Adapter)
    /// 2. Adds 100.64.0.1 as a secondary IP on that adapter
    /// 3. Sets up netsh portproxy rules to forward from the bypass IP to localhost
    /// 4. Tesla browser connects to http://100.64.0.1:8080 — not RFC 1918, so it's allowed
    /// </summary>
    public class TeslaBrowserBypass : IDisposable
    {
        public const string BypassIP = "100.64.0.1";
        public const string SubnetMask = "255.255.255.0";

        private string? _adapterName;
        private bool _ipAdded = false;
        private readonly List<(int listenPort, int connectPort)> _proxyRules = new();
        private bool _disposed = false;

        /// <summary>
        /// Sets up the Tesla browser bypass: adds secondary IP and port forwarding rules.
        /// Must be run as administrator.
        /// </summary>
        /// <param name="httpPort">HTTP port the server listens on (default 8080)</param>
        /// <param name="httpsPort">HTTPS port the server listens on (default 8443)</param>
        /// <returns>True if setup succeeded, false otherwise</returns>
        public bool Setup(int httpPort = 8080, int httpsPort = 8443)
        {
            _adapterName = FindHotspotAdapter();
            if (_adapterName == null)
            {
                Console.WriteLine("[TeslaBrowserBypass] WARNING: Could not find Mobile Hotspot adapter.");
                Console.WriteLine("[TeslaBrowserBypass] Make sure Windows Mobile Hotspot is enabled.");
                Console.WriteLine("[TeslaBrowserBypass] Tesla browser bypass will not be available.");
                return false;
            }

            Console.WriteLine($"[TeslaBrowserBypass] Found hotspot adapter: {_adapterName}");

            // Add secondary IP to the hotspot adapter
            if (!AddSecondaryIP(_adapterName))
            {
                Console.WriteLine("[TeslaBrowserBypass] Failed to add secondary IP. Are you running as administrator?");
                return false;
            }
            _ipAdded = true;

            // Set up port forwarding: bypass IP -> localhost
            if (!AddPortProxy(httpPort, httpPort) || !AddPortProxy(httpsPort, httpsPort))
            {
                Console.WriteLine("[TeslaBrowserBypass] Failed to set up port forwarding. Are you running as administrator?");
                Cleanup();
                return false;
            }

            if (!AddFirewallRule(httpPort, httpsPort))
            {
                Console.WriteLine("[TeslaBrowserBypass] Failed to add firewall rule. Are you running as administrator?");
                Cleanup();
                return false;
            }

            Console.WriteLine($"[TeslaBrowserBypass] Tesla browser bypass active!");
            Console.WriteLine($"[TeslaBrowserBypass] Access from Tesla: http://{BypassIP}:{httpPort}");
            return true;
        }

        /// <summary>
        /// Finds the Windows Mobile Hotspot virtual adapter by looking for
        /// "Microsoft Wi-Fi Direct Virtual Adapter" or "Local Area Connection*" names.
        /// </summary>
        private static string? FindHotspotAdapter()
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                // Mobile Hotspot adapters show up as "Local Area Connection* N"
                // and use the Microsoft Wi-Fi Direct Virtual Adapter
                if (nic.OperationalStatus == OperationalStatus.Up &&
                    (nic.Description.Contains("Microsoft Wi-Fi Direct Virtual Adapter", StringComparison.OrdinalIgnoreCase) ||
                     nic.Description.Contains("Microsoft Hosted Network Virtual Adapter", StringComparison.OrdinalIgnoreCase)))
                {
                    return nic.Name;
                }
            }

            // Fallback: look for any adapter with the hotspot IP range 192.168.137.x
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;

                var ipProps = nic.GetIPProperties();
                foreach (var addr in ipProps.UnicastAddresses)
                {
                    if (addr.Address.ToString().StartsWith("192.168.137."))
                    {
                        return nic.Name;
                    }
                }
            }

            return null;
        }

        /// <summary>
        /// Adds the bypass IP as a secondary address on the specified adapter.
        /// </summary>
        private static bool AddSecondaryIP(string adapterName)
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                var ipProps = nic.GetIPProperties();
                foreach (var addr in ipProps.UnicastAddresses)
                {
                    if (addr.Address.ToString() != BypassIP)
                        continue;

                    if (nic.Name == adapterName)
                    {
                        Console.WriteLine($"[TeslaBrowserBypass] IP {BypassIP} already assigned to {adapterName}");
                        return true;
                    }

                    Console.WriteLine($"[TeslaBrowserBypass] IP {BypassIP} is assigned to {nic.Name}, not {adapterName}");
                    return false;
                }
            }

            var result = RunNetsh($"interface ipv4 add address \"{adapterName}\" {BypassIP} {SubnetMask} SkipAsSource=True");
            if (result.exitCode != 0)
            {
                Console.WriteLine($"[TeslaBrowserBypass] netsh add address failed (exit {result.exitCode}): {result.output}");
                return false;
            }

            Console.WriteLine($"[TeslaBrowserBypass] Added {BypassIP} to adapter \"{adapterName}\"");
            return true;
        }

        /// <summary>
        /// Removes the bypass IP from the adapter.
        /// </summary>
        private static void RemoveSecondaryIP(string adapterName)
        {
            var result = RunNetsh($"interface ipv4 delete address \"{adapterName}\" {BypassIP}");
            if (result.exitCode == 0)
            {
                Console.WriteLine($"[TeslaBrowserBypass] Removed {BypassIP} from adapter \"{adapterName}\"");
            }
            else
            {
                Console.WriteLine($"[TeslaBrowserBypass] Failed to remove IP (exit {result.exitCode}): {result.output}");
            }
        }

        /// <summary>
        /// Adds a netsh portproxy rule to forward from the bypass IP to localhost.
        /// </summary>
        private bool AddPortProxy(int listenPort, int connectPort)
        {
            var result = RunNetsh(
                $"interface portproxy add v4tov4 listenaddress={BypassIP} listenport={listenPort} connectaddress=127.0.0.1 connectport={connectPort}");

            if (result.exitCode == 0)
            {
                _proxyRules.Add((listenPort, connectPort));
                Console.WriteLine($"[TeslaBrowserBypass] Port proxy: {BypassIP}:{listenPort} -> 127.0.0.1:{connectPort}");
                return true;
            }

            Console.WriteLine($"[TeslaBrowserBypass] Port proxy failed (exit {result.exitCode}): {result.output}");
            return false;
        }

        /// <summary>
        /// Removes a netsh portproxy rule.
        /// </summary>
        private static void RemovePortProxy(string listenAddress, int listenPort)
        {
            RunNetsh($"interface portproxy delete v4tov4 listenaddress={listenAddress} listenport={listenPort}");
        }

        /// <summary>
        /// Adds a Windows Firewall rule to allow inbound traffic on the bypass IP.
        /// </summary>
        public static bool AddFirewallRule(int httpPort, int httpsPort)
        {
            var result = RunNetsh($"advfirewall firewall add rule name=\"TeslaPC Bypass\" dir=in action=allow protocol=TCP localip={BypassIP} localport={httpPort},{httpsPort}");
            if (result.exitCode == 0)
                return true;

            Console.WriteLine($"[TeslaBrowserBypass] Firewall rule failed (exit {result.exitCode}): {result.output}");
            return false;
        }

        /// <summary>
        /// Removes the TeslaPC firewall rule.
        /// </summary>
        private static void RemoveFirewallRule()
        {
            RunNetsh("advfirewall firewall delete rule name=\"TeslaPC Bypass\"");
        }

        /// <summary>
        /// Cleans up all bypass configuration: removes port proxies, secondary IP, and firewall rules.
        /// </summary>
        public void Cleanup()
        {
            Console.WriteLine("[TeslaBrowserBypass] Cleaning up...");

            foreach (var (listenPort, _) in _proxyRules)
            {
                RemovePortProxy(BypassIP, listenPort);
            }
            _proxyRules.Clear();

            if (_ipAdded && _adapterName != null)
            {
                RemoveSecondaryIP(_adapterName);
                _ipAdded = false;
            }

            RemoveFirewallRule();
            Console.WriteLine("[TeslaBrowserBypass] Cleanup complete.");
        }

        /// <summary>
        /// Runs a netsh command and returns the exit code and output.
        /// </summary>
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

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    Cleanup();
                }
                _disposed = true;
            }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }
    }
}
