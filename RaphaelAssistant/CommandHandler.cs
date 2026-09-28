using System.Diagnostics;
using System.Text.Json.Nodes;

namespace Raphael;

/// <summary>
/// Executes tool calls requested by Raphael and returns a short result string
/// to feed back to the model (and optionally speak).
/// </summary>
public static class CommandHandler
{
    private static readonly SpotifyClient Spotify = new();
    private static readonly YouTubeClient YouTube = new();
    internal static readonly WeatherClient Weather = new();

    /// <summary>Set once at start-up so the view_screen tool can send a screenshot to Gemini's vision.</summary>
    internal static GeminiClient? Llm;

    /// <param name="allowShell">False for requests from the phone: run_command is refused even if the model asks for it.</param>
    public static string Execute(ToolCall call, bool allowShell = true)
    {
        try
        {
            return call.Name switch
            {
                "remember" => MemoryStore.Add(call.Input?["fact"]?.GetValue<string>() ?? ""),
                "forget" => MemoryStore.Remove(call.Input?["keyword"]?.GetValue<string>() ?? ""),
                "write_note" => WriteNote(
                    call.Input?["content"]?.GetValue<string>() ?? "",
                    call.Input?["filename"]?.GetValue<string>()),
                "check_mail" => Program.Mail?.DescribeAsync().GetAwaiter().GetResult()
                                ?? "Gmail is not connected. It needs GMAIL_CLIENT_ID and GMAIL_CLIENT_SECRET, then a one-time Google login.",
                "check_calendar" => Program.Calendar?.DescribeAsync(call.Input?["day"]?.GetValue<string>()).GetAwaiter().GetResult()
                                    ?? "The calendar is not connected. It needs the Google login (see the README).",
                "add_calendar_event" => AddCalendarEvent(
                    call.Input?["title"]?.GetValue<string>() ?? "",
                    call.Input?["start"]?.GetValue<string>() ?? "",
                    call.Input?["duration_minutes"] is JsonNode dm ? dm.GetValue<int>() : 60,
                    call.Input?["description"]?.GetValue<string>()),
                "habits" => string.Equals(call.Input?["action"]?.GetValue<string>(), "clear", StringComparison.OrdinalIgnoreCase)
                    ? HabitLog.Clear() : HabitLog.Describe(DateTimeOffset.Now),
                "learned_notes" => string.Equals(call.Input?["action"]?.GetValue<string>(), "clear", StringComparison.OrdinalIgnoreCase)
                    ? PersonalityStore.Clear() : PersonalityStore.Describe(),
                "recap" => ActivityLog.Summarize(call.Input?["period"]?.GetValue<string>() ?? "today", DateTimeOffset.Now),
                "check_git_status" => CheckGitStatus(call.Input?["project"]?.GetValue<string>()),
                "check_weather" => Weather.DescribeAsync(call.Input?["day"]?.GetValue<string>()).GetAwaiter().GetResult(),
                "web_lookup" => WebLookup.LookupAsync(
                    call.Input?["query"]?.GetValue<string>() ?? "",
                    call.Input?["type"]?.GetValue<string>()).GetAwaiter().GetResult(),
                "wolfram_compute" => WolframClient.ComputeAsync(call.Input?["query"]?.GetValue<string>() ?? "").GetAwaiter().GetResult(),
                "read_document" => DocumentReader.Read(
                    call.Input?["filename"]?.GetValue<string>() ?? "",
                    call.Input?["question"]?.GetValue<string>()),
                "security_status" => Program.Scanner.StatusAsync().GetAwaiter().GetResult(),
                "start_scan" => Program.Scanner.Start(
                    call.Input?["type"]?.GetValue<string>() ?? "quick",
                    call.Input?["path"]?.GetValue<string>(),
                    fromPhone: !allowShell),
                "wordle_answer" => WordleLookup.AnswerAsync(call.Input?["date"]?.GetValue<string>()).GetAwaiter().GetResult(),
                "set_timer" => Program.Timers.Set(
                    (int)Math.Round(call.Input?["seconds"]?.GetValue<double>() ?? 0),
                    call.Input?["label"]?.GetValue<string>(),
                    fromPhone: !allowShell), // shell commands are only ever refused for requests that came from the phone
                "list_timers" => Program.Timers.List(),
                "cancel_timer" => Program.Timers.Cancel(call.Input?["label"]?.GetValue<string>()),
                "power_off" => PowerOff(),
                "check_notifications" => NotificationWatcher.DescribeAsync().GetAwaiter().GetResult(),
                "media_control" => MediaControl.RunAsync(
                    call.Input?["action"]?.GetValue<string>() ?? "",
                    call.Input?["target"]?.GetValue<string>()).GetAwaiter().GetResult(),
                "play_youtube" => YouTube.PlayAsync(
                    call.Input?["query"]?.GetValue<string>() ?? "",
                    call.Input?["random"] is JsonNode r && r.GetValue<bool>()).GetAwaiter().GetResult(),
                "system_volume" => SystemVolumeControl(
                    call.Input?["action"]?.GetValue<string>() ?? "",
                    call.Input?["value"] is JsonNode sv ? sv.GetValue<int>() : null),
                "spotify_control" => Spotify.ControlAsync(
                    call.Input?["action"]?.GetValue<string>() ?? "",
                    call.Input?["value"] is JsonNode v ? v.GetValue<int>() : null).GetAwaiter().GetResult(),
                "play_playlist" => Spotify.PlayPlaylistAsync(call.Input?["name"]?.GetValue<string>() ?? "").GetAwaiter().GetResult(),
                "list_playlists" => Spotify.ListPlaylistsAsync().GetAwaiter().GetResult(),
                "play_song" => Spotify.PlayAsync(call.Input?["query"]?.GetValue<string>() ?? "").GetAwaiter().GetResult(),
                "open_app" => OpenApp(call.Input?["app"]?.GetValue<string>() ?? ""),
                "close_app" => CloseApp(call.Input?["app"]?.GetValue<string>() ?? ""),
                "view_screen" => ViewScreen(call.Input?["question"]?.GetValue<string>()),
                "open_url" => OpenUrl(call.Input?["url"]?.GetValue<string>() ?? ""),
                "run_command" when !allowShell => "Shell commands are disabled for requests from the phone.",
                "run_command" => RunCommand(call.Input?["command"]?.GetValue<string>() ?? ""),
                "write_code" => CodeSandbox.Write(
                    call.Input?["language"]?.GetValue<string>() ?? "",
                    call.Input?["filename"]?.GetValue<string>() ?? "",
                    call.Input?["code"]?.GetValue<string>() ?? ""),
                "run_code" when !allowShell => "Running code is disabled for requests from the phone.",
                "run_code" => CodeSandbox.Run(call.Input?["path"]?.GetValue<string>() ?? "", call.Input?["stdin"]?.GetValue<string>()),
                "delete_file" => DeleteManagedFile(call.Input?["path"]?.GetValue<string>() ?? ""),
                "get_time" => $"Current time: {DateTime.Now:HH:mm}, {DateTime.Now:dddd, MMMM d, yyyy}.",
                _ => $"Unknown tool: {call.Name}"
            };
        }
        catch (Exception ex)
        {
            return $"Execution failed: {ex.Message}";
        }
    }

