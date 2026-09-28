using System.Text.Json.Nodes;

namespace Raphael;

/// <summary>
/// A short running record of what happened, so she can be asked for a recap or give one herself in the evening. Each
/// entry is a category and a short detail (a sender name, an app, a title) — never message text or bodies, and nothing
/// that isn't already spoken out loud somewhere. Stored in %LOCALAPPDATA%\Raphael\activity_log.jsonl, capped and trimmed
/// so it never grows without bound.
/// </summary>
public static class ActivityLog
{
    private const int MaxEntries = 3000;
    private const int TrimTo = 2000;

    public static string? PathOverride; // for tests
    private static string LogPath => PathOverride ?? System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Raphael", "activity_log.jsonl");
    private static readonly object FileLock = new();
    private static int _sinceTrimCheck;

    // category -> (English label, Japanese label), used by Summarize.
    private static readonly Dictionary<string, (string En, string Ja)> Labels = new()
    {
        ["mail"] = ("mail", "メール"),
        ["calendar"] = ("calendar reminder(s)", "予定の通知"),
        ["notification"] = ("notification(s)", "通知"),
        ["tab"] = ("browser alert(s)", "ブラウザの通知"),
        ["scan"] = ("virus scan(s)", "ウイルススキャン"),
        ["timer"] = ("timer(s)", "タイマー"),
        ["routine"] = ("routine(s) offered", "習慣の提案"),
        ["battery"] = ("battery warning(s)", "バッテリー警告"),
        ["disk"] = ("disk-space warning(s)", "ディスク容量の警告"),
        ["defender"] = ("Defender warning(s)", "Defenderの警告"),
        ["presence"] = ("welcome-back moment(s)", "おかえりの挨拶"),
        ["coding"] = ("coding-break reminder(s)", "作業時間の通知"),
    };

    public static void Record(string category, string detail, DateTimeOffset? at = null)
    {
        detail = string.Join(' ', (detail ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (detail.Length > 80) detail = detail[..80];

        var line = new JsonObject { ["t"] = (at ?? DateTimeOffset.Now).ToUnixTimeSeconds(), ["c"] = category, ["d"] = detail }.ToJsonString();
        try
        {
            lock (FileLock)
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(LogPath)!);
                File.AppendAllText(LogPath, line + "\n");

                // Trimming every write is wasteful; a rough periodic check is enough since this is a soft cap.
                if (++_sinceTrimCheck >= 200)
                {
                    _sinceTrimCheck = 0;
                    var lines = File.ReadAllLines(LogPath);
                    if (lines.Length > MaxEntries)
                        File.WriteAllLines(LogPath, lines.Skip(lines.Length - TrimTo));
                }
            }
        }
        catch { /* a missed note is harmless */ }
    }

    private static List<(DateTimeOffset At, string Category, string Detail)> Load(DateTimeOffset since)
    {
        var list = new List<(DateTimeOffset, string, string)>();
        try
        {
            lock (FileLock)
            {
                if (!File.Exists(LogPath)) return list;
                foreach (var line in File.ReadAllLines(LogPath))
                {
                    try
                    {
                        var j = JsonNode.Parse(line)!;
                        var at = DateTimeOffset.FromUnixTimeSeconds(j["t"]!.GetValue<long>());
                        if (at >= since) list.Add((at, j["c"]!.GetValue<string>(), j["d"]?.GetValue<string>() ?? ""));
                    }
                    catch { }
                }
            }
        }
        catch { }
        return list;
    }

    private static (DateTimeOffset From, DateTimeOffset To, string Label) Range(string period, DateTimeOffset now)
    {
        var offset = TimeZoneInfo.Local.GetUtcOffset(now.LocalDateTime);
        var startOfToday = new DateTimeOffset(now.LocalDateTime.Date, offset);
        return (period ?? "today").Trim().ToLowerInvariant() switch
        {
            "yesterday" => (startOfToday.AddDays(-1), startOfToday, "Yesterday"),
            "week" => (now.AddDays(-7), now, "This week"),
            _ => (startOfToday, startOfToday.AddDays(1), "Today"),
        };
    }

    /// <summary>What has happened, for the recap tool (English; Gemini phrases the spoken reply from this).</summary>
    public static string Summarize(string period, DateTimeOffset now)
    {
        var (from, to, label) = Range(period, now);
        var entries = Load(from).Where(e => e.At < to).ToList();
        if (entries.Count == 0) return $"{label}: nothing notable has happened{(from <= now && to > now ? " yet" : "")}.";

        var parts = Labels.Select(kv =>
        {
            var g = entries.Where(e => e.Category == kv.Key).ToList();
            if (g.Count == 0) return "";
            var names = g.Select(e => e.Detail).Where(d => d.Length > 0).Distinct().Take(3).ToList();
            return $"{g.Count} {kv.Value.En}{(names.Count > 0 ? $" ({string.Join(", ", names)})" : "")}";
        }).Where(s => s.Length > 0).ToList();

        return $"{label}: {string.Join("; ", parts)}.";
    }

    /// <summary>The same thing, as counts only, for her own unprompted evening recap (no names, kept short to say aloud).</summary>
    public static string SummarizeJapanese(string period, DateTimeOffset now)
    {
        var (from, to, _) = Range(period, now);
        var entries = Load(from).Where(e => e.At < to).ToList();
        if (entries.Count == 0) return "特筆すべき活動はありませんでした。";

        var parts = Labels.Select(kv =>
        {
            var n = entries.Count(e => e.Category == kv.Key);
            return n == 0 ? "" : $"{kv.Value.Ja}{n}件";
        }).Where(s => s.Length > 0);

        return string.Join("、", parts) + "でした。";
    }
}
