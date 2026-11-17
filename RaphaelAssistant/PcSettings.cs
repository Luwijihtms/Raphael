using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace Raphael;

/// <summary>Finding the user's own files, and a few harmless Windows settings (theme, brightness, power plan).</summary>
public static class PcSettings
{
    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint msg, UIntPtr wParam, string lParam, uint flags, uint timeout, out UIntPtr result);

    // ---- find files -------------------------------------------------------------------------------------

    private static readonly string[] KnownFolders = { "Documents", "Desktop", "Downloads", "Pictures", "Videos", "Music" };

    /// <summary>Searches the user's own folders by name (all the words must appear in the file name). Read-only.</summary>
    public static string FindFiles(string? query, string? folder)
    {
        var words = (query ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (words.Length == 0) return "No file name to look for was given.";

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var roots = KnownFolders.Select(f => Path.Combine(home, f)).Where(Directory.Exists).ToList();
        if (!string.IsNullOrWhiteSpace(folder))
        {
            var wanted = folder.Trim();
            var named = KnownFolders.FirstOrDefault(f => f.Equals(wanted, StringComparison.OrdinalIgnoreCase));
            if (named != null) roots = new() { Path.Combine(home, named) };
            else
            {
                string full;
                try { full = Path.GetFullPath(wanted); } catch { return $"'{folder}' is not a folder path."; }
                if (!full.StartsWith(home, StringComparison.OrdinalIgnoreCase) || !Directory.Exists(full))
                    return "Searches stay inside the user's own folders (Documents, Desktop, Downloads, Pictures, Videos, Music).";
                roots = new() { full };
            }
        }

        var found = new List<FileInfo>();
        var clock = Stopwatch.StartNew();
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
        foreach (var root in roots)
        {
            try
            {
                foreach (var f in new DirectoryInfo(root).EnumerateFiles("*", options))
                {
                    if (words.All(w => f.Name.Contains(w, StringComparison.OrdinalIgnoreCase))) found.Add(f);
                    if (found.Count >= 60 || clock.ElapsedSeconds() > 20) break;
                }
            }
            catch { }
            if (found.Count >= 60 || clock.ElapsedSeconds() > 20) break;
        }

        if (found.Count == 0) return $"No file with '{query}' in its name was found in {string.Join(", ", roots.Select(Path.GetFileName))}.";

        var sb = new StringBuilder($"{found.Count}{(found.Count >= 60 ? "+" : "")} match(es), newest first:\n");
        foreach (var f in found.OrderByDescending(f => f.LastWriteTime).Take(15))
            sb.AppendLine($"{f.FullName} ({f.Length / 1024.0:0} KB, changed {f.LastWriteTime:yyyy-MM-dd})");
        return sb.ToString().Trim();
    }

    private static double ElapsedSeconds(this Stopwatch s) => s.Elapsed.TotalSeconds;

    // ---- settings ---------------------------------------------------------------------------------------

    public static string Set(string? setting, string? value)
    {
        setting = (setting ?? "").Trim().ToLowerInvariant();
        value = (value ?? "").Trim().ToLowerInvariant();

        switch (setting)
        {
            case "dark_mode":
            {
                if (value is not ("on" or "off")) return "For dark_mode the value must be 'on' or 'off'.";
                var light = value == "on" ? 0 : 1;
                using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
                key.SetValue("AppsUseLightTheme", light, RegistryValueKind.DWord);
                key.SetValue("SystemUsesLightTheme", light, RegistryValueKind.DWord);
                SendMessageTimeout((IntPtr)0xFFFF, 0x1A, UIntPtr.Zero, "ImmersiveColorSet", 2, 1000, out _); // tell open windows to re-theme
                return $"Dark mode is now {value}.";
            }

            case "brightness":
            {
                if (!int.TryParse(value.TrimEnd('%'), out var percent) || percent is < 0 or > 100) return "For brightness the value must be a number from 0 to 100.";
                var result = SystemReport.Ps($"try {{ (Get-CimInstance -Namespace root/WMI -ClassName WmiMonitorBrightnessMethods).WmiSetBrightness(1, {percent}); 'ok' }} catch {{ 'unsupported' }}", 20);
                return result.Contains("ok") ? $"Brightness set to {percent}%." : "This display does not allow brightness control from software (usual for desktop monitors; use the monitor's own buttons).";
            }

            case "power_plan":
            {
                var scheme = value.Replace(' ', '_') switch
                {
                    "balanced" => "SCHEME_BALANCED",
                    "high_performance" or "performance" => "SCHEME_MIN",
                    "power_saver" or "saver" or "battery_saver" => "SCHEME_MAX",
                    _ => null
                };
                if (scheme == null) return "For power_plan the value must be balanced, high_performance or power_saver.";
                var text = SystemReport.Ps($"powercfg /setactive {scheme}; powercfg /getactivescheme", 15);
                return text.Contains("not exist", StringComparison.OrdinalIgnoreCase) || text.Contains("Invalid", StringComparison.OrdinalIgnoreCase)
                    ? $"This PC has no '{value}' plan. {text}" : text;
            }

            default:
                return "Unknown setting. Use one of: dark_mode (on/off), brightness (0-100), power_plan (balanced/high_performance/power_saver).";
        }
    }
}