    /// <summary>
    /// Saves text as a new .txt file under Documents\Raphael Notes and opens it in Notepad.
    /// Never overwrites: if the name is taken, a timestamp is added.
    /// </summary>
    private static string WriteNote(string content, string? filename)
    {
        if (string.IsNullOrWhiteSpace(content)) return "No text to write.";

        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Raphael Notes");
        Directory.CreateDirectory(folder);

        // Keep only a safe file name (no folders, no invalid characters).
        var name = Path.GetFileNameWithoutExtension(filename ?? "");
        name = string.Concat(name.Where(c => !Path.GetInvalidFileNameChars().Contains(c))).Trim();
        if (name.Length == 0) name = $"note-{DateTime.Now:yyyyMMdd-HHmmss}";

        var path = Path.Combine(folder, name + ".txt");
        if (File.Exists(path))
            path = Path.Combine(folder, $"{name}-{DateTime.Now:yyyyMMdd-HHmmss}.txt");

        File.WriteAllText(path, content, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        Process.Start(new ProcessStartInfo("notepad.exe", $"\"{path}\"") { UseShellExecute = true });
        return $"Saved the note as {Path.GetFileName(path)} in Documents\\Raphael Notes and opened it in Notepad.";
    }

    /// <summary>Read-only git status for a named project, or whatever the user is currently coding in.</summary>
    private static string CheckGitStatus(string? project)
    {
        var name = string.IsNullOrWhiteSpace(project) ? Program.Coding?.CurrentProject : project.Trim();
        if (string.IsNullOrWhiteSpace(name))
            return "No project was named, and the user doesn't appear to be coding in a recognized editor right now. Ask which project.";

        var repo = GitAwareness.FindRepo(name);
        if (repo == null)
        {
            var known = GitAwareness.ListKnown();
            return known.Count == 0
                ? $"No local git repository named '{name}' was found under the configured project folder(s)."
                : $"No local git repository named '{name}' was found. Known repositories: {string.Join(", ", known)}.";
        }

        var status = GitAwareness.Status(repo);
        return status == null ? $"Could not read git status for '{name}' (is git installed and on PATH?)." : GitAwareness.Describe(status);
    }

    /// <summary>The PC's own master volume and mute (the real speaker level), not Spotify's own volume.</summary>
    private static string SystemVolumeControl(string action, int? value)
    {
        action = action.Trim().ToLowerInvariant();
        try
        {
            switch (action)
            {
                case "status":
                    var (percent, muted) = SystemVolume.Get();
                    return $"System volume is at {percent}%, {(muted ? "muted" : "not muted")}.";

                case "mute":
                    SystemVolume.SetMute(true);
                    return "System volume muted.";

                case "unmute":
                    SystemVolume.SetMute(false);
                    return "System volume unmuted.";

                case "set":
                    if (value == null) return "No volume level was given.";
                    SystemVolume.SetVolume(value.Value);
                    return $"System volume set to {Math.Clamp(value.Value, 0, 100)}%.";

                case "up":
                case "down":
                    var (current, _) = SystemVolume.Get();
                    var step = value ?? 10;
                    var next = Math.Clamp(action == "up" ? current + step : current - step, 0, 100);
                    SystemVolume.SetVolume(next);
                    return $"System volume set to {next}%.";

                default:
                    return $"Unknown system volume action '{action}'.";
            }
        }
        catch (Exception ex)
        {
            return $"Could not change the system volume: {ex.Message}";
        }
    }

    /// <summary>Adds an event to the user's Google Calendar. Never touches an event that already exists.</summary>
    private static string AddCalendarEvent(string title, string start, int durationMinutes, string? description)
    {
        if (Program.CalendarWrite == null)
            return "The calendar is not connected. It needs the Google login (see the Google Calendar section of the README).";
        if (string.IsNullOrWhiteSpace(title)) return "No event title was given.";
        if (!DateTime.TryParse(start, null, System.Globalization.DateTimeStyles.None, out var parsed))
            return $"Could not understand the start time '{start}'. Use a full local date and time, e.g. 2026-09-25T15:00:00.";
        if (durationMinutes <= 0) durationMinutes = 60;

        var when = new DateTimeOffset(DateTime.SpecifyKind(parsed, DateTimeKind.Local));
        var created = Program.CalendarWrite.CreateEventAsync(title, when, TimeSpan.FromMinutes(durationMinutes), description).GetAwaiter().GetResult();
        return $"Added '{created.Title}' to the calendar for {created.Start:dddd, MMMM d} at {created.Start:h:mm tt}, {durationMinutes} minutes.";
    }

    // The only two folders a delete request is ever allowed to touch — both are places only Raphael writes to.
    private static readonly string[] DeletableFolders =
    {
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Raphael Code"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Raphael Notes"),
    };

