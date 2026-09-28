using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;

namespace Raphael;

/// <summary>How a scan that Raphael started turned out.</summary>
public sealed record ScanResult(string Kind, string? Path, TimeSpan Duration, IReadOnlyList<string> Threats, string? Error, bool FromPhone);

/// <summary>
/// Starts Microsoft Defender scans and reads its status, through Defender's own PowerShell commands. Raphael is not an
/// antivirus: Defender does the scanning, and it alone quarantines anything it finds. She reports; she never deletes,
/// restores, or changes a security setting, and she needs no administrator rights for any of this.
/// </summary>
public sealed class SecurityScanner
{
    private static readonly string PowerShell = System.IO.Path.Combine(
        Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");

    private readonly Func<ScanResult, Task> _onFinished;
    private readonly object _lock = new();
    private string? _runningKind;
    private DateTime _runningSince;

    public SecurityScanner(Func<ScanResult, Task> onFinished) => _onFinished = onFinished;

    // ---- status -----------------------------------------------------------------------------------------

    public async Task<string> StatusAsync()
    {
        const string script = """
            [Console]::OutputEncoding = [Text.Encoding]::UTF8
            try {
              $s = Get-MpComputerStatus -ErrorAction Stop
              $o = [ordered]@{
                antivirus = [bool]$s.AntivirusEnabled
                realtime  = [bool]$s.RealTimeProtectionEnabled
                signatures = $s.AntivirusSignatureLastUpdated.ToString('o')
                quick = $(if ($s.QuickScanEndTime) { $s.QuickScanEndTime.ToString('o') } else { $null })
                full  = $(if ($s.FullScanEndTime)  { $s.FullScanEndTime.ToString('o') }  else { $null })
                threats = @(Get-MpThreatDetection -ErrorAction SilentlyContinue).Count
              }
              $o | ConvertTo-Json -Compress
            } catch { 'ERROR: ' + $_.Exception.Message }
            """;

        var (exit, output, error) = await RunAsync(script);
        output = output.Trim();
        if (exit != 0 || output.StartsWith("ERROR") || !output.StartsWith("{"))
            return $"Could not read Microsoft Defender's status: {(output.Length > 0 ? output : error).Trim()}";

        var json = JsonNode.Parse(output)!;
        string When(string? iso) => iso == null ? "never" : Ago(DateTimeOffset.Parse(iso));

        var running = "";
        lock (_lock)
        {
            if (_runningKind != null)
                running = $" A {_runningKind} scan started by Raphael is running ({Age(DateTime.Now - _runningSince)} so far).";
        }

        return $"Microsoft Defender: antivirus {(json["antivirus"]!.GetValue<bool>() ? "on" : "OFF")}, "
             + $"real-time protection {(json["realtime"]!.GetValue<bool>() ? "on" : "OFF")}. "
             + $"Virus definitions updated {When(json["signatures"]?.GetValue<string>())}. "
             + $"Last quick scan: {When(json["quick"]?.GetValue<string>())}; last full scan: {When(json["full"]?.GetValue<string>())}. "
             + $"Threat detections on record: {json["threats"]!.GetValue<int>()}.{running}";
    }

    // ---- scans ------------------------------------------------------------------------------------------

    /// <param name="type">"quick", "full", or "folder" (a folder or a single file).</param>
    /// <returns>What to tell the user right away; the result is announced later through the callback.</returns>
    public string Start(string type, string? path, bool fromPhone)
    {
        type = (type ?? "quick").Trim().ToLowerInvariant();
        if (type is "custom" or "file") type = "folder";
        if (type is not ("quick" or "full" or "folder")) return $"Unknown scan type '{type}'. Use quick, full or folder.";

        string? target = null;
        if (type == "folder")
        {
            target = ResolvePath(path);
            if (target == null) return $"I could not find '{path}'. Give a folder or file that exists, or say Downloads, Documents or Desktop.";
        }

        lock (_lock)
        {
            if (_runningKind != null)
                return $"A {_runningKind} scan is already running ({Age(DateTime.Now - _runningSince)} so far). Wait for it to finish.";
            _runningKind = type;
            _runningSince = DateTime.Now;
        }

        _ = Task.Run(() => RunScanAsync(type, target, fromPhone));

        return type switch
        {
            "quick" => "Quick scan started. It can take from under a minute to several minutes; Raphael will announce the result when it finishes.",
            "full" => "Full scan started. It can take an hour or more and slows the PC down; Raphael will announce the result when it finishes.",
            _ => $"Scan of '{target}' started. Raphael will announce the result when it finishes."
        };
    }

    private async Task RunScanAsync(string type, string? target, bool fromPhone)
    {
        var started = DateTime.Now;
        ScanResult result;
        try
        {
            var scanArgs = type switch
            {
                "quick" => "-ScanType QuickScan",
                "full" => "-ScanType FullScan",
                _ => "-ScanType CustomScan -ScanPath $env:RAPHAEL_SCAN_PATH" // the path travels in the environment, never in the command text
            };

            // Quick and full scans record when they last finished. A scan that was cancelled leaves that time untouched,
            // which is how we tell a finished scan from a stopped one. (A folder scan has no such record.)
            var endProperty = type switch { "quick" => "QuickScanEndTime", "full" => "FullScanEndTime", _ => null };
            var endCheck = endProperty == null ? "" : $$"""
                $end = (Get-MpComputerStatus -ErrorAction SilentlyContinue).{{endProperty}}
                if ($end) { 'ENDTIME: ' + $end.ToUniversalTime().ToString('o') }
                """;

            var script = $$"""
                [Console]::OutputEncoding = [Text.Encoding]::UTF8
                $start = (Get-Date).AddSeconds(-5)
                try { Start-MpScan {{scanArgs}} -ErrorAction Stop } catch { 'ERROR: ' + $_.Exception.Message; exit 1 }
                {{endCheck}}
                Get-MpThreatDetection -ErrorAction SilentlyContinue | Where-Object { $_.InitialDetectionTime -ge $start } | ForEach-Object {
                  $name = (Get-MpThreat -ThreatID $_.ThreatID -ErrorAction SilentlyContinue).ThreatName
                  'THREAT: ' + $name + ' | ' + ($_.Resources -join ';')
                }
                """;

            var env = target != null ? new Dictionary<string, string> { ["RAPHAEL_SCAN_PATH"] = target } : null;
            var (exit, output, error) = await RunAsync(script, env);

            var lines = output.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
            var errorLine = lines.FirstOrDefault(l => l.StartsWith("ERROR:"));
            var threats = lines.Where(l => l.StartsWith("THREAT:")).Select(l => l["THREAT:".Length..].Trim()).Distinct().ToList();

            var problem = errorLine?[6..].Trim() ?? (exit != 0 ? (error.Trim().Length > 0 ? error.Trim() : "the scan did not complete") : null);

            if (problem == null && endProperty != null)
            {
                // Did Defender record a finish time after we started? If not, the scan was stopped before it ended.
                var endLine = lines.FirstOrDefault(l => l.StartsWith("ENDTIME:"));
                var finishedAt = endLine != null && DateTimeOffset.TryParse(endLine["ENDTIME:".Length..].Trim(), out var t) ? t : (DateTimeOffset?)null;
                if (finishedAt == null || finishedAt.Value < started.ToUniversalTime().AddSeconds(-10))
                    problem = "the scan was stopped before it finished";
            }

            result = new ScanResult(type, target, DateTime.Now - started, threats, problem, fromPhone);
        }
        catch (Exception ex)
        {
            result = new ScanResult(type, target, DateTime.Now - started, Array.Empty<string>(), ex.Message, fromPhone);
        }

        lock (_lock) _runningKind = null;

        try { await _onFinished(result); }
        catch (Exception ex) { Console.WriteLine($"[scan] could not announce the result: {ex.Message}"); }
    }

    // ---- helpers ----------------------------------------------------------------------------------------

    /// <summary>"Downloads" and friends, or a real path, or null if it doesn't exist.</summary>
    private static string? ResolvePath(string? path)
    {
        path = (path ?? "").Trim().Trim('"');
        if (path.Length == 0) return null;

        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var known = path.ToLowerInvariant() switch
        {
            "downloads" => System.IO.Path.Combine(profile, "Downloads"),
            "documents" => Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "desktop" => Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            "pictures" => Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
            "music" => Environment.GetFolderPath(Environment.SpecialFolder.MyMusic),
            "videos" => Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
            _ => null
        };

        var full = System.IO.Path.GetFullPath(Environment.ExpandEnvironmentVariables(known ?? path));
        return Directory.Exists(full) || File.Exists(full) ? full : null;
    }

    private static string Age(TimeSpan span) => span.TotalMinutes < 1 ? "under a minute"
        : span.TotalHours < 1 ? $"{(int)span.TotalMinutes} minute(s)"
        : span.TotalDays < 1 ? $"{(int)span.TotalHours} hour(s)"
        : $"{(int)span.TotalDays} day(s)";

    private static string Ago(DateTimeOffset when) => $"{Age(DateTimeOffset.Now - when)} ago";

    private static async Task<(int Exit, string Output, string Error)> RunAsync(string script, Dictionary<string, string>? env = null)
    {
        var psi = new ProcessStartInfo(PowerShell)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8
        };
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-NonInteractive");
        psi.ArgumentList.Add("-Command");
        psi.ArgumentList.Add(script);
        if (env != null) foreach (var (k, v) in env) psi.Environment[k] = v;

        using var process = Process.Start(psi)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, await stdout, await stderr);
    }
}
