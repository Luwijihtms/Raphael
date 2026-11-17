using System.Diagnostics;
using System.Text;

namespace Raphael;

/// <summary>
/// Read-only facts about this PC, so questions like "why is it slow?" or "did it crash last night?" are answered from the
/// machine's real state instead of a guess. Each topic is a fixed script; nothing is changed.
/// </summary>
public static class SystemReport
{
    public static string Run(string? topic)
    {
        topic = (topic ?? "overview").Trim().ToLowerInvariant();
        return topic switch
        {
            "overview" => Ps(OverviewScript, 40),
            "disks" or "disk" => Ps(DisksScript, 30),
            "folders" => BiggestFolders(),
            "network" => Ps(NetworkScript, 45),
            "battery" or "power" => Ps(BatteryScript, 20),
            "updates" => Ps(UpdatesScript, 100),
            "crashes" => Ps(CrashScript, 45),
            "gpu" => Ps(GpuScript, 20),
            _ => "Unknown topic. Use one of: overview, disks, folders, network, battery, updates, crashes, gpu."
        };
    }

    private const string OverviewScript = @"
$os = Get-CimInstance Win32_OperatingSystem
$cpu = Get-CimInstance Win32_Processor | Select-Object -First 1
$up = (Get-Date) - $os.LastBootUpTime
'OS: {0} (build {1})' -f $os.Caption, $os.BuildNumber
'CPU: {0}, {1} cores / {2} threads, load {3}%' -f $cpu.Name.Trim(), $cpu.NumberOfCores, $cpu.NumberOfLogicalProcessors, $cpu.LoadPercentage
'RAM: {0:N1} GB used of {1:N1} GB' -f (($os.TotalVisibleMemorySize - $os.FreePhysicalMemory) / 1MB), ($os.TotalVisibleMemorySize / 1MB)
'Uptime: {0} days {1} hours since the last full boot' -f [int][math]::Floor($up.TotalDays), $up.Hours
Get-CimInstance Win32_LogicalDisk -Filter 'DriveType=3' | ForEach-Object { 'Disk {0} {1:N0} GB free of {2:N0} GB' -f $_.DeviceID, ($_.FreeSpace / 1GB), ($_.Size / 1GB) }
$gpu = Get-CimInstance Win32_VideoController | Select-Object -First 1
if ($gpu) { 'GPU: ' + $gpu.Name }
$b = Get-CimInstance Win32_Battery
if ($b) { 'Battery: {0}%' -f $b.EstimatedChargeRemaining } else { 'Battery: none (desktop PC)' }
";

    private const string DisksScript = @"
Get-CimInstance Win32_LogicalDisk -Filter 'DriveType=3' | ForEach-Object { 'Drive {0} {1:N1} GB free of {2:N1} GB ({3:N0}% free)' -f $_.DeviceID, ($_.FreeSpace / 1GB), ($_.Size / 1GB), (100 * $_.FreeSpace / $_.Size) }
Get-PhysicalDisk | ForEach-Object { 'Physical disk {0}: {1}, health {2}, status {3}' -f $_.FriendlyName, $_.MediaType, $_.HealthStatus, $_.OperationalStatus }
";

    private const string NetworkScript = @"
Get-NetAdapter | Where-Object Status -eq 'Up' | ForEach-Object { 'Adapter: {0} ({1}), link speed {2}' -f $_.Name, $_.InterfaceDescription, $_.LinkSpeed }
$p = Test-Connection 1.1.1.1 -Count 3 -ErrorAction SilentlyContinue
if ($p) { 'Ping 1.1.1.1: average {0} ms, {1} of 3 replies' -f [int](($p | Measure-Object ResponseTime -Average).Average), @($p).Count } else { 'Ping 1.1.1.1: no reply (no internet, or ping blocked)' }
try { [void][Net.Dns]::GetHostAddresses('example.com'); 'DNS lookup: working' } catch { 'DNS lookup: FAILING' }
netsh wlan show interfaces 2>$null | Select-String 'SSID|Signal|Radio type|Receive rate' | ForEach-Object { $_.Line.Trim() }
";

    private const string BatteryScript = @"
$b = Get-CimInstance Win32_Battery
if ($b) { 'Battery: {0}% charge, status code {1} (1 = on battery, 2 = on AC), about {2} minutes left' -f $b.EstimatedChargeRemaining, $b.BatteryStatus, $b.EstimatedRunTime } else { 'No battery (desktop PC).' }
powercfg /getactivescheme
";

