using System.Text;
using Windows.Media.Control;

namespace Raphael;

/// <summary>
/// Pauses, resumes and skips whatever is playing, through the same Windows media layer as the keyboard's
/// media keys and the volume flyout. Chrome, Edge and Spotify all report to it, so she can control a
/// YouTube video without touching the browser tab.
/// </summary>
public static class MediaControl
{
    private static readonly string[] BrowserIds = { "chrome", "msedge", "edge", "firefox", "brave", "opera", "vivaldi" };

    public static async Task<string> RunAsync(string action, string? target)
    {
        try
        {
            var manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            var all = manager.GetSessions().ToList();
            var normalizedTarget = (target ?? "any").Trim().ToLowerInvariant();

            if (action.Trim().ToLowerInvariant() == "now_playing")
                return await DescribeAsync(all);

            var matching = all.Where(s => Matches(s, normalizedTarget)).ToList();
            if (matching.Count == 0)
                return normalizedTarget == "any"
                    ? "No media is playing or paused right now."
                    : $"No {normalizedTarget} media session was found. Nothing from the {normalizedTarget} is currently playing.";

            return action.Trim().ToLowerInvariant() switch
            {
                "pause" => await PauseAsync(matching),
                "resume" => await ResumeAsync(matching),
                "toggle" => await OneAsync(matching, s => s.TryTogglePlayPauseAsync().AsTask(), "Toggled play and pause"),
                "next" => await OneAsync(matching, s => s.TrySkipNextAsync().AsTask(), "Skipped to the next item"),
                "previous" => await OneAsync(matching, s => s.TrySkipPreviousAsync().AsTask(), "Went back to the previous item"),
                _ => $"Unknown media action '{action}'."
            };
        }
        catch (Exception ex)
        {
            return $"Media control failed: {ex.Message}";
        }
    }

    /// <summary>
    /// After a video has been opened in the browser, waits for it to show up as a media session and presses play
    /// if the browser held it paused (Chrome often blocks autoplay). It only acts on the session whose title
    /// matches the video that was just opened, so an older paused video is never resumed by mistake.
    /// </summary>
    /// <returns>True once that video is playing.</returns>
    public static async Task<bool> StartBrowserPlaybackAsync(string expectedTitle, TimeSpan wait)
    {
        var start = expectedTitle.Length > 20 ? expectedTitle[..20] : expectedTitle;
        var deadline = DateTime.UtcNow + wait;

        try
        {
            var manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            while (DateTime.UtcNow < deadline)
            {
                foreach (var session in manager.GetSessions().Where(s => Matches(s, "browser")))
                {
                    var props = await session.TryGetMediaPropertiesAsync();
                    if (!(props.Title ?? "").Contains(start, StringComparison.OrdinalIgnoreCase)) continue;

                    var status = session.GetPlaybackInfo().PlaybackStatus;
                    if (status == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing) return true;
                    if (status == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused && await session.TryPlayAsync()) return true;
                }
                await Task.Delay(700);
            }
        }
        catch
        {
            // Best effort: the video is already open, so just report that it may need a click.
        }
        return false;
    }

    private static bool Matches(GlobalSystemMediaTransportControlsSession session, string target)
    {
        var id = session.SourceAppUserModelId ?? "";
        return target switch
        {
            "browser" => BrowserIds.Any(b => id.Contains(b, StringComparison.OrdinalIgnoreCase)),
            "spotify" => id.Contains("spotify", StringComparison.OrdinalIgnoreCase),
            _ => true
        };
    }

    private static bool IsPlaying(GlobalSystemMediaTransportControlsSession s) =>
        s.GetPlaybackInfo().PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;

    private static async Task<string> PauseAsync(List<GlobalSystemMediaTransportControlsSession> sessions)
    {
        var playing = sessions.Where(IsPlaying).ToList();
        if (playing.Count == 0) return "Nothing that matches is playing, so there was nothing to pause.";

        var paused = new List<string>();
        foreach (var s in playing)
        {
            if (await s.TryPauseAsync()) paused.Add(await LabelAsync(s));
        }
        return paused.Count > 0 ? $"Paused {string.Join(" and ", paused)}." : "Windows refused the pause request.";
    }

    private static async Task<string> ResumeAsync(List<GlobalSystemMediaTransportControlsSession> sessions)
    {
        var target = sessions.FirstOrDefault(s => s.GetPlaybackInfo().PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused);
        if (target == null) return "Nothing that matches is paused, so there was nothing to resume.";

        return await target.TryPlayAsync() ? $"Resumed {await LabelAsync(target)}." : "Windows refused the resume request.";
    }

    /// <summary>Applies an action to the session that is playing, or else the first match.</summary>
    private static async Task<string> OneAsync(List<GlobalSystemMediaTransportControlsSession> sessions,
        Func<GlobalSystemMediaTransportControlsSession, Task<bool>> action, string done)
    {
        var session = sessions.FirstOrDefault(IsPlaying) ?? sessions[0];
        return await action(session) ? $"{done} on {await LabelAsync(session)}." : "Windows refused the request; that player may not support it.";
    }

    private static async Task<string> DescribeAsync(List<GlobalSystemMediaTransportControlsSession> sessions)
    {
        if (sessions.Count == 0) return "Nothing is playing or paused right now.";

        var lines = new StringBuilder();
        foreach (var s in sessions)
        {
            var status = s.GetPlaybackInfo().PlaybackStatus.ToString().ToLowerInvariant();
            lines.AppendLine($"{status}: {await LabelAsync(s)}");
        }
        return lines.ToString().Trim();
    }

    /// <summary>"'Title' by Artist (Chrome)" for a session.</summary>
    private static async Task<string> LabelAsync(GlobalSystemMediaTransportControlsSession s)
    {
        try
        {
            var props = await s.TryGetMediaPropertiesAsync();
            var app = (s.SourceAppUserModelId ?? "unknown app").Replace(".exe", "", StringComparison.OrdinalIgnoreCase);
            var title = string.IsNullOrWhiteSpace(props.Title) ? "an untitled item" : $"'{props.Title}'";
            var by = string.IsNullOrWhiteSpace(props.Artist) ? "" : $" by {props.Artist}";
            return $"{title}{by} ({app})";
        }
        catch
        {
            return s.SourceAppUserModelId ?? "a player";
        }
    }
}
