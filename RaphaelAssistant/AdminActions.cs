using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace Raphael;

/// <summary>
/// The few things that need administrator rights, as a short fixed list. Raphael itself never runs elevated: each action
/// starts its own small PowerShell with a hard-coded script, and Windows shows its own Yes/No (UAC) prompt on the screen,
/// so nothing happens unless you press Yes. There is no way to pass her own commands through here.
/// </summary>
public static class AdminActions
{
    private static readonly Dictionary<string, (string Description, string Script, bool Visible)> Actions =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["restart_network"] = ("Restart the active network adapters (the connection drops for a few seconds).",
                "Get-NetAdapter | Where-Object Status -eq 'Up' | Restart-NetAdapter -Confirm:$false", false),
            ["restart_audio"] = ("Restart the Windows audio service (fixes missing or crackling sound).",
                "Restart-Service Audiosrv -Force", false),
            ["restart_print_spooler"] = ("Restart the print spooler (fixes a stuck print queue).",
                "Restart-Service Spooler -Force", false),
            ["restart_windows_update"] = ("Restart the Windows Update services (can unstick a hung update).",
                "Restart-Service bits -Force; Restart-Service wuauserv -Force", false),
            ["repair_system_files"] = ("Scan and repair Windows system files (sfc /scannow). Takes 10-20 minutes; a window shows the progress.",
                "sfc /scannow; Read-Host 'Finished. Press Enter to close'", true),
        };

    public static string Run(string? action, bool confirmed)
    {
        action = (action ?? "").Trim();
        if (!Actions.TryGetValue(action, out var a))
            return $"Unknown action. Available: {string.Join(", ", Actions.Select(x => $"{x.Key} ({x.Value.Description})"))}";

        var gate = PcManager.Gate("admin", new[] { action }, confirmed);
        if (gate != null) return $"{a.Description}\n{gate}";

        try
        {
            var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes($"try {{ {a.Script} }} catch {{ exit 1 }}"));
            var psi = new ProcessStartInfo(
                Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"),
                $"-NoProfile -ExecutionPolicy Bypass -EncodedCommand {encoded}")
            {
                Verb = "runas",
                UseShellExecute = true,
                WindowStyle = a.Visible ? ProcessWindowStyle.Normal : ProcessWindowStyle.Hidden
            };

            using var p = Process.Start(psi)!;
            if (a.Visible) return $"Started '{action}'. A window shows its progress; it is not finished yet.";
            if (!p.WaitForExit(60_000)) return $"'{action}' is still running after a minute.";
            return p.ExitCode == 0 ? $"Done: {a.Description}" : $"'{action}' failed (exit code {p.ExitCode}).";
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return "The user declined the Windows administrator prompt, so nothing was changed.";
        }
        catch (Exception ex)
        {
            return $"Could not run '{action}': {ex.Message}";
        }
    }
}
