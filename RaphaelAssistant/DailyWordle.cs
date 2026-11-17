using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Raphael;

/// <summary>
/// "Message me the Wordle answer every day at 12 am." Saved in %LOCALAPPDATA%\Raphael\wordle-daily.json (on/off, time of
/// day, last date sent). It only runs while Raphael does: if she was off at that time, the answer is sent when she next
/// starts, marked as late. The message goes to the user's Telegram.
/// </summary>
public static class DailyWordle
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Raphael", "wordle-daily.json");
    private static readonly object Gate = new();
    private static readonly TimeSpan LateAfter = TimeSpan.FromMinutes(10);

    private static TimeSpan? _time;      // null = off
    private static string _lastSent = ""; // yyyy-MM-dd

    /// <param name="send">How to message the user. Null = through the running Raphael's Telegram link (Program.Phone);
    /// the hidden standby copy passes its own sender so the message still goes out after she is powered off.</param>
    public static void Start(Func<string, Task>? send = null, CancellationToken ct = default)
    {
        Load();
        _send = send;
        _ = Task.Run(() => LoopAsync(ct));
    }

    private static Func<string, Task>? _send;
    private static Func<string, Task>? Sender => _send ?? Program.Phone;

    public static string Control(string? action, string? time)
    {
        action = (action ?? "status").Trim().ToLowerInvariant();
        lock (Gate)
        {
            switch (action)
            {
                case "on":
                    var at = TimeSpan.Zero;
                    if (!string.IsNullOrWhiteSpace(time)
                        && !TimeSpan.TryParseExact(time.Trim(), new[] { @"h\:mm", @"hh\:mm" }, CultureInfo.InvariantCulture, out at))
                        return $"Could not read the time '{time}'. Use HH:mm, 24-hour.";
                    _time = at;
                    // Turning it on after today's time has passed means "from tomorrow", not an immediate "late" message.
                    if (DateTime.Now >= DateTime.Now.Date + at) _lastSent = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                    Save();
                    return $"Every day at {at:hh\\:mm} she will send the Wordle answer to the user's Telegram. It is sent while Raphael is running, and also after a proper power-off (a hidden standby copy sends it); if she was closed another way, it is sent when she next starts. The PC must be on.";
                case "off":
                    _time = null;
                    Save();
                    return "The daily Wordle message is turned off.";
                default:
                    return _time == null
                        ? "The daily Wordle message is off."
                        : $"The daily Wordle message is on, every day at {_time:hh\\:mm} (last sent: {(_lastSent.Length > 0 ? _lastSent : "never")}).";
            }
        }
    }

    private static async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                TimeSpan? at;
                string last;
                lock (Gate) { at = _time; last = _lastSent; }

                var now = DateTime.Now;
                var today = now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                var send = Sender;
                if (at != null && last != today && now >= now.Date + at.Value && send != null)
                {
                    var late = now - (now.Date + at.Value) > LateAfter;
                    var answer = await WordleLookup.LookupAsync(now.Date);
                    if (answer != null)
                    {
                        var (number, _, solution) = answer.Value;
                        await send($"🟩 Wordle #{number}: {solution}" + (late ? $" (late: Raphael was off at {at.Value:hh\\:mm})" : ""));
                        lock (Gate) { _lastSent = today; Save(); }
                    }
                    // Not published yet or no connection: stay unsent and try again on the next pass.
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[wordle-daily] {ex.Message}");
            }

            try { await Task.Delay(TimeSpan.FromSeconds(30), ct); } catch (OperationCanceledException) { break; }
        }
    }

    private static void Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return;
            var json = JsonNode.Parse(File.ReadAllText(FilePath));
            var t = json?["time"]?.GetValue<string>();
            if (t != null && TimeSpan.TryParseExact(t, @"hh\:mm", CultureInfo.InvariantCulture, out var parsed)) _time = parsed;
            _lastSent = json?["lastSent"]?.GetValue<string>() ?? "";
        }
        catch { /* a damaged file just means it is off */ }
    }

    private static void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var json = new JsonObject { ["time"] = _time?.ToString(@"hh\:mm"), ["lastSent"] = _lastSent };
            File.WriteAllText(FilePath, json.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* it still works until she closes */ }
    }
}
