using System.Globalization;

namespace Raphael;

/// <summary>Something she may say unprompted. Key names the situation, so it isn't repeated within Cooldown.</summary>
/// <param name="Category">If set, logged to <see cref="ActivityLog"/> under this category when said (null: not logged).</param>
public sealed record Suggestion(string Key, string Ja, string En, TimeSpan Cooldown, TimeSpan Ttl, Offer? Offer = null, string? Category = null);

/// <summary>
/// Speaks up on her own when something is worth knowing (disk nearly full, battery low, Defender switched off, an
/// important mail left unread), but rarely: a limit per day, a gap between two remarks, and a cooldown per situation.
/// Every rule is plain code and only reads; nothing here changes anything on the PC. When she said what is remembered on
/// disk, so restarting her doesn't repeat it.
/// </summary>
public sealed class ProactiveWatcher : IDisposable
{
    private readonly IReadOnlyList<Func<Task<IReadOnlyList<Suggestion>>>> _rules;
    private readonly Func<Suggestion, Task> _say;
    private readonly Func<DateTimeOffset> _now;
    private readonly TimeSpan _interval;
    private readonly TimeSpan _firstDelay;
    private readonly int _maxPerDay;
    private readonly TimeSpan _minGap;
    private readonly string _statePath;
    private readonly CancellationTokenSource _cts = new();
    private readonly List<(string Key, DateTimeOffset At)> _history = new();

    public ProactiveWatcher(IReadOnlyList<Func<Task<IReadOnlyList<Suggestion>>>> rules, Func<Suggestion, Task> say,
        Func<DateTimeOffset>? now = null, TimeSpan? interval = null, TimeSpan? firstDelay = null, int maxPerDay = 3,
        TimeSpan? minGap = null, string? statePath = null)
    {
        _rules = rules;
        _say = say;
        _now = now ?? (() => DateTimeOffset.Now);
        _interval = interval ?? TimeSpan.FromMinutes(5);
        _firstDelay = firstDelay ?? TimeSpan.FromMinutes(2);
        _maxPerDay = maxPerDay;
        _minGap = minGap ?? TimeSpan.FromMinutes(20);
        _statePath = statePath ?? System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Raphael", "proactive.txt");
        LoadState();
    }

    public void Start() => _ = Task.Run(LoopAsync);

