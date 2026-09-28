using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;

namespace Raphael;

/// <summary>One event on the calendar. An all-day event has AllDay set and its times at local midnight.</summary>
public sealed record CalendarEvent(string Id, string Title, DateTimeOffset Start, DateTimeOffset End, bool AllDay);

/// <summary>Reads events from, and adds events to, the user's main Google Calendar through the shared <see cref="GoogleAuth"/>
/// sign-in (the "calendar.events" permission: it can create and read events, never touch calendar settings or other calendars,
/// and this code never deletes or edits an event that already exists).</summary>
public sealed class CalendarClient
{
    private readonly GoogleAuth _auth;

    public CalendarClient(GoogleAuth auth) => _auth = auth;

    /// <summary>
    /// Adds one event to the primary calendar. <paramref name="start"/> is a local date and time. Throws on a Google error;
    /// the caller turns that into a spoken message.
    /// </summary>
    public async Task<CalendarEvent> CreateEventAsync(string title, DateTimeOffset start, TimeSpan duration, string? description = null)
    {
        // Windows' own time zone id (e.g. "Singapore Standard Time") isn't the IANA name Google's "timeZone" field expects, so
        // the UTC offset is put straight into "dateTime" instead (RFC 3339 allows this, and no separate time zone is then needed).
        static string Rfc3339(DateTimeOffset t) => t.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture);

        var body = new JsonObject
        {
            ["summary"] = title,
            ["start"] = new JsonObject { ["dateTime"] = Rfc3339(start) },
            ["end"] = new JsonObject { ["dateTime"] = Rfc3339(start.Add(duration)) },
        };
        if (!string.IsNullOrWhiteSpace(description)) body["description"] = description;

        var json = await _auth.PostJsonAsync("https://www.googleapis.com/calendar/v3/calendars/primary/events", body);
        return new CalendarEvent(json["id"]?.GetValue<string>() ?? "", title, start, start.Add(duration), false);
    }

    /// <summary>Events that overlap the given window, in start order. Cancelled ones and ones you declined are left out.</summary>
    public async Task<IReadOnlyList<CalendarEvent>> ListAsync(DateTimeOffset from, DateTimeOffset to)
    {
        static string Utc(DateTimeOffset t) => Uri.EscapeDataString(t.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture));

        var json = await _auth.GetJsonAsync(
            "https://www.googleapis.com/calendar/v3/calendars/primary/events"
            + $"?timeMin={Utc(from)}&timeMax={Utc(to)}&singleEvents=true&orderBy=startTime&maxResults=50");

        var events = new List<CalendarEvent>();
        foreach (var item in json["items"]?.AsArray() ?? new JsonArray())
        {
            if (item == null || item["status"]?.GetValue<string>() == "cancelled") continue;
            if (item["eventType"]?.GetValue<string>() == "workingLocation") continue;

            var declined = item["attendees"]?.AsArray().Any(a =>
                a?["self"]?.GetValue<bool>() == true && a["responseStatus"]?.GetValue<string>() == "declined") ?? false;
            if (declined) continue;

            var start = ParseTime(item["start"], out var allDay);
            var end = ParseTime(item["end"], out _);
            if (start == null || end == null) continue;

            events.Add(new CalendarEvent(item["id"]?.GetValue<string>() ?? "", item["summary"]?.GetValue<string>() ?? "an untitled event",
                start.Value, end.Value, allDay));
        }
        return events;
    }

    private static DateTimeOffset? ParseTime(JsonNode? node, out bool allDay)
    {
        allDay = false;
        var dateTime = node?["dateTime"]?.GetValue<string>();
        if (dateTime != null) return DateTimeOffset.Parse(dateTime, CultureInfo.InvariantCulture);

        var date = node?["date"]?.GetValue<string>();
        if (date == null) return null;
        allDay = true;
        var local = DateTime.SpecifyKind(DateTime.ParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture), DateTimeKind.Local);
        return new DateTimeOffset(local);
    }
}

/// <summary>
/// Warns you shortly before an event starts ("Notice. Standup starts in 10 minutes."), once per event. Each warning is
/// remembered on disk, so closing and reopening her inside that window doesn't repeat it. All-day events don't trigger a warning.
/// </summary>
public sealed class CalendarWatcher : IDisposable
{
    private readonly Func<DateTimeOffset, DateTimeOffset, Task<IReadOnlyList<CalendarEvent>>> _list;
    private readonly Func<CalendarEvent, int, Task> _announce;
    private readonly Func<Task>? _onLoginRequired;
    private readonly Action<string>? _log;
    private readonly TimeSpan _interval;
    private readonly TimeSpan _lead;
    private readonly Func<DateTimeOffset> _now;
    private readonly string _statePath;
    private readonly CancellationTokenSource _cts = new();
    private readonly HashSet<string> _reminded = new();
    private bool _loginPromptedRecently;
    private string? _lastLoggedError;

