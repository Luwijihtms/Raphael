using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.VisualBasic.FileIO;

namespace Raphael;

/// <summary>
/// Fixes that tidy the PC, each a named action with fixed limits (no free-form commands). Anything that deletes shows what
/// it would delete first and waits for a yes (see <see cref="PcManager.Gate"/>). Old downloads go to the Recycle Bin, so they
/// can still be recovered; temp files and the Recycle Bin itself are gone for good.
/// </summary>
public static class SystemFix
{
    private static readonly TimeSpan TempAge = TimeSpan.FromDays(1);
    private static readonly TimeSpan DownloadAge = TimeSpan.FromDays(90);
    private const int MaxDownloadFiles = 500;

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct ShQueryRbInfo { public int CbSize; public long Size; public long Items; }

    [DllImport("shell32.dll")] private static extern int SHQueryRecycleBin(string? root, ref ShQueryRbInfo info);
    [DllImport("shell32.dll")] private static extern int SHEmptyRecycleBin(IntPtr hwnd, string? root, uint flags);

    public static string Run(string? action, bool confirmed)
    {
        action = (action ?? "").Trim().ToLowerInvariant();
        switch (action)
        {
            case "flush_dns":
                return RunTool("ipconfig.exe", "/flushdns");

            case "clean_temp":
            case "empty_recycle_bin":
            case "old_downloads":
                var preview = Preview(action);
                var gate = PcManager.Gate("fix", new[] { action }, confirmed);
                if (gate != null) return $"{preview}\n{gate}";
                return action switch
                {
                    "clean_temp" => CleanTemp(),
                    "empty_recycle_bin" => EmptyBin(),
                    _ => TrashOldDownloads()
                };

            default:
                return "Unknown fix. Use one of: clean_temp, empty_recycle_bin, old_downloads, flush_dns.";
        }
    }

    // ---- what each one would do -------------------------------------------------------------------------

    private static string Preview(string action)
    {
        switch (action)
        {
            case "clean_temp":
            {
                var files = OldTempFiles();
                return $"Temp folder: {files.Count} file(s) older than a day, {files.Sum(f => f.Length) / 1048576.0:0} MB. They would be deleted for good; files in use are skipped.";
            }
            case "empty_recycle_bin":
            {
                var info = new ShQueryRbInfo { CbSize = Marshal.SizeOf<ShQueryRbInfo>() };
                SHQueryRecycleBin(null, ref info);
                return $"Recycle Bin: {info.Items} item(s), {info.Size / 1048576.0:0} MB. They would be gone for good.";
            }
            default:
            {
                var files = OldDownloads();
                var biggest = string.Join(", ", files.OrderByDescending(f => f.Length).Take(6).Select(f => $"{f.Name} ({f.Length / 1048576.0:0} MB)"));
                return $"Downloads: {files.Count} file(s) not changed for 90+ days, {files.Sum(f => f.Length) / 1048576.0:0} MB. They would go to the Recycle Bin (recoverable). Largest: {(biggest.Length > 0 ? biggest : "none")}.";
            }
        }
    }

    private static List<FileInfo> OldTempFiles()
    {
        var list = new List<FileInfo>();
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
        try
        {
            foreach (var f in new DirectoryInfo(Path.GetTempPath()).EnumerateFiles("*", options))
                if (DateTime.Now - f.LastWriteTime > TempAge) list.Add(f);
        }
        catch { }
        return list;
    }

    private static List<FileInfo> OldDownloads()
    {
        var list = new List<FileInfo>();
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        if (!Directory.Exists(dir)) return list;
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
        try
        {
            foreach (var f in new DirectoryInfo(dir).EnumerateFiles("*", options))
            {
                if (DateTime.Now - f.LastWriteTime > DownloadAge) list.Add(f);
                if (list.Count >= MaxDownloadFiles) break;
            }
        }
        catch { }
        return list;
    }

    // ---- doing it ---------------------------------------------------------------------------------------

    private static string CleanTemp()
    {
        int deleted = 0, skipped = 0; long freed = 0;
        foreach (var f in OldTempFiles())
        {
            try { var size = f.Length; f.Delete(); deleted++; freed += size; }
            catch { skipped++; }
        }
        return $"Temp cleanup: deleted {deleted} file(s), freed {freed / 1048576.0:0} MB; {skipped} in use or protected were skipped.";
    }

    private static string EmptyBin()
    {
        var hr = SHEmptyRecycleBin(IntPtr.Zero, null, 0x7); // no confirmation, no progress window, no sound
        return hr == 0 ? "Recycle Bin emptied." : $"Could not empty the Recycle Bin (code {hr}); it may already be empty.";
    }

    private static string TrashOldDownloads()
    {
        int moved = 0, failed = 0; long bytes = 0;
        foreach (var f in OldDownloads())
        {
            try
            {
                var size = f.Length;
                FileSystem.DeleteFile(f.FullName, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
                moved++; bytes += size;
            }
            catch { failed++; }
        }
        return $"Moved {moved} old download(s) ({bytes / 1048576.0:0} MB) to the Recycle Bin; {failed} could not be moved. Emptying the Recycle Bin makes the space free.";
    }

    private static string RunTool(string exe, string args)
    {
        var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, exe), args)
        {
            RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true
        };
        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEnd();
        p.WaitForExit(10000);
        return string.IsNullOrWhiteSpace(output) ? "Done." : output.Trim();
    }
}
