using System.Text.Json;
using System.Text.Json.Nodes;

namespace Raphael;

/// <summary>A countdown that is running.</summary>
public sealed record TimerItem(string Id, string Label, DateTimeOffset Due, int Seconds, bool FromPhone);

/// <summary>
/// Countdown timers ("set a timer for 2 minutes"). They are saved to %LOCALAPPDATA%\Raphael\timers.json, so a timer
/// survives closing Raphael: one that ran out while she was off is reported the next time she starts.
/// </summary>
public sealed class TimerService : IDisposable
{
    private const int MaxTimers = 20;
    private const int MaxSeconds = 7 * 24 * 3600;

    private readonly string _path;
    private readonly Func<TimerItem, bool, Task> _onDue;
    private readonly List<TimerItem> _timers = new();
    private readonly object _lock = new();
    private readonly CancellationTokenSource _cts = new();

    /// <param name="onDue">Called when a timer ends. The bool is true if it actually ran out while Raphael was switched off.</param>
    public TimerService(Func<TimerItem, bool, Task> onDue, string? path = null)
    {
        _onDue = onDue;
        _path = path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Raphael", "timers.json");
    }

    /// <summary>Loads saved timers (reporting any that ran out while she was off), then keeps time.</summary>
    public async Task StartAsync()
    {
        List<TimerItem> missed;
        lock (_lock)
        {
            Load();
            missed = _timers.Where(t => t.Due <= DateTimeOffset.Now).OrderBy(t => t.Due).ToList();
            _timers.RemoveAll(t => t.Due <= DateTimeOffset.Now);
            if (missed.Count > 0) Save();
        }

        foreach (var t in missed) await SafeOnDue(t, true);
        _ = Task.Run(LoopAsync);
    }

    private async Task LoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            List<TimerItem> due;
            lock (_lock)
            {
                due = _timers.Where(t => t.Due <= DateTimeOffset.Now).OrderBy(t => t.Due).ToList();
                if (due.Count > 0)
                {
                    _timers.RemoveAll(t => due.Contains(t));
                    Save();
                }
            }

            foreach (var t in due) await SafeOnDue(t, false);

            try { await Task.Delay(500, _cts.Token); } catch (OperationCanceledException) { break; }
        }
    }

    private async Task SafeOnDue(TimerItem timer, bool missed)
    {
        try { await _onDue(timer, missed); }
        catch (Exception ex) { Console.WriteLine($"[timer] could not announce '{timer.Label}': {ex.Message}"); }
    }

    // ---- what the tools call ----------------------------------------------------------------------------

    public string Set(int seconds, string? label, bool fromPhone)
    {
        if (seconds < 1) return "A timer needs a duration of at least one second.";
        if (seconds > MaxSeconds) return "The longest timer is 7 days.";

        label = (label ?? "").Trim();
        if (label.Length > 60) label = label[..60];

        lock (_lock)
        {
            if (_timers.Count >= MaxTimers) return $"There are already {MaxTimers} timers running. Cancel one first.";

            var due = DateTimeOffset.Now.AddSeconds(seconds);
            _timers.Add(new TimerItem(Guid.NewGuid().ToString("N")[..6], label, due, seconds, fromPhone));
            Save();
            return $"Timer{(label.Length > 0 ? $" '{label}'" : "")} set for {DurationEn(seconds)}. It will go off at {due:HH:mm:ss}.";
        }
    }

    public string List()
    {
        lock (_lock)
        {
            if (_timers.Count == 0) return "No timers are running.";
            var lines = _timers.OrderBy(t => t.Due).Select(t =>
                $"{(t.Label.Length > 0 ? $"'{t.Label}'" : "unnamed timer")}: {DurationEn((int)Math.Max(1, (t.Due - DateTimeOffset.Now).TotalSeconds))} left (goes off at {t.Due:HH:mm:ss})");
            return $"{_timers.Count} timer(s) running: {string.Join("; ", lines)}.";
        }
    }

    /// <param name="label">Text of the timer to cancel; empty or "all" cancels every timer.</param>
    public string Cancel(string? label)
    {
        label = (label ?? "").Trim();
        lock (_lock)
        {
            var everything = label.Length == 0 || label.Equals("all", StringComparison.OrdinalIgnoreCase);
            var matches = _timers.Where(t => everything || t.Label.Contains(label, StringComparison.OrdinalIgnoreCase)).ToList();
            if (matches.Count == 0) return everything ? "There are no timers to cancel." : $"No timer matches '{label}'.";

            _timers.RemoveAll(t => matches.Contains(t));
            Save();
            return $"Cancelled {matches.Count} timer(s): {string.Join(", ", matches.Select(t => t.Label.Length > 0 ? $"'{t.Label}'" : "unnamed"))}.";
        }
    }

    // ---- wording ----------------------------------------------------------------------------------------

    public static string DurationEn(int seconds)
    {
        var parts = new List<string>();
        var h = seconds / 3600; var m = seconds % 3600 / 60; var s = seconds % 60;
        if (h > 0) parts.Add($"{h} hour{(h == 1 ? "" : "s")}");
        if (m > 0) parts.Add($"{m} minute{(m == 1 ? "" : "s")}");
        if (s > 0 && h == 0) parts.Add($"{s} second{(s == 1 ? "" : "s")}");
        return string.Join(" ", parts);
    }

    public static string DurationJa(int seconds)
    {
        var h = seconds / 3600; var m = seconds % 3600 / 60; var s = seconds % 60;
        return (h > 0 ? $"{h}時間" : "") + (m > 0 ? $"{m}分" : "") + (s > 0 && h == 0 ? $"{s}秒" : "");
    }

    // ---- saving -----------------------------------------------------------------------------------------

    private void Load()
    {
        try
        {
            if (!File.Exists(_path)) return;
            foreach (var item in JsonNode.Parse(File.ReadAllText(_path))!.AsArray())
            {
                _timers.Add(new TimerItem(
                    item!["id"]!.GetValue<string>(), item["label"]?.GetValue<string>() ?? "",
                    DateTimeOffset.Parse(item["due"]!.GetValue<string>()),
                    item["seconds"]?.GetValue<int>() ?? 0, item["fromPhone"]?.GetValue<bool>() ?? false));
            }
        }
        catch { /* a damaged file just means no saved timers */ }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var array = new JsonArray(_timers.Select(t => (JsonNode)new JsonObject
            {
                ["id"] = t.Id, ["label"] = t.Label, ["due"] = t.Due.ToString("O"), ["seconds"] = t.Seconds, ["fromPhone"] = t.FromPhone
            }).ToArray());
            File.WriteAllText(_path, array.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* the timer still runs; it just won't survive a restart */ }
    }

    public void Dispose() => _cts.Cancel();
}