    /// <param name="announce">Called with the event and the whole minutes left until it starts (0 = starting now).</param>
    public CalendarWatcher(Func<DateTimeOffset, DateTimeOffset, Task<IReadOnlyList<CalendarEvent>>> list,
        Func<CalendarEvent, int, Task> announce, Func<Task>? onLoginRequired = null, Action<string>? log = null,
        TimeSpan? interval = null, TimeSpan? lead = null, Func<DateTimeOffset>? now = null, string? statePath = null)
    {
        _list = list;
        _announce = announce;
        _onLoginRequired = onLoginRequired;
        _log = log;
        _interval = interval ?? TimeSpan.FromSeconds(60);
        _lead = lead ?? TimeSpan.FromMinutes(10);
        _now = now ?? (() => DateTimeOffset.Now);
        _statePath = statePath ?? System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Raphael", "calendar_reminded.txt");
        LoadState();
    }

    public void Start() => _ = Task.Run(LoopAsync);

    private async Task LoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                await CheckOnceAsync();
                _lastLoggedError = null;
            }
            catch (GmailLoginRequiredException)
            {
                if (_onLoginRequired != null && !_loginPromptedRecently)
                {
                    _loginPromptedRecently = true;
                    _ = Task.Delay(TimeSpan.FromHours(1)).ContinueWith(_ => _loginPromptedRecently = false);
                    try { await _onLoginRequired(); } catch { }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A network hiccup: try again next round. Say what went wrong once, so a real problem isn't invisible.
                var message = Describe(ex);
                if (message != _lastLoggedError) { _lastLoggedError = message; _log?.Invoke(message); }
            }

            try { await Task.Delay(_interval, _cts.Token); } catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>One look at the calendar. Public so it can be tested without waiting for the timer.</summary>
    public async Task CheckOnceAsync()
    {
        var now = _now();
        var events = await _list(now - TimeSpan.FromMinutes(1), now + _lead + TimeSpan.FromMinutes(1));

        foreach (var e in events)
        {
            if (e.AllDay) continue;
            var until = e.Start - now;
            if (until > _lead || until < TimeSpan.FromMinutes(-1)) continue;

            var key = $"{e.Id}|{e.Start.ToUnixTimeSeconds()}"; // a moved event gets a new warning
            if (!_reminded.Add(key)) continue;
            SaveState(now);

            await _announce(e, Math.Max(0, (int)Math.Ceiling(until.TotalMinutes)));
        }
    }

    /// <summary>What is on today or tomorrow, for the check_calendar tool.</summary>
    public async Task<string> DescribeAsync(string? day)
    {
        try
        {
            var now = _now();
            var tomorrow = string.Equals(day?.Trim(), "tomorrow", StringComparison.OrdinalIgnoreCase);
            var startOfToday = new DateTimeOffset(now.LocalDateTime.Date, TimeZoneInfo.Local.GetUtcOffset(now.LocalDateTime.Date));
            var from = tomorrow ? startOfToday.AddDays(1) : now;
            var to = tomorrow ? startOfToday.AddDays(2) : startOfToday.AddDays(1);

            var events = (await _list(from, to)).Where(e => e.End > from && e.Start < to).ToList();
            var label = tomorrow ? "Tomorrow" : "Today";
            if (events.Count == 0) return tomorrow ? "Nothing on the calendar tomorrow." : "Nothing left on the calendar today.";

            var parts = events.Take(10).Select(e => e.AllDay ? $"all day: {e.Title}" : $"{Clock(e.Start)} {e.Title}");
            var more = events.Count > 10 ? $" (and {events.Count - 10} more)" : "";
            return $"{label}: {string.Join("; ", parts)}{more}.";
        }
        catch (GmailLoginRequiredException)
        {
            return "Google needs to be signed in again. Restart Raphael and approve the Google login that opens.";
        }
        catch (Exception ex)
        {
            return $"Could not read the calendar: {Describe(ex)}";
        }
    }

    public static string Clock(DateTimeOffset t) => t.ToLocalTime().ToString("h:mm tt", CultureInfo.InvariantCulture);

    private static string Describe(Exception ex) =>
        ex is HttpRequestException { StatusCode: HttpStatusCode.Forbidden }
            ? "Google refused the calendar request. Enable the Google Calendar API in your Google Cloud project (APIs & Services > Library)."
            : ex.Message.Length > 160 ? ex.Message[..160] + "…" : ex.Message;

    private void LoadState()
    {
        try { if (File.Exists(_statePath)) foreach (var line in File.ReadAllLines(_statePath)) if (line.Length > 0) _reminded.Add(line); }
        catch { /* worst case a warning is repeated once */ }
    }

    private void SaveState(DateTimeOffset now)
    {
        try
        {
            // Forget warnings for events that ended long ago, so the file stays small.
            var cutoff = now.AddDays(-1).ToUnixTimeSeconds();
            _reminded.RemoveWhere(k => long.TryParse(k[(k.LastIndexOf('|') + 1)..], out var start) && start < cutoff);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_statePath)!);
            File.WriteAllLines(_statePath, _reminded);
        }
        catch { }
    }

    public void Dispose() => _cts.Cancel();
}
