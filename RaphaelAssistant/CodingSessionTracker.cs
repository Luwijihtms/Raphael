using System.Text.RegularExpressions;

namespace Raphael;

/// <summary>Recognized code editors, and how to pull a project/folder name out of their window title.</summary>
public static class CodeEditors
{
    private static readonly HashSet<string> Processes = new(StringComparer.OrdinalIgnoreCase)
        { "Code", "devenv", "rider64", "idea64", "pycharm64", "clion64", "webstorm64", "goland64" };

    public static bool IsEditor(string process) => Processes.Contains(process);

    /// <summary>
    /// The project/folder name from a window title, or null if none is evident (e.g. no folder/solution open, just a
    /// bare file). Handles VS Code's "file - Project - Visual Studio Code" and Visual Studio's "... - Microsoft Visual Studio".
    /// </summary>
    public static string? ProjectFromTitle(string process, string title)
    {
        if (string.IsNullOrWhiteSpace(title)) return null;
        var parts = title.Split(" - ", StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return null;

        if (process.Equals("Code", StringComparison.OrdinalIgnoreCase))
        {
            if (parts.Length >= 3 && parts[^1].Equals("Visual Studio Code", StringComparison.OrdinalIgnoreCase))
                return parts[^2]; // "file.cs - Project - Visual Studio Code"
            if (parts.Length == 2 && parts[^1].Equals("Visual Studio Code", StringComparison.OrdinalIgnoreCase))
                return Regex.IsMatch(parts[0], @"\.\w{1,5}$") ? null : parts[0]; // a bare filename, not a folder: no project known
            return null;
        }

        if (process.Equals("devenv", StringComparison.OrdinalIgnoreCase))
        {
            var vsIndex = Array.FindIndex(parts, p => p.Contains("Visual Studio", StringComparison.OrdinalIgnoreCase));
            return vsIndex > 0 ? parts[vsIndex - 1] : null; // "File.cs - Solution - Microsoft Visual Studio"
        }

        // JetBrains IDEs: title format varies by version; the project name is usually the first segment.
        return parts[0].Length is > 0 and < 60 ? parts[0] : null;
    }
}

/// <summary>
/// Notices which project you're coding in (from the front window's title — never its contents) and how long you've
/// stayed in it without switching away, so she can answer "what am I working on" for check_git_status, and so the
/// proactive system can nudge a break after a long unbroken stretch. Resets on restart; nothing is saved to disk.
/// </summary>
public sealed class CodingSessionTracker : IDisposable
{
    private readonly Func<ForegroundInfo?> _read;
    private readonly TimeSpan _interval;
    private readonly CancellationTokenSource _cts = new();
    private readonly object _lock = new();
    private string? _project;
    private DateTimeOffset _since;

    public CodingSessionTracker(Func<ForegroundInfo?>? read = null, TimeSpan? interval = null)
    {
        _read = read ?? PresenceMonitor.ReadForeground;
        _interval = interval ?? TimeSpan.FromSeconds(20);
    }

    /// <summary>The project you're currently coding in, or null if you're not in a recognized editor with one open.</summary>
    public string? CurrentProject { get { lock (_lock) return _project; } }

    /// <summary>How long you've stayed in <see cref="CurrentProject"/> without switching to something else.</summary>
    public TimeSpan Elapsed { get { lock (_lock) return _project == null ? TimeSpan.Zero : DateTimeOffset.Now - _since; } }

    public void Start() => _ = Task.Run(LoopAsync);

    private async Task LoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            try { Sample(); } catch { /* try again next round */ }
            try { await Task.Delay(_interval, _cts.Token); } catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>One look at the foreground window. Public so it can be tested without waiting.</summary>
    public void Sample()
    {
        var info = _read();
        var project = info != null && CodeEditors.IsEditor(info.Process) ? CodeEditors.ProjectFromTitle(info.Process, info.Title) : null;

        lock (_lock)
        {
            if (project == null) { _project = null; return; } // stepped away from coding; the streak picks up fresh when it resumes
            if (!string.Equals(project, _project, StringComparison.OrdinalIgnoreCase)) { _project = project; _since = DateTimeOffset.Now; }
        }
    }

    public void Dispose() => _cts.Cancel();
}
