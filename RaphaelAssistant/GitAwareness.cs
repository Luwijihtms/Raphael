using System.Diagnostics;
using System.Text.RegularExpressions;

namespace Raphael;

/// <summary>One repo's status: nothing here ever comes from committing, pushing or otherwise changing anything.</summary>
public sealed record GitStatus(string RepoName, string Branch, bool HasUpstream, int Ahead, int Behind,
    int Modified, int Added, int Deleted, int Untracked, string? LastCommit);

/// <summary>
/// Finds one of your local coding projects by name (or from whichever one CodingSessionTracker says you're in) and
/// reads its git status — read-only, through plain `git status`/`git log`. Never commits, pushes, stages or changes
/// anything, and works whether or not the repo has ever been pushed to a remote. Projects are found under
/// RAPHAEL_CODE_ROOTS (semicolon-separated folders; defaults to C:\coding).
/// </summary>
public static class GitAwareness
{
    private static IEnumerable<string> Roots =>
        (Environment.GetEnvironmentVariable("RAPHAEL_CODE_ROOTS") ?? @"C:\coding")
        .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>The repo folder matching this name (an exact folder-name match wins over a partial one), or null.</summary>
    public static string? FindRepo(string name)
    {
        name = name.Trim();
        string? partial = null;
        foreach (var root in Roots)
        {
            foreach (var dir in SafeSubdirs(root))
            {
                if (!Directory.Exists(Path.Combine(dir, ".git"))) continue;
                var folder = Path.GetFileName(dir);
                if (string.Equals(folder, name, StringComparison.OrdinalIgnoreCase)) return dir;
                if (partial == null && (folder.Contains(name, StringComparison.OrdinalIgnoreCase) || name.Contains(folder, StringComparison.OrdinalIgnoreCase)))
                    partial = dir;
            }
        }
        return partial;
    }

    /// <summary>Every known local repo's folder name, so a "not found" message can suggest what she does know about.</summary>
    public static List<string> ListKnown() =>
        Roots.SelectMany(SafeSubdirs).Where(d => Directory.Exists(Path.Combine(d, ".git"))).Select(Path.GetFileName)
            .Where(n => n != null).Select(n => n!).ToList();

    private static IEnumerable<string> SafeSubdirs(string root)
    {
        if (!Directory.Exists(root)) return Enumerable.Empty<string>();
        try { return Directory.EnumerateDirectories(root); } catch { return Enumerable.Empty<string>(); }
    }

    public static GitStatus? Status(string repoPath)
    {
        var raw = RunGit(repoPath, "status", "--porcelain=v2", "--branch");
        if (raw == null) return null;

        string branch = "?";
        bool hasUpstream = false;
        int ahead = 0, behind = 0, modified = 0, added = 0, deleted = 0, untracked = 0;

        foreach (var line in raw.Split('\n').Select(l => l.TrimEnd('\r')))
        {
            if (line.StartsWith("# branch.head ")) branch = line["# branch.head ".Length..];
            else if (line.StartsWith("# branch.ab "))
            {
                hasUpstream = true;
                var m = Regex.Match(line, @"\+(\d+) -(\d+)");
                if (m.Success) { ahead = int.Parse(m.Groups[1].Value); behind = int.Parse(m.Groups[2].Value); }
            }
            else if (line.StartsWith("1 ") || line.StartsWith("2 ")) // an ordinary or renamed tracked change
            {
                var xy = line.Length > 3 ? line.Substring(2, 2) : "";
                if (xy.Contains('A')) added++;
                else if (xy.Contains('D')) deleted++;
                else if (xy.Trim().Length > 0) modified++;
            }
            else if (line.StartsWith("? ")) untracked++;
        }

        var lastCommit = RunGit(repoPath, "log", "-1", "--format=%cr: %s")?.Trim();
        return new GitStatus(Path.GetFileName(repoPath.TrimEnd('\\', '/')), branch, hasUpstream, ahead, behind,
            modified, added, deleted, untracked, string.IsNullOrEmpty(lastCommit) ? null : lastCommit);
    }

    /// <summary>One plain-English line, for the check_git_status tool.</summary>
    public static string Describe(GitStatus s)
    {
        var changes = new List<string>();
        if (s.Modified > 0) changes.Add($"{s.Modified} modified");
        if (s.Added > 0) changes.Add($"{s.Added} added");
        if (s.Deleted > 0) changes.Add($"{s.Deleted} deleted");
        if (s.Untracked > 0) changes.Add($"{s.Untracked} untracked");
        var changeText = changes.Count == 0 ? "clean, nothing uncommitted" : string.Join(", ", changes);

        var remoteText = !s.HasUpstream ? "no remote tracking branch set"
            : s.Ahead == 0 && s.Behind == 0 ? "up to date with its remote"
            : string.Join(" and ", new[] { s.Ahead > 0 ? $"{s.Ahead} commit(s) ahead" : null, s.Behind > 0 ? $"{s.Behind} commit(s) behind" : null }
                .Where(x => x != null));

        var commitText = s.LastCommit != null ? $" Last commit {s.LastCommit}." : " No commits yet.";
        return $"{s.RepoName} (branch {s.Branch}): {changeText}; {remoteText}.{commitText}";
    }

    private static string? _gitExe;

    /// <summary>
    /// Where git.exe actually is. Git for Windows is often not on the system PATH even when installed (a bare "git"
    /// then fails to launch at all), so this also tries the well-known install locations, and RAPHAEL_GIT_PATH for
    /// anywhere else. Resolved once and cached.
    /// </summary>
    private static string ResolveGitExe()
    {
        if (_gitExe != null) return _gitExe;

        var candidates = new List<string?>
        {
            Environment.GetEnvironmentVariable("RAPHAEL_GIT_PATH"),
            "git", // in case it is on PATH after all
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Git", "cmd", "git.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Git", "bin", "git.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Git", "cmd", "git.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Git", "cmd", "git.exe"),
        };

        foreach (var candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate)) continue;
            if (candidate != "git" && !File.Exists(candidate)) continue;
            try
            {
                using var probe = Process.Start(new ProcessStartInfo(candidate, "--version")
                    { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true });
                probe?.WaitForExit(3000);
                if (probe is { ExitCode: 0 }) return _gitExe = candidate;
            }
            catch { /* try the next candidate */ }
        }
        return _gitExe = "git"; // nothing worked; the caller reports "could not read" either way
    }

    /// <summary>Each element is one argument, passed through untouched (no manual quoting/splitting, unlike a single
    /// space-joined string — safer for a format like "--format=%cr: %s" that itself contains a space).</summary>
    private static string? RunGit(string repoPath, params string[] arguments)
    {
        try
        {
            var psi = new ProcessStartInfo(ResolveGitExe())
            {
                WorkingDirectory = repoPath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            foreach (var arg in arguments) psi.ArgumentList.Add(arg);
            using var process = Process.Start(psi);
            if (process == null) return null;
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(5000);
            return process.ExitCode == 0 ? output : null;
        }
        catch { return null; } // git not found anywhere, or something else read-only-safe to just report as "couldn't read"
    }
}
