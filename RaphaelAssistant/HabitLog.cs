using System.Globalization;
using System.Text.Json.Nodes;

namespace Raphael;

/// <summary>A thing she can offer to do: a tool and its arguments, plus how to describe it out loud.</summary>
public sealed record Offer(string Tool, JsonObject? Args, string Description);

/// <summary>Something you do around the same time on most days.</summary>
public sealed record Habit(string Tool, string Detail, int MedianMinute, int Days);

/// <summary>
/// Notices routines: what you ask her to do, and when. Only the kind of request and its target are kept (which app,
/// which playlist), never message text, links or notes. It lives in %LOCALAPPDATA%\Raphael\habits.jsonl on this PC, and
/// "forget my habits" clears it. The patterns are found with plain arithmetic, without any Gemini call.
/// </summary>
public static class HabitLog
{
    // Harmless things she may offer to do at the usual time.
    private static readonly HashSet<string> Tracked = new()
        { "open_app", "play_playlist", "play_song", "wordle_answer", "check_mail", "check_calendar" };

    private const int WindowDays = 21;
    private const int MinDays = 4;

    public static string? PathOverride; // for tests
    private static string StorePath => PathOverride ?? System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Raphael", "habits.jsonl");
    private static readonly object FileLock = new();

    public static void Record(string tool, JsonNode? input, DateTimeOffset? at = null)
    {
        if (!Tracked.Contains(tool)) return;
        var detail = tool switch
        {
            "open_app" => input?["app"]?.GetValue<string>(),
            "play_playlist" => input?["name"]?.GetValue<string>(),
            "play_song" => input?["query"]?.GetValue<string>(),
            _ => ""
        };
        if (detail == null) return;
        detail = detail.Trim();
        if (detail.Length > 60) detail = detail[..60];

        var line = new JsonObject { ["t"] = (at ?? DateTimeOffset.Now).ToUnixTimeSeconds(), ["k"] = tool, ["d"] = detail }.ToJsonString();
        try
        {
            lock (FileLock)
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(StorePath)!);
                File.AppendAllText(StorePath, line + "\n");
            }
        }
        catch { /* a missed note is harmless */ }
    }

    private static List<(DateTimeOffset At, string Tool, string Detail)> Load(DateTimeOffset now)
    {
        var list = new List<(DateTimeOffset, string, string)>();
        try
        {
            lock (FileLock)
            {
                if (!File.Exists(StorePath)) return list;
                foreach (var line in File.ReadAllLines(StorePath))
                {
                    try
                    {
                        var j = JsonNode.Parse(line)!;
                        var at = DateTimeOffset.FromUnixTimeSeconds(j["t"]!.GetValue<long>());
                        if (now - at <= TimeSpan.FromDays(WindowDays + 1))
                            list.Add((at, j["k"]!.GetValue<string>(), j["d"]?.GetValue<string>() ?? ""));
                    }
                    catch { }
                }
            }
        }
        catch { }
        return list;
    }

    /// <summary>Routines: the same request on at least four different days in three weeks, mostly within an hour of the same time.</summary>
    public static IReadOnlyList<Habit> Detect(DateTimeOffset now)
    {
        var habits = new List<Habit>();
        var groups = Load(now).GroupBy(r => (r.Tool, Detail: r.Detail.ToLowerInvariant()));

        foreach (var g in groups)
        {
            // The first time on each day counts (asking three times in a row is not three habits).
            var perDay = g.GroupBy(r => r.At.LocalDateTime.Date)
                .Select(d => d.Min(r => r.At.LocalDateTime.TimeOfDay.TotalMinutes)).OrderBy(m => m).ToList();
            if (perDay.Count < MinDays) continue;

            var median = (int)Math.Round(perDay[perDay.Count / 2]);
            var close = perDay.Count(m => Math.Abs(m - median) <= 60);
            if (close < Math.Ceiling(perDay.Count * 0.6)) continue;

            habits.Add(new Habit(g.Key.Tool, g.First().Detail, median, perDay.Count));
        }
        return habits;
    }

    /// <summary>Habits that are due now: the usual time has come (or was a little while ago) and it hasn't happened yet today.</summary>
    public static IReadOnlyList<(Habit Habit, Offer Offer)> DueNow(DateTimeOffset now)
    {
        var minutes = now.LocalDateTime.TimeOfDay.TotalMinutes;
        var today = now.LocalDateTime.Date;
        var records = Load(now);

        var due = new List<(Habit, Offer)>();
        foreach (var h in Detect(now))
        {
            var diff = minutes - h.MedianMinute;
            if (diff < -10 || diff > 45) continue;
            var doneToday = records.Any(r => r.At.LocalDateTime.Date == today && r.Tool == h.Tool
                && string.Equals(r.Detail, h.Detail, StringComparison.OrdinalIgnoreCase));
            if (doneToday) continue;
            due.Add((h, MakeOffer(h)));
        }
        return due;
    }

    public static Offer MakeOffer(Habit h) => h.Tool switch
    {
        "open_app" => new Offer("open_app", new JsonObject { ["app"] = h.Detail }, $"open {h.Detail}"),
        "play_playlist" => new Offer("play_playlist", new JsonObject { ["name"] = h.Detail }, $"play your playlist \"{h.Detail}\""),
        "play_song" => new Offer("play_song", new JsonObject { ["query"] = h.Detail }, $"play \"{h.Detail}\""),
        "wordle_answer" => new Offer("wordle_answer", null, "check today's Wordle answer"),
        "check_mail" => new Offer("check_mail", null, "check your mail"),
        _ => new Offer("check_calendar", null, "check your calendar"),
    };

    /// <summary>The Japanese way to say what she is offering, as a noun-phrase-plus-verb ("Spotifyを開く").</summary>
    public static string JapaneseDescription(Habit h) => h.Tool switch
    {
        "open_app" => $"{h.Detail}を開く",
        "play_playlist" => $"プレイリスト「{h.Detail}」を再生する",
        "play_song" => $"「{h.Detail}」を再生する",
        "wordle_answer" => "今日のWordleの答えを確認する",
        "check_mail" => "メールを確認する",
        _ => "予定を確認する",
    };

    public static string Clock(int minuteOfDay) => DateTime.Today.AddMinutes(minuteOfDay).ToString("h:mm tt", CultureInfo.InvariantCulture);

    /// <summary>What she has noticed, for the "habits" tool.</summary>
    public static string Describe(DateTimeOffset now)
    {
        var habits = Detect(now);
        if (habits.Count == 0)
            return "No routines noticed yet. A routine needs the same request on at least four different days within three weeks, at similar times.";
        return "Routines noticed: " + string.Join("; ", habits.Select(h =>
            $"{MakeOffer(h).Description} around {Clock(h.MedianMinute)} ({h.Days} days)")) + ".";
    }

    /// <summary>Deletes everything she has logged about your routines.</summary>
    public static string Clear()
    {
        try
        {
            lock (FileLock) { if (File.Exists(StorePath)) File.Delete(StorePath); }
            return "Forgot all logged routines. She will start noticing them afresh.";
        }
        catch (Exception ex) { return $"Could not clear the routine log: {ex.Message}"; }
    }
}