    /// <summary>
    /// Deletes one file she wrote (a generated program or a note) — never anything else on the PC. The path must resolve
    /// inside Documents\Raphael Code or Documents\Raphael Notes; anything outside those, including a relative path that
    /// tries to climb out with "..", is refused.
    /// </summary>
    private static string DeleteManagedFile(string path)
    {
        path = (path ?? "").Trim().Trim('"');
        if (path.Length == 0) return "No file was named.";

        string full;
        try { full = Path.GetFullPath(path); }
        catch (Exception ex) { return $"Not a valid path: {ex.Message}"; }

        var allowed = DeletableFolders.Any(folder => full.StartsWith(Path.GetFullPath(folder) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
        if (!allowed)
            return "Refused: this only deletes files it wrote itself, in Documents\\Raphael Code or Documents\\Raphael Notes. That path is outside both.";

        if (!File.Exists(full)) return $"'{Path.GetFileName(full)}' doesn't exist (already deleted, or the name doesn't match).";

        try { File.Delete(full); return $"Deleted {Path.GetFileName(full)}."; }
        catch (Exception ex) { return $"Could not delete it: {ex.Message}"; }
    }

    /// <summary>Closes Raphael herself once her goodbye has been spoken. It never turns off the PC.</summary>
    private static string PowerOff()
    {
        Program.RequestShutdown();
        return "Raphael will close after she has spoken her farewell. The computer itself is not being shut down.";
    }

    internal static string OpenApp(string app)
    {
        if (string.IsNullOrWhiteSpace(app)) return "No application specified.";

        // Fast path: anything Windows can resolve by name (notepad, calc, chrome via App Paths, ...).
        try
        {
            Process.Start(new ProcessStartInfo(app) { UseShellExecute = true });
            return $"Launched {app}.";
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // Not resolvable by name; fall through to the Start menu.
        }

        var match = FindStartMenuApp(app);
        if (match == null) return $"No installed application matching '{app}' was found.";

        // AppID is either a path to an exe (classic apps) or an AUMID (Store apps).
        if (File.Exists(match.Value.AppId))
            Process.Start(new ProcessStartInfo(match.Value.AppId)
            {
                UseShellExecute = true,
                // Launchers and games often expect to start from their own folder.
                WorkingDirectory = Path.GetDirectoryName(match.Value.AppId)!
            });
        else
            Process.Start(new ProcessStartInfo("explorer.exe", $"shell:AppsFolder\\{match.Value.AppId}") { UseShellExecute = true });

        return $"Launched {match.Value.Name}.";
    }

    // Never closable, even if a name happened to match: core Windows processes, and Raphael herself (power_off is for that).
    private static readonly HashSet<string> ProtectedProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer", "dwm", "winlogon", "csrss", "wininit", "services", "lsass", "smss", "svchost", "system", "registry",
        "idle", "spoolsv", "taskhostw", "sihost", "searchhost", "startmenuexperiencehost", "shellexperiencehost",
        "textinputhost", "runtimebroker", "ctfmon", "fontdrvhost", "raphael"
    };

    /// <summary>
    /// Closes a running app by matching its window title (what you actually call it), not just its process name — a
    /// game's process can be named anything ("Client-Win64-Shipping.exe" for Wuthering Waves). Tries a graceful close
    /// first, and only forces it if the app doesn't respond. Only apps with a visible window can be closed this way,
    /// and a short list of core Windows processes is refused outright.
    /// </summary>
    /// <summary>
    /// Takes a screenshot, shrinks it (kinder to the per-minute token limit and much faster; the daily request count
    /// is the same either way), and asks Gemini's vision what is on it. One extra request; never called unprompted.
    /// </summary>
    private static string ViewScreen(string? question)
    {
        if (Llm == null) return "Cannot see the screen right now: the model connection isn't available.";

        byte[] jpeg;
        try { jpeg = CaptureScreenJpeg(); }
        catch (Exception ex) { return $"Could not take a screenshot: {ex.Message}"; }

        var hasQuestion = !string.IsNullOrWhiteSpace(question);
        var prompt = hasQuestion
            ? $"Looking at this Windows screen, answer plainly: {question!.Trim()} If it's an error message or stack trace, "
              + "explain in plain terms what actually went wrong and, if it's evident from what's visible, one likely cause — "
              + "don't just transcribe the text back."
            : "Briefly say what is on this Windows screen: the app or apps in the foreground, and the title of the main window if one is visible. One or two short, plain sentences, no formatting.";

        // A specific question (e.g. explaining an error) needs more room to answer than a one-line "what's on screen" summary.
        var answer = Llm.SideQuestionWithImageAsync(prompt, jpeg, hasQuestion ? 700 : 300).GetAwaiter().GetResult();
        return answer ?? "Could not read the screen right now (the model did not answer).";
    }

    /// <summary>The whole (all-monitor) screen, downscaled and compressed — plenty to identify a window or read on-screen
    /// text (including small error/console text), at a fraction of a full screenshot's size.</summary>
    private static byte[] CaptureScreenJpeg(int maxDimension = 1600, long quality = 75)
    {
        var bounds = System.Windows.Forms.SystemInformation.VirtualScreen;
        using var full = new System.Drawing.Bitmap(bounds.Width, bounds.Height);
        using (var g = System.Drawing.Graphics.FromImage(full))
            g.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, bounds.Size);

        var scale = Math.Min(1.0, (double)maxDimension / Math.Max(full.Width, full.Height));
        using var scaled = new System.Drawing.Bitmap((int)(full.Width * scale), (int)(full.Height * scale));
        using (var g = System.Drawing.Graphics.FromImage(scaled))
        {
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBilinear;
            g.DrawImage(full, 0, 0, scaled.Width, scaled.Height);
        }

        var jpegCodec = System.Drawing.Imaging.ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == System.Drawing.Imaging.ImageFormat.Jpeg.Guid);
        using var parameters = new System.Drawing.Imaging.EncoderParameters(1);
        parameters.Param[0] = new System.Drawing.Imaging.EncoderParameter(System.Drawing.Imaging.Encoder.Quality, quality);