    private async Task LoopAsync()
    {
        try { await Task.Delay(_firstDelay, _cts.Token); } catch (OperationCanceledException) { return; }
        while (!_cts.IsCancellationRequested)
        {
            try { await TickAsync(); } catch (Exception ex) when (ex is not OperationCanceledException) { /* try again next round */ }
            try { await Task.Delay(_interval, _cts.Token); } catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>One round: at most one remark. Public so it can be tested without waiting.</summary>
    public async Task TickAsync()
    {
        var now = _now();
        var today = now.LocalDateTime.Date;

        if (_history.Count(h => h.At.LocalDateTime.Date == today) >= _maxPerDay) return;
        if (_history.Count > 0 && now - _history.Max(h => h.At) < _minGap) return;

        foreach (var rule in _rules)
        {
            IReadOnlyList<Suggestion> candidates;
            try { candidates = await rule(); }
            catch (Exception ex) when (ex is not OperationCanceledException) { continue; } // one broken rule doesn't silence the rest

            foreach (var s in candidates)
            {
                var last = _history.Where(h => h.Key == s.Key).Select(h => (DateTimeOffset?)h.At).Max();
                if (last != null && now - last.Value < s.Cooldown) continue;

                _history.Add((s.Key, now));
                SaveState(now);
                await _say(s);
                return;
            }
        }
    }

    // ---- what she checks --------------------------------------------------------------------------------

    /// <summary>The system drive is nearly full.</summary>
    public static Func<Task<IReadOnlyList<Suggestion>>> DiskRule(Func<(long Free, long Total)>? read = null) => () =>
    {
        (long Free, long Total) drive;
        try
        {
            drive = read?.Invoke() ?? new Func<(long, long)>(() =>
            {
                var d = new DriveInfo(System.IO.Path.GetPathRoot(Environment.SystemDirectory)!);
                return (d.AvailableFreeSpace, d.TotalSize);
            })();
        }
        catch { return Task.FromResult<IReadOnlyList<Suggestion>>(Array.Empty<Suggestion>()); }

        var percent = drive.Total == 0 ? 100 : (int)Math.Round(100.0 * drive.Free / drive.Total);
        var gb = drive.Free / 1_073_741_824.0;
        if (percent >= 10 && gb >= 10) return Task.FromResult<IReadOnlyList<Suggestion>>(Array.Empty<Suggestion>());

        var gbText = gb.ToString("0.#", CultureInfo.InvariantCulture);
        return Task.FromResult<IReadOnlyList<Suggestion>>(new[]
        {
            new Suggestion("disk",
                $"告。システムドライブの空き容量が残り{percent}パーセントです。整理を推奨します。",
                $"Notice. The system drive has only {percent}% free ({gbText} GB). Cleaning it up is recommended.",
                TimeSpan.FromDays(2), TimeSpan.FromHours(1), Category: "disk")
        });
    };

    /// <summary>A laptop is running on battery and getting low. Two levels, each said once per stretch of low battery.</summary>
    public static Func<Task<IReadOnlyList<Suggestion>>> BatteryRule(Func<(bool OnBattery, int Percent)?>? read = null) => () =>
    {
        (bool OnBattery, int Percent)? status;
        try
        {
            status = read != null ? read() : new Func<(bool, int)?>(() =>
            {
                var p = System.Windows.Forms.SystemInformation.PowerStatus;
                if (p.BatteryChargeStatus.HasFlag(System.Windows.Forms.BatteryChargeStatus.NoSystemBattery)) return null; // a desktop PC
                return (p.PowerLineStatus == System.Windows.Forms.PowerLineStatus.Offline, (int)Math.Round(p.BatteryLifePercent * 100));
            })();
        }
        catch { status = null; }

        var none = Task.FromResult<IReadOnlyList<Suggestion>>(Array.Empty<Suggestion>());
        if (status is not { OnBattery: true } s || s.Percent > 20) return none;

        var level = s.Percent <= 10 ? 10 : 20;
        return Task.FromResult<IReadOnlyList<Suggestion>>(new[]
        {
            new Suggestion($"battery{level}",
                $"告。バッテリー残量が{s.Percent}パーセントです。充電を推奨します。",
                $"Notice. Battery is at {s.Percent}%. Please plug in the charger.",
                TimeSpan.FromHours(3), TimeSpan.FromMinutes(20), Category: "battery")
        });
    };

    /// <summary>Windows' protection has been switched off. Checked at most every six hours, since it starts a PowerShell.</summary>
    public static Func<Task<IReadOnlyList<Suggestion>>> DefenderRule(Func<Task<string>> status, Func<DateTimeOffset>? now = null)
    {
        var clock = now ?? (() => DateTimeOffset.Now);
        DateTimeOffset? lastChecked = null;
        return async () =>
        {
            if (lastChecked != null && clock() - lastChecked < TimeSpan.FromHours(6)) return Array.Empty<Suggestion>();
            lastChecked = clock();

            var text = await status();
            var off = text.Contains("antivirus OFF", StringComparison.Ordinal) || text.Contains("real-time protection OFF", StringComparison.Ordinal);
            return off
                ? new[]
                {
                    new Suggestion("defender",
                        "告。Microsoft Defenderの保護が無効になっています。設定の確認を推奨します。",
                        "Notice. Microsoft Defender protection is switched off. Checking Windows Security is recommended.",
                        TimeSpan.FromDays(3), TimeSpan.FromHours(1), Category: "defender")
                }
                : Array.Empty<Suggestion>();
        };
    }

    /// <summary>A mail that arrived while she was running has sat unread for a couple of hours.</summary>
    public static Func<Task<IReadOnlyList<Suggestion>>> MailRule(Func<TimeSpan, IReadOnlyList<(MailInfo Mail, TimeSpan Age)>> lingering) => () =>
    {
        var list = lingering(TimeSpan.FromHours(2)).Select(x =>
        {
            var hours = Math.Max(2, (int)Math.Round(x.Age.TotalHours));
            return new Suggestion($"mail:{x.Mail.Id}",
                $"告。{x.Mail.Sender}さんからのメールが{hours}時間、未読のままです。",
                $"Notice. The mail from {x.Mail.Sender} has been unread for {hours} hours.",
                TimeSpan.FromDays(365), TimeSpan.FromHours(1), Category: "mail");
        }).ToList();
        return Task.FromResult<IReadOnlyList<Suggestion>>(list);
    };

    /// <summary>
    /// A routine's usual time has come and it hasn't happened yet today: offer it ("You usually open Spotify around now. Proceed?").
    /// Only while you are at the PC, and at most once a day per routine. Saying "yes" makes her do it.
    /// </summary>
    public static Func<Task<IReadOnlyList<Suggestion>>> HabitRule(Func<DateTimeOffset>? now = null, Func<bool>? userPresent = null) => () =>
    {
        var none = Task.FromResult<IReadOnlyList<Suggestion>>(Array.Empty<Suggestion>());
        if (userPresent != null && !userPresent()) return none;

        var list = HabitLog.DueNow((now ?? (() => DateTimeOffset.Now))()).Select(d => new Suggestion(
            $"habit:{d.Habit.Tool}:{d.Habit.Detail.ToLowerInvariant()}",
            $"告。この時間帯には通常、{HabitLog.JapaneseDescription(d.Habit)}ことが多いです。実行しますか？",
            $"Notice. Around this time you usually {d.Offer.Description}. Proceed?",
            TimeSpan.FromHours(20), TimeSpan.FromMinutes(20), d.Offer, Category: "routine")).ToList();
        return Task.FromResult<IReadOnlyList<Suggestion>>(list);
    };

    /// <summary>
    /// Once a day, from the evening on, a short unprompted recap of what happened (counts only, no names spoken aloud).
    /// Silent if nothing notable happened. <paramref name="fromHour"/> is the local hour she may start giving it.
    /// </summary>
    public static Func<Task<IReadOnlyList<Suggestion>>> RecapRule(Func<DateTimeOffset>? now = null, int fromHour = 21) => () =>
    {
        var n = (now ?? (() => DateTimeOffset.Now))();
        if (n.LocalDateTime.Hour < fromHour) return Task.FromResult<IReadOnlyList<Suggestion>>(Array.Empty<Suggestion>());

        var en = ActivityLog.Summarize("today", n);
        if (en.Contains("nothing notable")) return Task.FromResult<IReadOnlyList<Suggestion>>(Array.Empty<Suggestion>());

        var ja = $"告。本日の活動を報告します。{ActivityLog.SummarizeJapanese("today", n)}";
        var key = $"recap:{n.LocalDateTime:yyyyMMdd}";
        return Task.FromResult<IReadOnlyList<Suggestion>>(new[]
            { new Suggestion(key, ja, $"Notice. A brief recap of today: {en}", TimeSpan.FromHours(20), TimeSpan.FromHours(3)) });
    };

    /// <summary>You've stayed in the same project, without switching away, for longer than <paramref name="threshold"/>: a nudge to
    /// take a break. Once per project per stretch (a fresh cooldown starts once you actually switch away and come back).</summary>
    public static Func<Task<IReadOnlyList<Suggestion>>> CodingSessionRule(Func<(string? Project, TimeSpan Elapsed)> read, TimeSpan threshold) => () =>
    {
        var (project, elapsed) = read();
        if (project == null || elapsed < threshold) return Task.FromResult<IReadOnlyList<Suggestion>>(Array.Empty<Suggestion>());

        var text = elapsed.TotalHours >= 1 ? $"{(int)elapsed.TotalHours}h {elapsed.Minutes}m" : $"{(int)elapsed.TotalMinutes} minutes";
        return Task.FromResult<IReadOnlyList<Suggestion>>(new[]
        {
            new Suggestion($"coding:{project.ToLowerInvariant()}",
                $"告。{project}での作業が{text}続いています。小休憩を推奨します。",
                $"Notice. You've been working in {project} for {text} straight. A short break is recommended.",
                TimeSpan.FromHours(4), TimeSpan.FromMinutes(20), Category: "coding")
        });
    };

    // ---- memory of what she said

    private void LoadState()
    {
        try
        {
            if (!File.Exists(_statePath)) return;
            foreach (var line in File.ReadAllLines(_statePath))
            {
                var bar = line.LastIndexOf('|');
                if (bar > 0 && long.TryParse(line[(bar + 1)..], out var unix))
                    _history.Add((line[..bar], DateTimeOffset.FromUnixTimeSeconds(unix)));
            }
        }
        catch { /* worst case a remark is repeated once */ }
    }

    private void SaveState(DateTimeOffset now)
    {
        try
        {
            _history.RemoveAll(h => now - h.At > TimeSpan.FromDays(400));
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_statePath)!);
            File.WriteAllLines(_statePath, _history.Select(h => $"{h.Key}|{h.At.ToUnixTimeSeconds()}"));
        }
        catch { }
    }

    public void Dispose() => _cts.Cancel();
}

