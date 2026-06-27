using System.Diagnostics;

namespace PrimaryProcess;

internal enum HotspotResult { AlreadyOn, TurnedOn, Failed }

/// <summary>
/// Controls Windows Mobile Hotspot via the WinRT tethering API (through a PowerShell child process,
/// to keep the app on its current target framework). Used so the Tesla browser bypass has an adapter
/// to attach the CGNAT IP to. Requires an active internet profile to share, and elevation.
/// </summary>
internal static class HotspotManager
{
    /// <summary>Last observed hotspot state (updated by EnsureHotspotOn / TurnOff). For the UI.</summary>
    public static volatile bool LastKnownOn;

    /// <summary>Set when the user turned the hotspot off from the UI, so the watchdog leaves it off.</summary>
    public static volatile bool ManuallyOff;

    private const string OnScript = @"
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

    private const string OffScript = @"
$ErrorActionPreference = 'Stop'
[Windows.Networking.NetworkOperators.NetworkOperatorTetheringManager, Windows.Networking.NetworkOperators, ContentType = WindowsRuntime] | Out-Null
[Windows.Networking.Connectivity.NetworkInformation, Windows.Networking.Connectivity, ContentType = WindowsRuntime] | Out-Null
$profile = [Windows.Networking.Connectivity.NetworkInformation]::GetInternetConnectionProfile()
if ($null -eq $profile) { Write-Output 'NOPROFILE'; exit 3 }
$mgr = [Windows.Networking.NetworkOperators.NetworkOperatorTetheringManager]::CreateFromConnectionProfile($profile)
if ($mgr.TetheringOperationalState.ToString() -ne 'On') { Write-Output 'ALREADYOFF'; exit 0 }
$null = $mgr.StopTetheringAsync()
for ($i = 0; $i -lt 30; $i++) {
    if ($mgr.TetheringOperationalState.ToString() -ne 'On') { Write-Output 'TURNEDOFF'; exit 0 }
    Start-Sleep -Milliseconds 500
}
exit 2
";

    /// <summary>Ensures Mobile Hotspot is on. Reports whether it was already on, was turned on, or failed.</summary>
    public static HotspotResult EnsureHotspotOn()
    {
        string output = RunPs(OnScript);
        switch (output)
        {
            case "ALREADYON":
                LastKnownOn = true;
                return HotspotResult.AlreadyOn;
            case "TURNEDON":
                LastKnownOn = true;
                Console.WriteLine("[Hotspot] Turned Mobile Hotspot on.");
                return HotspotResult.TurnedOn;
            case "NOPROFILE":
                Console.WriteLine("[Hotspot] No internet connection profile to share; cannot enable Mobile Hotspot.");
                return HotspotResult.Failed;
            default:
                Console.WriteLine($"[Hotspot] Could not enable Mobile Hotspot ({output}).");
                return HotspotResult.Failed;
        }
    }

    /// <summary>Turns Mobile Hotspot off. Returns true if it is off afterward.</summary>
    public static bool TurnOff()
    {
        string output = RunPs(OffScript);
        bool off = output is "TURNEDOFF" or "ALREADYOFF";
        if (off)
        {
            LastKnownOn = false;
            Console.WriteLine("[Hotspot] Mobile Hotspot turned off.");
        }
        else
        {
            Console.WriteLine($"[Hotspot] Could not turn Mobile Hotspot off ({output}).");
        }
        return off;
    }

    private static string RunPs(string script)
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
            if (process == null) return "NOPROCESS";
            string output = process.StandardOutput.ReadToEnd().Trim();
            process.StandardError.ReadToEnd();
            process.WaitForExit(30000);
            return output;
        }
        catch (Exception ex)
        {
            return "ERROR:" + ex.Message;
        }
    }
}