        using var ms = new MemoryStream();
        scaled.Save(ms, jpegCodec, parameters);
        return ms.ToArray();
    }

    internal static string CloseApp(string app)
    {
        if (string.IsNullOrWhiteSpace(app)) return "No application specified.";
        app = app.Trim();

        if (ProtectedProcesses.Contains(app) || ProtectedProcesses.Any(p => app.Equals(p, StringComparison.OrdinalIgnoreCase)))
            return $"'{app}' is a core Windows process and won't be closed this way. Use Task Manager if you really need to.";

        var candidates = Process.GetProcesses()
            .Where(p => p.MainWindowHandle != IntPtr.Zero && !ProtectedProcesses.Contains(p.ProcessName))
            .Select(p => (Process: p, Title: SafeTitle(p)))
            .Where(x => x.Title.Length > 0)
            .ToList();

        // Best match first: the window title or process name containing the whole request, then the other way round.
        var match = candidates.FirstOrDefault(x => x.Title.Contains(app, StringComparison.OrdinalIgnoreCase)
                                                  || x.Process.ProcessName.Contains(app, StringComparison.OrdinalIgnoreCase))
                    .Process ?? candidates.FirstOrDefault(x => app.Contains(x.Title, StringComparison.OrdinalIgnoreCase)).Process;

        if (match == null)
            return $"No open, visible application matching '{app}' was found. Only apps with an open window can be closed this way.";

        var title = SafeTitle(match).Length > 0 ? SafeTitle(match) : match.ProcessName;
        try
        {
            match.CloseMainWindow(); // gives the app a chance to prompt to save, ask to confirm, etc.
            if (match.WaitForExit(3000)) return $"Closed {title}.";

            match.Kill(entireProcessTree: true);
            match.WaitForExit(2000);
            return $"{title} did not close on its own, so it was ended forcibly. Any unsaved work in it may be lost.";
        }
        catch (Exception ex)
        {
            return $"Could not close {title}: {ex.Message}";
        }
    }

    private static string SafeTitle(Process p)
    {
        try { return p.MainWindowTitle ?? ""; } catch { return ""; }
    }

    private static List<(string Name, string AppId)>? _startApps;

    /// <summary>Best match for what the user said among the apps in the Start menu, or null.</summary>
    private static (string Name, string AppId)? FindStartMenuApp(string query)
    {
        _startApps ??= LoadStartApps();
        var q = query.Trim().ToLowerInvariant();

        var exact = _startApps.FirstOrDefault(a => a.Name.ToLowerInvariant() == q);
        if (exact.Name != null) return exact;

        // Prefer the shortest name that contains the query ("steam" -> "Steam", not "Steam Support Center"),
        // then names contained in the query ("open google chrome browser").
        var contains = _startApps.Where(a => a.Name.ToLowerInvariant().Contains(q)).OrderBy(a => a.Name.Length).ToList();
        if (contains.Count > 0) return contains[0];

        var inside = _startApps.Where(a => q.Contains(a.Name.ToLowerInvariant())).OrderByDescending(a => a.Name.Length).ToList();
        if (inside.Count > 0) return inside[0];

        return null;
    }

    private static List<(string Name, string AppId)> LoadStartApps()
    {
        var list = new List<(string, string)>();
        var powershell = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        var psi = new ProcessStartInfo(powershell,
            "-NoProfile -Command \"[Console]::OutputEncoding=[Text.Encoding]::UTF8; Get-StartApps | ConvertTo-Json -Compress\"")
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8
        };

        using var proc = Process.Start(psi)!;
        var json = proc.StandardOutput.ReadToEnd();
        proc.WaitForExit(10000);

        if (string.IsNullOrWhiteSpace(json)) return list;

        var node = JsonNode.Parse(json);
        // ConvertTo-Json emits a bare object (not an array) when there is only one result.
        var items = node is JsonArray arr ? arr : new JsonArray(node!.DeepClone());
        foreach (var item in items)
        {
            var name = item?["Name"]?.GetValue<string>();
            var id = item?["AppID"]?.GetValue<string>();
            if (!string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(id)) list.Add((name, id));
        }
        return list;
    }

    private static string OpenUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return "No URL specified.";
        if (!url.StartsWith("http")) url = "https://" + url;
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        return $"Opened {url}.";
    }

    private static string RunCommand(string command)
    {
        if (string.IsNullOrWhiteSpace(command)) return "No command specified.";

        var psi = new ProcessStartInfo("cmd.exe", $"/c {command}")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        // Make sure "powershell" resolves even when it is missing from this process's PATH.
        var psDir = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0");
        psi.Environment["PATH"] = $"{psDir};{Environment.GetEnvironmentVariable("PATH")}";

        using var proc = Process.Start(psi)!;
        var output = proc.StandardOutput.ReadToEnd();
        var error = proc.StandardError.ReadToEnd();
        proc.WaitForExit(5000);

        var result = string.IsNullOrWhiteSpace(output) ? error : output;
        return string.IsNullOrWhiteSpace(result) ? "Command executed. No output." : result.Trim();
    }
}

