using System.Diagnostics;
using System.Text;
using Microsoft.Win32;

namespace Raphael;

/// <summary>
/// "What is slowing my PC?": lists the heaviest processes and the startup items, ends background processes and turns
/// startup items off (and back on). Nothing needs administrator rights, and nothing is ever deleted:
/// a startup item is switched off the same way Task Manager does it (the StartupApproved flag), so it can be switched back.
/// Ending a process or switching a startup item off is a two-step action: the first call only records the plan and
/// tells the model to ask the user; only a second call with confirmed=true, for the same names, does it.
/// </summary>
public static class PcManager
{
    private static readonly TimeSpan ConfirmWindow = TimeSpan.FromMinutes(10);
    private static readonly Dictionary<string, DateTime> Pending = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Never ended, whatever the user says: core Windows, security software, Raphael and her voice engine.</summary>
    private static readonly HashSet<string> NeverEnd = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer", "dwm", "winlogon", "csrss", "wininit", "services", "lsass", "smss", "svchost", "system", "registry",
        "idle", "spoolsv", "taskhostw", "sihost", "searchhost", "startmenuexperiencehost", "shellexperiencehost",
        "textinputhost", "runtimebroker", "ctfmon", "fontdrvhost", "memory compression", "audiodg", "conhost",
        "msmpeng", "nissrv", "securityhealthservice", "securityhealthsystray", "mpdefendercoreservice", "smartscreen",
        "raphael", "voicevox", "vv-engine", "dotnet"
    };

    // ---- processes --------------------------------------------------------------------------------------

    /// <summary>Measures every process over a short window and sums the ones that share a name.</summary>
    private static List<(string Name, double Cpu, long Mem, int Count)> Sample(int milliseconds)
    {
        var cores = Environment.ProcessorCount;
        var before = Snapshot();
        var started = Stopwatch.StartNew();
        Thread.Sleep(milliseconds);
        var after = Snapshot();
        var seconds = started.Elapsed.TotalSeconds;

        return after.GroupBy(p => p.Value.Name, StringComparer.OrdinalIgnoreCase).Select(g =>
        {
            double cpu = 0;
            foreach (var p in g)
                if (before.TryGetValue(p.Key, out var b)) cpu += (p.Value.Cpu - b.Cpu).TotalSeconds / seconds / cores * 100;
            return (Name: g.Key, Cpu: cpu, Mem: g.Sum(p => p.Value.Memory), Count: g.Count());
        }).ToList();
    }

    /// <summary>The busiest program right now (idle and the System process left out), for the background watcher.</summary>
    internal static (string Name, double Pct)? TopCpu()
    {
        var top = Sample(2000).Where(g => !g.Name.Equals("idle", StringComparison.OrdinalIgnoreCase)
                                          && !g.Name.Equals("system", StringComparison.OrdinalIgnoreCase))
                              .OrderByDescending(g => g.Cpu).FirstOrDefault();
        return top.Name == null ? null : (top.Name, top.Cpu);
    }

    /// <summary>The program holding the most memory right now.</summary>
    internal static (string Name, long Bytes)? TopMemory()
    {
        var top = Snapshot().Values.GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => (Name: g.Key, Bytes: g.Sum(p => p.Memory))).OrderByDescending(g => g.Bytes).FirstOrDefault();
        return top.Name == null ? null : (top.Name, top.Bytes);
    }

    public static string ListProcesses()
    {
        var groups = Sample(1500);

        string Line((string Name, double Cpu, long Mem, int Count) g) =>
            $"{g.Name}: {g.Cpu:0.#}% CPU, {g.Mem / 1048576.0:0} MB{(g.Count > 1 ? $", {g.Count} processes" : "")}{(IsProtectedName(g.Name) ? " [protected, will not be ended]" : "")}";

        var sb = new StringBuilder("Busiest right now (CPU is the share of the whole PC):\n");
        foreach (var g in groups.OrderByDescending(g => g.Cpu).Take(10)) sb.AppendLine(Line(g));
        sb.AppendLine("Using the most memory:");
        foreach (var g in groups.OrderByDescending(g => g.Mem).Take(8)) sb.AppendLine(Line(g));
        return sb.ToString().Trim();
    }

    private sealed record Snap(string Name, TimeSpan Cpu, long Memory);

    private static Dictionary<int, Snap> Snapshot()
    {
        var map = new Dictionary<int, Snap>();
        foreach (var p in Process.GetProcesses())
        {
            try { map[p.Id] = new Snap(p.ProcessName, p.TotalProcessorTime, p.WorkingSet64); }
            catch { /* protected or already gone: not ours to end anyway */ }
            finally { p.Dispose(); }
        }
        return map;
    }

    private static bool IsProtectedName(string name) => NeverEnd.Contains(name);

    private static bool IsProtected(Process p)
    {
        if (NeverEnd.Contains(p.ProcessName)) return true;
        try
        {
            if (p.SessionId == 0) return true; // services
            var path = p.MainModule?.FileName ?? "";
            var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            if (path.StartsWith(windows, StringComparison.OrdinalIgnoreCase)) return true;
            if (path.Contains(@"\Windows Defender\", StringComparison.OrdinalIgnoreCase)) return true;
            if (path.Contains("VOICEVOX", StringComparison.OrdinalIgnoreCase)) return true;
            if (path.StartsWith(AppContext.BaseDirectory, StringComparison.OrdinalIgnoreCase)) return true; // Raphael's own folder
        }
        catch { return true; /* cannot even inspect it: leave it alone */ }
        return false;
    }

    public static string EndProcesses(IReadOnlyList<string> names, bool confirmed)
    {
        names = Clean(names);
        if (names.Count == 0) return "No process names were given. Use list_processes first and pass the exact names.";

        var gate = Gate("end", names, confirmed);
        if (gate != null) return gate;

        var sb = new StringBuilder();
        foreach (var name in names)
        {
            var procs = Process.GetProcessesByName(name);
            if (procs.Length == 0) { sb.AppendLine($"{name}: not running."); continue; }

            int ended = 0, refused = 0, failed = 0;
            foreach (var p in procs)
            {
                try
                {
                    if (IsProtected(p)) { refused++; continue; }
                    if (p.MainWindowHandle != IntPtr.Zero) { p.CloseMainWindow(); if (p.WaitForExit(2000)) { ended++; continue; } }
                    p.Kill();
                    p.WaitForExit(2000);
                    ended++;
                }
                catch { failed++; }
                finally { p.Dispose(); }
            }
            sb.AppendLine($"{name}: ended {ended}" + (refused > 0 ? $", {refused} protected (left alone)" : "") + (failed > 0 ? $", {failed} could not be ended (needs administrator rights)" : "") + ".");
        }
        return sb.ToString().Trim();
    }

    // ---- startup items ----------------------------------------------------------------------------------

    private sealed record StartupEntry(string Name, string Command, string Source, bool IsFolder, bool AllUsers)
    {
        /// <summary>The value under StartupApproved: the registry value's name, or the shortcut's file name.</summary>
        public string ApprovedName => Name;
        public string ApprovedKey => IsFolder ? "StartupFolder" : "Run";
    }

    private const string ApprovedRoot = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\";

    private static List<StartupEntry> ReadStartup()
    {
        var list = new List<StartupEntry>();

        void FromRun(RegistryKey hive, string path, string source, bool all)
        {
            try
            {
                using var key = hive.OpenSubKey(path);
                if (key == null) return;
                foreach (var n in key.GetValueNames())
                    list.Add(new StartupEntry(n, key.GetValue(n)?.ToString() ?? "", source, false, all));
            }
            catch { }
        }

        FromRun(Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Run", "your logins (registry)", false);
        FromRun(Registry.LocalMachine, @"Software\Microsoft\Windows\CurrentVersion\Run", "all users (registry)", true);
        FromRun(Registry.LocalMachine, @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Run", "all users, 32-bit (registry)", true);

        void FromFolder(Environment.SpecialFolder folder, string source, bool all)
        {
            try
            {
                var dir = Environment.GetFolderPath(folder);
                if (!Directory.Exists(dir)) return;
                foreach (var f in Directory.GetFiles(dir).Where(f => !f.EndsWith("desktop.ini", StringComparison.OrdinalIgnoreCase)))
                    list.Add(new StartupEntry(Path.GetFileName(f), f, source, true, all));
            }
            catch { }
        }

        FromFolder(Environment.SpecialFolder.Startup, "Startup folder", false);
        FromFolder(Environment.SpecialFolder.CommonStartup, "Startup folder (all users)", true);
        return list;
    }

    /// <summary>Task Manager's flag: the first byte is 2 (or 6) for enabled and 3 for disabled. The user's setting wins over the machine's.</summary>
    private static bool IsEnabled(StartupEntry e)
    {
        foreach (var hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            try
            {
                using var key = hive.OpenSubKey(ApprovedRoot + e.ApprovedKey);
                if (key?.GetValue(e.ApprovedName) is byte[] { Length: > 0 } data) return (data[0] & 1) == 0;
            }
            catch { }
        }
        return true;
    }

    private static bool IsOurs(StartupEntry e) =>
        e.Name.Contains("raphael", StringComparison.OrdinalIgnoreCase) || e.Command.Contains("raphael", StringComparison.OrdinalIgnoreCase);

    public static string ListStartup()
    {
        var entries = ReadStartup();
        if (entries.Count == 0) return "No startup items were found.";

        var sb = new StringBuilder($"{entries.Count} startup item(s):\n");
        foreach (var e in entries.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase))
        {
            var cmd = e.Command.Length > 80 ? e.Command[..80] + "..." : e.Command;
            sb.AppendLine($"{e.Name}: {(IsEnabled(e) ? "ON" : "off")} ({e.Source}) {cmd}{(IsOurs(e) ? " [Raphael's own, will not be turned off]" : "")}");
        }
        return sb.ToString().Trim();
    }

    /// <param name="enable">True turns items back on (no confirmation needed, it is what the user asked for); false turns them off after confirmation.</param>
    public static string SetStartup(IReadOnlyList<string> names, bool enable, bool confirmed)
    {
        names = Clean(names);
        if (names.Count == 0) return "No startup item names were given. Use list_startup first and pass the names.";

        if (!enable)
        {
            var gate = Gate("startup-off", names, confirmed);
            if (gate != null) return gate;
        }

        var all = ReadStartup();
        var sb = new StringBuilder();
        foreach (var name in names)
        {
            var matches = all.Where(e => e.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).ToList();
            if (matches.Count == 0) matches = all.Where(e => e.Name.Contains(name, StringComparison.OrdinalIgnoreCase)).ToList();
            if (matches.Count == 0) { sb.AppendLine($"{name}: no startup item with that name."); continue; }
            if (matches.Count > 3) { sb.AppendLine($"{name}: matches {matches.Count} items, so nothing was changed. Use the exact name."); continue; }

            foreach (var e in matches)
            {
                if (IsOurs(e)) { sb.AppendLine($"{e.Name}: that is Raphael's own start-up, left alone."); continue; }
                try
                {
                    using var key = Registry.CurrentUser.CreateSubKey(ApprovedRoot + e.ApprovedKey);
                    var data = new byte[12];
                    data[0] = enable ? (byte)2 : (byte)3;
                    if (!enable) BitConverter.GetBytes(DateTime.Now.ToFileTime()).CopyTo(data, 4);
                    key.SetValue(e.ApprovedName, data, RegistryValueKind.Binary);
                    sb.AppendLine($"{e.Name}: turned {(enable ? "on" : "off")} for the next start-up ({(IsEnabled(e) == enable ? "confirmed" : "could not be confirmed")}).");
                }
                catch (Exception ex) { sb.AppendLine($"{e.Name}: could not be changed ({ex.Message})."); }
            }
        }
        return sb.ToString().Trim();
    }

    // ---- shared -----------------------------------------------------------------------------------------

    private static List<string> Clean(IReadOnlyList<string> names) =>
        names.Select(n => n.Trim()).Select(n => n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? n[..^4] : n)
             .Where(n => n.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>
    /// Null when the action may go ahead. Otherwise the text to give the model: the plan is recorded and the user must be
    /// asked. A confirmed call only goes through for names that were proposed (unconfirmed) in the last few minutes.
    /// </summary>
    internal static string? Gate(string kind, IReadOnlyList<string> names, bool confirmed)
    {
        lock (Pending)
        {
            var now = DateTime.Now;
            foreach (var old in Pending.Where(p => now - p.Value > ConfirmWindow).Select(p => p.Key).ToList()) Pending.Remove(old);

            if (confirmed && names.All(n => Pending.ContainsKey($"{kind}:{n}")))
            {
                foreach (var n in names) Pending.Remove($"{kind}:{n}");
                return null;
            }

            foreach (var n in names) Pending[$"{kind}:{n}"] = now;
            var what = kind switch
            {
                "end" => "end these processes",
                "startup-off" => "turn these startup items off",
                "fix" => "run this fix",
                "admin" => "run this administrator action (Windows will also show its own Yes/No prompt)",
                _ => "do this"
            };
            return $"NOT DONE YET. Ask the user for permission to {what}: {string.Join(", ", names)}. " +
                   "Tell them plainly what it does and what they lose (unsaved work, a program not opening at start-up, deleted files). " +
                   "Only when they answer yes in their next message, call the same tool again with confirmed=true and the same names.";
        }
    }
}
