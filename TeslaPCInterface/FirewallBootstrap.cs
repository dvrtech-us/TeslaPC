using System.Diagnostics;
using System.Security.Principal;
using System.Text.RegularExpressions;

namespace PrimaryProcess;

/// <summary>
/// Ensures Windows Firewall allows inbound TCP traffic on the TeslaPC server ports.
/// Requires administrator privileges.
/// </summary>
internal static class FirewallBootstrap
{
    private const string RuleName = "TeslaPC Server";

    public static bool TryEnsureFirewallOpen(int httpPort, int httpsPort)
    {
        if (!IsAdministrator())
        {
            Console.WriteLine("[Firewall] Opening ports requires administrator privileges.");
            Console.WriteLine("[Firewall] Remote devices may be blocked until you run as administrator.");
            return false;
        }

        if (RuleExists(httpPort, httpsPort))
        {
            Console.WriteLine($"[Firewall] Inbound TCP {httpPort}/{httpsPort} already allowed.");
            return true;
        }

        RunNetsh($"advfirewall firewall delete rule name=\"{RuleName}\"");

        var result = RunNetsh(
            $"advfirewall firewall add rule name=\"{RuleName}\" dir=in action=allow protocol=TCP localport={httpPort},{httpsPort} enable=yes profile=any");

        if (result.exitCode != 0)
        {
            Console.WriteLine($"[Firewall] Failed to add rule (exit {result.exitCode}): {result.output}");
            return false;
        }

        Console.WriteLine($"[Firewall] Opened inbound TCP ports {httpPort} (HTTP) and {httpsPort} (HTTPS).");
        return true;
    }

    private static bool RuleExists(int httpPort, int httpsPort)
    {
        var result = RunNetsh($"advfirewall firewall show rule name=\"{RuleName}\" verbose");
        if (result.exitCode != 0)
            return false;

        var portMatch = Regex.Match(result.output, @"(?i)LocalPort:\s*([0-9,\s]+)");
        if (!portMatch.Success)
            return false;

        var ports = portMatch.Groups[1].Value
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(int.Parse)
            .ToHashSet();

        return ports.Contains(httpPort) && ports.Contains(httpsPort);
    }

    private static bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
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