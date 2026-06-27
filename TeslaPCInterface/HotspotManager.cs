using System.Diagnostics;

namespace PrimaryProcess;

/// <summary>
/// Turns on Windows Mobile Hotspot if it is off, so the Tesla browser bypass has an adapter to
/// attach the CGNAT IP to. Uses the WinRT tethering API via a PowerShell child process (keeps the
/// app on its current target framework, consistent with the other shell-outs in this project).
/// Requires the connection being shared to have an active internet profile, and elevation.
/// </summary>
internal static class HotspotManager
{
    // Fire StartTetheringAsync (it begins immediately) and poll the operational state until On.
    // Avoids the WinRT AsTask/await interop, which isn't always available in Windows PowerShell.
    private const string Script = @"
$ErrorActionPreference = 'Stop'
[Windows.Networking.NetworkOperators.NetworkOperatorTetheringManager, Windows.Networking.NetworkOperators, ContentType = WindowsRuntime] | Out-Null
[Windows.Networking.Connectivity.NetworkInformation, Windows.Networking.Connectivity, ContentType = WindowsRuntime] | Out-Null
$profile = [Windows.Networking.Connectivity.NetworkInformation]::GetInternetConnectionProfile()
if ($null -eq $profile) { Write-Output 'NOPROFILE'; exit 3 }
$mgr = [Windows.Networking.NetworkOperators.NetworkOperatorTetheringManager]::CreateFromConnectionProfile($profile)
if ($mgr.TetheringOperationalState.ToString() -eq 'On') { Write-Output 'ALREADYON'; exit 0 }
$null = $mgr.StartTetheringAsync()
for ($i = 0; $i -lt 30; $i++) {
    if ($mgr.TetheringOperationalState.ToString() -eq 'On') { Write-Output 'TURNEDON'; exit 0 }
    Start-Sleep -Milliseconds 500
}
Write-Output ('STATE:' + $mgr.TetheringOperationalState)
exit 2
";

    /// <summary>Ensures Mobile Hotspot is on. Returns true if it is on afterward.</summary>
    public static bool EnsureHotspotOn()
    {
        try
        {
            string encoded = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(Script));
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
            {
                Console.WriteLine("[Hotspot] Could not start PowerShell to enable Mobile Hotspot.");
                return false;
            }

            string output = process.StandardOutput.ReadToEnd().Trim();
            string error = process.StandardError.ReadToEnd().Trim();
            process.WaitForExit(30000);

            switch (output)
            {
                case "ALREADYON":
                    Console.WriteLine("[Hotspot] Mobile Hotspot already on.");
                    return true;
                case "TURNEDON":
                    Console.WriteLine("[Hotspot] Turned Mobile Hotspot on.");
                    return true;
                case "NOPROFILE":
                    Console.WriteLine("[Hotspot] No internet connection profile to share; cannot enable Mobile Hotspot.");
                    return false;
                default:
                    Console.WriteLine($"[Hotspot] Could not enable Mobile Hotspot ({output}{(string.IsNullOrEmpty(error) ? "" : "; " + error)}).");
                    return false;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Hotspot] Error enabling Mobile Hotspot: {ex.Message}");
            return false;
        }
    }
}