    private const string UpdatesScript = @"
try {
  $s = (New-Object -ComObject Microsoft.Update.Session).CreateUpdateSearcher()
  $r = $s.Search(""IsInstalled=0 and Type='Software'"")
  if ($r.Updates.Count -eq 0) { 'Windows Update: nothing pending.' } else { 'Pending updates ({0}):' -f $r.Updates.Count; $r.Updates | Select-Object -First 10 | ForEach-Object { ' - ' + $_.Title } }
} catch { 'Could not ask Windows Update: ' + $_.Exception.Message }
Get-HotFix | Sort-Object InstalledOn -Descending | Select-Object -First 3 | ForEach-Object { 'Recently installed: {0} on {1:d}' -f $_.HotFixID, $_.InstalledOn }
";

    private const string CrashScript = @"
$s = (Get-Date).AddDays(-3)
Get-WinEvent -FilterHashtable @{LogName='System'; Id=41,1001,6008; StartTime=$s} -ErrorAction SilentlyContinue | ForEach-Object { '{0:g} System event {1}: {2}' -f $_.TimeCreated, $_.Id, ($_.Message -split ""`n"")[0] }
Get-WinEvent -FilterHashtable @{LogName='Application'; Id=1000,1002; StartTime=$s} -MaxEvents 15 -ErrorAction SilentlyContinue | ForEach-Object { '{0:g} App crash/hang: {1}' -f $_.TimeCreated, ($_.Message -split ""`n"")[0] }
";

    private const string GpuScript = @"
Get-CimInstance Win32_VideoController | ForEach-Object { '{0}: driver {1} from {2:d}, {3} MB' -f $_.Name, $_.DriverVersion, $_.DriverDate, [int]($_.AdapterRAM / 1MB) }
";

    /// <summary>Runs a fixed PowerShell script and returns what it printed. Killed if it takes longer than the limit.</summary>
    internal static string Ps(string script, int timeoutSeconds)
    {
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        var exe = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        var psi = new ProcessStartInfo(exe, $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand {encoded}")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8
        };

        using var proc = Process.Start(psi)!;
        var output = proc.StandardOutput.ReadToEndAsync();
        _ = proc.StandardError.ReadToEndAsync();
        if (!proc.WaitForExit(timeoutSeconds * 1000))
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
            return "That check took too long and was stopped.";
        }

        var text = output.GetAwaiter().GetResult().Trim();
        return text.Length == 0 ? "Nothing found (no matching events or data)." : text;
    }

    // ---- folder sizes -----------------------------------------------------------------------------------

    /// <summary>Total size of the files under a folder, stopping early (partial = true) once the stopwatch passes the budget.</summary>
    internal static long DirSize(string path, Stopwatch clock, int budgetMs, out bool partial)
    {
        partial = false;
        long total = 0;
        if (!Directory.Exists(path)) return 0;
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
        try
        {
            foreach (var f in new DirectoryInfo(path).EnumerateFiles("*", options))
            {
                total += f.Length;
                if (clock.ElapsedMilliseconds > budgetMs) { partial = true; break; }
            }
        }
        catch { /* a folder that cannot be read just counts what was read */ }
        return total;
    }

    private static string BiggestFolders()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var folders = new[] { "Downloads", "Documents", "Desktop", "Pictures", "Videos", "Music", @"AppData\Local", @"AppData\Roaming", @"AppData\Local\Temp" };
        var clock = Stopwatch.StartNew();
        var sizes = new List<(string Name, long Bytes, bool Partial)>();
        foreach (var name in folders)
        {
            var size = DirSize(Path.Combine(home, name), clock, 40_000, out var partial);
            sizes.Add((name, size, partial));
            if (clock.ElapsedMilliseconds > 40_000) break;
        }

        var sb = new StringBuilder("Folder sizes in the user profile (largest first; AppData\\Local includes Temp):\n");
        foreach (var s in sizes.OrderByDescending(s => s.Bytes))
            sb.AppendLine($"{s.Name}: {s.Bytes / 1073741824.0:0.0} GB{(s.Partial ? " (at least; stopped counting early)" : "")}");
        return sb.ToString().Trim();
    }
}
