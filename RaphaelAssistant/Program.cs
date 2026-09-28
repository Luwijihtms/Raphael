namespace Raphael;

public static class Program
{
    // Voice and typed commands can arrive at the same time; the client's history isn't thread-safe.
    private static readonly SemaphoreSlim CommandLock = new(1, 1);

    private static readonly string[] WakeWords = { "great sage", "sage", "raphael-san", "raphael san", "raphael" };

    public static async Task Main(string[] args)
    {
        if (args.Contains("--mic-test", StringComparer.OrdinalIgnoreCase))
        {
            MicTest.Run(WakeWords);
            return;
        }

        // What is left running after a power off: watches Telegram for her name and starts the real Raphael.
        if (args.Contains("--standby", StringComparer.OrdinalIgnoreCase))
        {
            await Standby.RunAsync();
            return;
        }
        Standby.SignalStop(); // if a standby copy is watching the bot, it steps aside now

        Console.OutputEncoding = System.Text.Encoding.UTF8; // so Japanese text prints correctly
        Console.InputEncoding = System.Text.Encoding.UTF8;  // ...and is read correctly when input is piped in
        SessionLog.Start();
        Console.WriteLine("=== Raphael — Desktop Assistant ===");

        // Started by Windows at login (the shortcut passes --startup): give the network and the desktop a moment first.
        if (args.Contains("--startup", StringComparer.OrdinalIgnoreCase))
        {
            Console.WriteLine("Starting with Windows: waiting 15 seconds for the network and desktop to be ready...");
            await Task.Delay(TimeSpan.FromSeconds(15));
        }

        var apiKey = Environment.GetEnvironmentVariable("GEMINI_API_KEY");
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            Console.WriteLine("ERROR: Set the GEMINI_API_KEY environment variable first.");
            Console.WriteLine("  Free key: https://aistudio.google.com/apikey");
            Console.WriteLine("  setx GEMINI_API_KEY \"your-key-here\"   (then restart the terminal)");
            return;
        }

        var llm = new GeminiClient(apiKey);
        CommandHandler.Llm = llm; // lets the view_screen tool send a screenshot to Gemini's vision
        // Voice input is switched off for now. To turn it back on: setx RAPHAEL_MIC on (then restart).
        var micOn = string.Equals(Environment.GetEnvironmentVariable("RAPHAEL_MIC"), "on", StringComparison.OrdinalIgnoreCase);
        var voice = new VoiceService(micOn, WakeWords);

        // Timers ("set a timer for 2 minutes"): announced out loud, and on the phone if it was set from there.
        // Created now, before anything can ask for one; the phone link is filled in once Telegram is up.
        Func<string, Task>? phone = null;
        Timers = new TimerService((timer, missed) => AnnounceTimerAsync(timer, missed, voice, phone));
        Scanner = new SecurityScanner(result => AnnounceScanAsync(result, voice, phone));

        voice.OnCommandHeard += async (command, audio) =>
        {
            // With audio, Gemini does the real transcription; Windows' guess is shown only for reference.
            Console.WriteLine(audio != null ? $"[mic guess] {command}" : $"[heard] {command}");
            await HandleCommand(command, audio, llm, voice);
        };

        var voicevoxUp = await voice.InitJapaneseVoiceAsync();
        Console.WriteLine(voicevoxUp
            ? "VOICEVOX connected."
            : "VOICEVOX not reachable at 127.0.0.1:50021 — falling back to the Windows English voice.");

        // Optional: command Raphael from a phone through a private Telegram bot.
        using var telegram = TelegramClient.Create((text, audio, mime) => HandleRemoteAsync(text, audio, mime, llm, voice));
        if (telegram != null && await telegram.StartAsync())
        {
            phone = text => telegram.SendToOwnerAsync(text);
            Console.WriteLine($"Telegram: connected as @{telegram.BotUsername}. Only your account can use it, and shell commands are disabled from the phone.");
        }

        voice.StartListening();

        // Gmail and Calendar (needs GMAIL_CLIENT_ID / GMAIL_CLIENT_SECRET). Started now, and given a few seconds to connect
        // with an already-saved login, so the greeting right after can mention what she finds. A sign-in that needs the
        // browser (first run, or an expired login) is not waited for: it just continues in the background as usual.
        var googleReady = StartGoogle(voice);
        await Task.WhenAny(googleReady, Task.Delay(TimeSpan.FromSeconds(4)));

        // Woken from the phone: a line of her own (no Gemini call), different from the usual start-up greetings.
        if (args.Contains("--woken", StringComparer.OrdinalIgnoreCase))
        {
            var (ja, en) = WokenGreetings[Random.Shared.Next(WokenGreetings.Length)];
            Console.WriteLine($"[Raphael] {ja}");
            voice.Say(ja, en);
        }
        else
        {
            await GreetAsync(llm, voice);
        }

        // Start keeping time (and report timers that ran out while she was off).
        await Timers.StartAsync();

        // Knows when you are busy (full-screen game or video, a call) so she doesn't talk over it, and when you come back
        // after a long absence. RAPHAEL_PRESENCE=off turns it off.
        if (!string.Equals(Environment.GetEnvironmentVariable("RAPHAEL_PRESENCE"), "off", StringComparison.OrdinalIgnoreCase))
        {
            Presence = new PresenceMonitor(onFree: () => SpeakHeldAsync(voice), onReturn: idle => WelcomeBackAsync(voice, idle));
            Presence.Start();
            Console.WriteLine("Presence: she stays quiet during full-screen games, videos and calls, and reads out what she held afterwards.");
        }

        // Notices which project you're coding in (from the window title only) so she can check git status on request
        // and nudge a break after a long unbroken stretch. RAPHAEL_CODING=off turns it off.
        if (!string.Equals(Environment.GetEnvironmentVariable("RAPHAEL_CODING"), "off", StringComparison.OrdinalIgnoreCase))
        {
            Coding = new CodingSessionTracker();
            Coding.Start();
            Console.WriteLine("Coding: aware of which project you're in, and can check its git status on request.");
        }

        var rules = NotificationRules.Load();

        // Report new notifications (browser sites, Messenger, ...) out loud. RAPHAEL_NOTIFY=off turns this off.
        if (!string.Equals(Environment.GetEnvironmentVariable("RAPHAEL_NOTIFY"), "off", StringComparison.OrdinalIgnoreCase))
        {
            if (NotificationWatcher.CanRead())
            {
                new NotificationWatcher(rules, (notices, startup) => AnnounceNoticesAsync(notices, startup, rules, voice)).Start();
                Console.WriteLine("Notifications: watching for new ones from browsers and chat apps.");
            }
            else
            {
                Console.WriteLine("Notifications: Raphael is not allowed to read them (Settings > Privacy & security > Notifications).");
            }
        }

        // Also watch the browser's tab titles for unread counters ("(1) Messenger"). This works even with popups
        // turned off. RAPHAEL_TABS=off turns it off.
        if (!string.Equals(Environment.GetEnvironmentVariable("RAPHAEL_TABS"), "off", StringComparison.OrdinalIgnoreCase))
        {
            new TabTitleWatcher(rules, (alerts, startup) => AnnounceTabsAsync(alerts, startup, rules, voice)).Start();
            Console.WriteLine("Browser tabs: watching for unread counters like \"(1) Messenger\".");
        }
        StartProactive(voice);

        Console.WriteLine(micOn
            ? "Listening for wake word \"Sage\", \"Great Sage\", \"Raphael-san\" or \"Raphael\". Say a command, or type one below. Type 'exit' to quit."
            : "Type a command below. Type 'exit' to quit.");

        // Also allow typed input, useful for testing without a mic. A power-off request from anywhere (typed, spoken,
        // or from the phone) ends the loop too, even while it is waiting for the next line.
        var powerOff = false;
        while (true)
        {
            var read = Task.Run(Console.ReadLine);
            if (await Task.WhenAny(read, ShutdownRequested.Task) != read) { powerOff = true; break; }

            var typed = (await read)?.TrimStart('﻿'); // piped input can start with an invisible byte-order mark
            if (typed == null) { await Task.Delay(500); continue; }
            if (typed.Trim().Equals("exit", StringComparison.OrdinalIgnoreCase)) break;
            if (typed.Trim().Length == 0) continue;

            await HandleCommand(typed, null, llm, voice);
            if (ShutdownRequested.Task.IsCompleted) { powerOff = true; break; }
        }

        if (powerOff)
        {
            // Let a command that is still being handled finish first, so her goodbye is spoken and the reply reaches the phone.
            await CommandLock.WaitAsync(TimeSpan.FromSeconds(30));
            await Task.Delay(TimeSpan.FromSeconds(1.5));
            Console.WriteLine("Raphael has powered off.");
        }

        voice.StopListening();
        voice.Dispose();
        if (powerOff) Standby.Spawn(); // stays hidden, so a Telegram message with her name can start her again
        Environment.Exit(0); // don't wait for the console read that may still be pending
    }

    // ---- virus scans -----------------------------------------------------------------------------------------

    internal static SecurityScanner Scanner = null!;

    /// <summary>A Defender scan she started has ended: say how it went, and message the phone if it was asked for from there.</summary>
    private static async Task AnnounceScanAsync(ScanResult r, VoiceService voice, Func<string, Task>? phone)
    {
        var folderName = System.IO.Path.GetFileName(r.Path?.TrimEnd('\\'));
        if (string.IsNullOrEmpty(folderName)) folderName = r.Path ?? "the folder"; // e.g. a whole drive like C:\
        var (kindJa, kindEn) = r.Kind switch
        {
            "quick" => ("クイックスキャン", "quick scan"),
            "full" => ("フルスキャン", "full scan"),
            _ => ("フォルダースキャン", "scan of " + folderName)
        };
        var took = r.Duration.TotalSeconds < 1 ? "under a second" : TimerService.DurationEn((int)Math.Max(1, r.Duration.TotalSeconds));

        string ja, en;
        if (r.Error != null)
        {
            ja = $"告。{kindJa}を完了できませんでした。";
            en = $"Notice. The {kindEn} could not finish: {Shorten(r.Error, 100)}";
        }
        else if (r.Threats.Count == 0)
        {
            ja = $"告。{kindJa}が完了しました。脅威は検出されませんでした。";
            en = $"Notice. The {kindEn} finished in {took}. No threats found.";
        }
        else
        {
            ja = $"告。{kindJa}が完了しました。脅威を{r.Threats.Count}件検出しました。Windowsセキュリティで確認してください。";
            en = $"Notice. The {kindEn} finished in {took}. {r.Threats.Count} threat(s) detected: {Shorten(string.Join("; ", r.Threats.Take(3)), 160)}. Check Windows Security.";
        }

        Console.WriteLine($"[scan] {en}");
        ActivityLog.Record("scan", r.Error != null ? $"{r.Kind} failed" : $"{r.Kind}: {r.Threats.Count} threat(s)");
        await SayAnnouncementAsync(voice, ja, en);

        if (r.FromPhone && phone != null) await phone($"🛡 {en}");
    }

    // ---- timers ----------------------------------------------------------------------------------------------

    internal static TimerService Timers = null!;

    /// <summary>A timer ended (or ran out while she was off): say so out loud, and message the phone if it was set from there.</summary>
    private static async Task AnnounceTimerAsync(TimerItem timer, bool missed, VoiceService voice, Func<string, Task>? phone)
    {
        var what = timer.Label.Length > 0 ? timer.Label : null;
        var ja = missed
            ? $"告。停止中にタイマーが完了していました。{what ?? TimerService.DurationJa(timer.Seconds)}。"
            : $"告。タイマーが完了しました。{TimerService.DurationJa(timer.Seconds)}が経過しました。{what}".TrimEnd() + (what == null ? "" : "。");
        var en = missed
            ? $"Notice. A timer ended while I was off: {what ?? TimerService.DurationEn(timer.Seconds)} (due {timer.Due:HH:mm})."
            : $"Notice. Timer complete: {TimerService.DurationEn(timer.Seconds)} have passed{(what != null ? $" ({what})" : "")}.";

        Console.WriteLine($"[timer] {en}");
        ActivityLog.Record("timer", what ?? TimerService.DurationEn(timer.Seconds));
        Console.WriteLine($"[Raphael] {ja}");

        // Wait for any reply she is in the middle of, so the announcement doesn't cut it off.
        await CommandLock.WaitAsync();
        try { await voice.SayAsync(ja, en); }
        finally { CommandLock.Release(); }

        if (timer.FromPhone && phone != null) await phone($"⏰ {en}");
    }

    // ---- notifications ---------------------------------------------------------------------------------------

    /// <summary>Says out loud that new (or, at start-up, unread) notifications arrived. Only the sender and title, not the message.</summary>
    private static async Task AnnounceNoticesAsync(IReadOnlyList<Notice> notices, bool startup, NotificationRules rules, VoiceService voice)
    {
        foreach (var n in notices) Console.WriteLine($"[notice] {n.Source}: {n.Title}");

        string ja, en;
        if (notices.Count == 1 && !startup)
        {
            var n = notices[0];
            var title = Shorten(n.Title, 40);
            var hasTitle = title.Length > 0
                && !title.Equals(n.App, StringComparison.OrdinalIgnoreCase)
                && !title.Equals(n.Source, StringComparison.OrdinalIgnoreCase);

            ja = $"告。{rules.SpokenName(n)}に新着通知があります。" + (hasTitle ? $"{title}。" : "");
            en = $"Notice. New notification from {n.Source}" + (hasTitle ? $": {title}" : "") + ".";
            if (rules.ShowBody && n.Body.Length > 0) en += $" {Shorten(n.Body, 120)}";
        }
        else
        {
            var spoken = string.Join("、", notices.Select(rules.SpokenName).Distinct());
            var sources = string.Join(", ", notices.Select(n => n.Source).Distinct());
            ja = startup ? $"告。未読の通知が{notices.Count}件あります。{spoken}。" : $"告。新着通知が{notices.Count}件あります。{spoken}。";
            en = startup ? $"Notice. {notices.Count} unread notification(s): {sources}." : $"Notice. {notices.Count} new notifications: {sources}.";
        }

        ActivityLog.Record("notification", string.Join(", ", notices.Select(n => n.Source).Distinct().Take(3)));
        await SayAnnouncementAsync(voice, ja, en);
    }

    /// <summary>Says out loud that a browser tab shows unread messages. It knows how many, not from whom.</summary>
    private static async Task AnnounceTabsAsync(IReadOnlyList<TabAlert> alerts, bool startup, NotificationRules rules, VoiceService voice)
    {
        foreach (var a in alerts) Console.WriteLine($"[tab] {a.Text ?? $"{a.Site}: {a.Count} unread"}{(startup ? " (already there at start-up)" : "")}");
        ActivityLog.Record("tab", string.Join(", ", alerts.Select(a => a.Sender ?? a.Site).Distinct().Take(3)));

        // Each alert as (Japanese, English). A title like "Nicole Louise messaged you" says who; a bare counter only says how many.
        (string Ja, string En) Describe(TabAlert a)
        {
            if (a.Sender != null)
                return ($"{a.Sender}さんから新着メッセージです。", $"{a.Text}");
            if (a.Text != null)
                return ("新着の通知があります。", $"{a.Text}");
            return ($"{rules.SpokenForTab(a.Site)}に未読が{a.Count}件あります。", $"{rules.DisplayForTab(a.Site)} has {a.Count} unread.");
        }

        var parts = alerts.Select(Describe).ToList();
        var ja = "告。" + string.Join("", parts.Select(p => p.Ja));
        var en = "Notice. " + string.Join(" ", parts.Select(p => p.En.TrimEnd('.') + "."));

        await SayAnnouncementAsync(voice, ja, en);
    }

    // ---- staying quiet while you are busy --------------------------------------------------------------------

    internal static PresenceMonitor? Presence;
    internal static CodingSessionTracker? Coding;
    private static readonly List<(DateTimeOffset At, string Ja, string En, TimeSpan Ttl)> Held = new();
    private static DateTimeOffset _lastWelcome = DateTimeOffset.MinValue;

    /// <summary>
    /// Says an announcement, unless you are busy (a full-screen game or video, or a call): then it is held, and read out
    /// together when you are free. Timers and calendar warnings don't go through here, since they are time-critical.
    /// </summary>
    private static async Task SayAnnouncementAsync(VoiceService voice, string ja, string en, TimeSpan? ttl = null)
    {
        if (Presence is { IsBusy: true } presence)
        {
            lock (Held)
            {
                Held.Add((DateTimeOffset.Now, ja, en, ttl ?? TimeSpan.FromHours(3)));
                if (Held.Count > 20) Held.RemoveAt(0);
            }
            Console.WriteLine($"[held while {presence.BusyReason}] {en}");
            return;
        }
        await SayUnderLockAsync(voice, ja, en);
    }

    /// <summary>You are free again: read out what was held, as one short report.</summary>
    private static async Task SpeakHeldAsync(VoiceService voice)
    {
        List<(DateTimeOffset At, string Ja, string En, TimeSpan Ttl)> items;
        lock (Held)
        {
            var now = DateTimeOffset.Now;
            items = Held.Where(h => now - h.At <= h.Ttl).ToList(); // an old, stale alert isn't worth reading out
            Held.Clear();
        }
        if (items.Count == 0) return;

        static string Plain(string text, string marker) => text.StartsWith(marker) ? text[marker.Length..].TrimStart() : text;

        var shown = items.Take(6).ToList();
        var ja = $"告。取り込み中に保留した通知が{items.Count}件あります。" + string.Join("", shown.Select(h => Plain(h.Ja, "告。")));
        var en = $"Notice. {items.Count} alert{(items.Count == 1 ? "" : "s")} held while you were busy. "
                 + string.Join(" ", shown.Select(h => Plain(h.En, "Notice.")));
        if (items.Count > shown.Count)
        {
            ja += $"ほか{items.Count - shown.Count}件です。";
            en += $" And {items.Count - shown.Count} more.";
        }
        await SayUnderLockAsync(voice, ja, en);
    }

    /// <summary>You are back after a long time away from the keyboard: one short line (at most once every three hours).</summary>
    private static async Task WelcomeBackAsync(VoiceService voice, TimeSpan idle)
    {
        if (Presence is { IsBusy: true } || DateTimeOffset.Now - _lastWelcome < TimeSpan.FromHours(3)) return;
        _lastWelcome = DateTimeOffset.Now;

        var (ja, en) = new (string, string)[]
        {
            ("告。おかえりなさいませ。不在中も待機を継続していました。", "Notice. Welcome back. I remained on standby while you were away."),
            ("告。操作を再確認しました。おかえりなさいませ。", "Notice. Input detected again. Welcome back."),
            ("告。おかえりなさいませ。異常は検出されていません。", "Notice. Welcome back. No anomalies were detected in your absence."),
        }[Random.Shared.Next(3)];
        ActivityLog.Record("presence", $"away {idle.TotalMinutes:0} min");
        await SayUnderLockAsync(voice, ja, en);
    }

    // ---- speaking up on her own ------------------------------------------------------------------------------

    /// <summary>Starts the (rare, rule-based) unprompted remarks. RAPHAEL_PROACTIVE=off turns them off.</summary>
    private static void StartProactive(VoiceService voice)
    {
        if (string.Equals(Environment.GetEnvironmentVariable("RAPHAEL_PROACTIVE"), "off", StringComparison.OrdinalIgnoreCase)) return;

        var rules = new List<Func<Task<IReadOnlyList<Suggestion>>>>
        {
            ProactiveWatcher.BatteryRule(),
            ProactiveWatcher.DiskRule(),
            ProactiveWatcher.MailRule(age => Mail?.Lingering(age) ?? Array.Empty<(MailInfo, TimeSpan)>()),
            ProactiveWatcher.DefenderRule(() => Scanner.StatusAsync()),
        };
        if (!string.Equals(Environment.GetEnvironmentVariable("RAPHAEL_HABITS"), "off", StringComparison.OrdinalIgnoreCase))
            rules.Add(ProactiveWatcher.HabitRule(userPresent: () => (Presence?.LastIdleSeconds ?? 0) < 600));
        if (Coding != null)
        {
            var breakMinutes = int.TryParse(Environment.GetEnvironmentVariable("RAPHAEL_CODING_BREAK_MINUTES"), out var bm) ? bm : 90;
            rules.Add(ProactiveWatcher.CodingSessionRule(() => (Coding.CurrentProject, Coding.Elapsed), TimeSpan.FromMinutes(breakMinutes)));
        }
        if (!string.Equals(Environment.GetEnvironmentVariable("RAPHAEL_RECAP"), "off", StringComparison.OrdinalIgnoreCase))
        {
            var recapHour = int.TryParse(Environment.GetEnvironmentVariable("RAPHAEL_RECAP_HOUR"), out var h) ? h : 21;
            rules.Add(ProactiveWatcher.RecapRule(fromHour: recapHour));
        }

        new ProactiveWatcher(rules, s =>
        {
            if (s.Offer != null) { _pendingOffer = s.Offer; _pendingOfferAt = DateTimeOffset.Now; } // "yes" now means "do it"
            if (s.Category != null) ActivityLog.Record(s.Category, s.En);
            return SayAnnouncementAsync(voice, s.Ja, s.En, s.Ttl);
        }).Start();
        Console.WriteLine("Proactive: she may point out low battery, a full disk, Defender off, mail left unread, or a short evening recap (at most 3 times a day).");
    }

    // ---- Google: Gmail and Calendar (one sign-in) -----------------------------------------------------------

    internal static GmailWatcher? Mail;
    internal static CalendarWatcher? Calendar;
    internal static CalendarClient? CalendarWrite;

    private static Task? _signIn;
    private static readonly object SignInLock = new();

    /// <summary>The Google sign-in, run once at a time even if Gmail and Calendar both ask for it.</summary>
    private static Task SignInOnceAsync(GoogleAuth auth, VoiceService voice, bool expired)
    {
        lock (SignInLock)
        {
            if (_signIn == null || _signIn.IsCompleted) _signIn = DoSignInAsync(auth, voice, expired);
            return _signIn;
        }
    }

    private static async Task DoSignInAsync(GoogleAuth auth, VoiceService voice, bool expired)
    {
        Console.WriteLine(expired ? "Google: the saved sign-in expired." : "Google: a sign-in is needed.");
        if (expired)
            await SayUnderLockAsync(voice, "告。Googleの再ログインが必要です。ブラウザで承認してください。", "Notice. Google needs you to sign in again. Approve it in your browser.");
        Console.WriteLine("Google: opening the sign-in page in your browser...");
        await auth.LoginAsync();
        Console.WriteLine("Google: connected.");
        await SayUnderLockAsync(voice, "告。Googleアカウントに接続しました。", "Notice. Your Google account is connected.");
    }

    /// <summary>
    /// Starts the mail and calendar watchers (needs GMAIL_CLIENT_ID / GMAIL_CLIENT_SECRET). RAPHAEL_MAIL=off / RAPHAEL_CALENDAR=off
    /// switch either off. Returns the background task (callers may wait on it briefly, but it is not meant to be awaited in full:
    /// a first-time or expired sign-in opens a browser and can take minutes).
    /// </summary>
    private static Task StartGoogle(VoiceService voice)
    {
        var mailOn = !string.Equals(Environment.GetEnvironmentVariable("RAPHAEL_MAIL"), "off", StringComparison.OrdinalIgnoreCase);
        var calendarOn = !string.Equals(Environment.GetEnvironmentVariable("RAPHAEL_CALENDAR"), "off", StringComparison.OrdinalIgnoreCase);
        if (!mailOn && !calendarOn) return Task.CompletedTask;

        var auth = new GoogleAuth();
        if (!auth.IsConfigured)
        {
            Console.WriteLine("Google: not set up (see the Gmail and Calendar sections of the README). Skipping mail and calendar.");
            return Task.CompletedTask;
        }

        return Task.Run(async () =>
        {
            try
            {
                if (!auth.HasLogin) await SignInOnceAsync(auth, voice, expired: false);
                Func<Task> relogin = () => SignInOnceAsync(auth, voice, expired: true);

                if (mailOn)
                {
                    var gmail = new GmailClient(auth: auth);
                    Mail = new GmailWatcher(gmail.ListUnreadInboxIdsAsync, gmail.GetMailAsync,
                        (mails, startup) => AnnounceMailAsync(mails, startup, voice), onLoginRequired: relogin);
                    Mail.Start();
                    Console.WriteLine("Gmail: watching your inbox for new mail (sender only).");
                }

                if (calendarOn)
                {
                    var calendar = new CalendarClient(auth);
                    CalendarWrite = calendar;
                    Calendar = new CalendarWatcher(calendar.ListAsync, (e, minutes) => AnnounceEventAsync(e, minutes, voice),
                        onLoginRequired: relogin, log: message => Console.WriteLine($"Calendar: {message}"));
                    Calendar.Start();
                    Console.WriteLine("Calendar: warning you 10 minutes before events, and able to add new ones.");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Google: could not start: {ex.Message}");
            }
        });
    }

    /// <summary>Warns that an event is about to start.</summary>
    private static Task AnnounceEventAsync(CalendarEvent e, int minutes, VoiceService voice)
    {
        Console.WriteLine($"[calendar] {e.Title} at {CalendarWatcher.Clock(e.Start)} (in {minutes} min)");
        var ja = minutes <= 1
            ? $"告。まもなく「{e.Title}」が始まります。"
            : $"告。あと{minutes}分で「{e.Title}」が始まります。";
        var en = minutes <= 1
            ? $"Notice. \"{e.Title}\" starts now ({CalendarWatcher.Clock(e.Start)})."
            : $"Notice. \"{e.Title}\" starts in {minutes} minutes ({CalendarWatcher.Clock(e.Start)}).";
        ActivityLog.Record("calendar", e.Title);
        return SayUnderLockAsync(voice, ja, en);
    }
    private static async Task SayUnderLockAsync(VoiceService voice, string ja, string en)
    {
        Console.WriteLine($"[Raphael] {ja}");
        await CommandLock.WaitAsync();
        try { await voice.SayAsync(ja, en); }
        finally { CommandLock.Release(); }
    }

    /// <summary>Says who new mail is from. Never the subject or the text.</summary>
    private static Task AnnounceMailAsync(IReadOnlyList<MailInfo> mails, bool startup, VoiceService voice)
    {
        foreach (var m in mails) Console.WriteLine($"[mail] from {m.Sender}{(startup ? " (already unread at start-up)" : "")}");

        var senders = mails.Select(m => m.Sender).Distinct().ToList();
        string ja, en;
        if (startup)
        {
            var who = senders.Take(3).ToList();
            var jaWho = string.Join("、", who.Select(s => $"{s}さん"));
            var more = senders.Count > who.Count ? "など" : "";
            ja = $"告。未読のメールが{mails.Count}件あります。{jaWho}{more}からです。";
            en = $"Notice. {mails.Count} unread mail{(mails.Count == 1 ? "" : "s")}, from {string.Join(", ", who)}{(senders.Count > who.Count ? " and others" : "")}.";
        }
        else if (senders.Count == 1 && mails.Count == 1)
        {
            ja = $"告。{senders[0]}さんからメールが届きました。";
            en = $"Notice. New mail from {senders[0]}.";
        }
        else
        {
            var who = senders.Take(3).ToList();
            ja = $"告。新着メールが{mails.Count}件届きました。{string.Join("、", who.Select(s => $"{s}さん"))}{(senders.Count > who.Count ? "など" : "")}からです。";
            en = $"Notice. {mails.Count} new mails, from {string.Join(", ", who)}{(senders.Count > who.Count ? " and others" : "")}.";
        }
        ActivityLog.Record("mail", string.Join(", ", senders.Take(3)));
        return SayAnnouncementAsync(voice, ja, en);
    }

    private static string Shorten(string text, int max)
    {
        text = text.Trim();
        return text.Length <= max ? text : text[..max] + "…";
    }

    // ---- power off -------------------------------------------------------------------------------------------

    private static readonly TaskCompletionSource ShutdownRequested = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Asks Raphael to close herself (not the PC). Called by the power_off tool and by the "power off" phrase.</summary>
    internal static void RequestShutdown() => ShutdownRequested.TrySetResult();

    // The whole message must be the request ("power off", "Raphael, please power down"), so "power off my pc" doesn't count.
    private static readonly System.Text.RegularExpressions.Regex PowerOffPhrase = new(
        @"^\s*(?:(?:hey\s+)?(?:raphael(?:[- ]san)?|great sage|sage)[,.:!\s]+)?(?:please\s+)?(?:power\s*(?:off|down)|shut\s*down|shut\s+yourself\s+down|close(?:\s+(?:yourself|raphael|down))?|go\s+to\s+sleep|stand\s+down|quit|log\s*off|turn\s+(?:yourself\s+)?off)(?:\s+(?:for\s+now|for\s+today|now|raphael|please))*[.!\s]*$",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    private static readonly (string Ja, string En)[] Farewells =
    {
        ("告。システムを停止します。またお呼びください。", "Notice. Shutting down. Call me again whenever you need me."),
        ("告。ラファエル、電源を切ります。", "Notice. Raphael, powering off."),
        ("了解しました。停止シーケンスを実行します。", "Understood. Executing the shutdown sequence."),
        ("告。待機を終了します。ご用命の際はいつでもどうぞ。", "Notice. Ending standby. Call on me whenever needed."),
    };

    /// <summary>Says a short goodbye, waits until it has been spoken, then asks the program to close.</summary>
    private static async Task<(string Ja, string En)> PowerOffAsync(VoiceService voice)
    {
        var (ja, en) = Farewells[Random.Shared.Next(Farewells.Length)];
        Console.WriteLine($"[Raphael] {ja}");
        await voice.SayAsync(ja, en);
        RequestShutdown();
        return (ja, en);
    }

    // Said when she is woken from the phone (from standby), so it is clear it was your message that called her.
    private static readonly (string Ja, string En)[] WokenGreetings =
    {
        ("告。遠隔からの呼び出しを確認しました。ラファエル、再起動します。", "Notice. Remote summons confirmed. Raphael, reactivating."),
        ("告。呼び出しを受信しました。待機状態を解除し、稼働を再開します。", "Notice. Summons received. Standby released; resuming operation."),
        ("告。携帯端末からの信号を確認。ラファエル、覚醒しました。", "Notice. Signal from the mobile terminal confirmed. Raphael, awakened."),
        ("告。呼びかけに応じ、システムを再起動しました。ご用件をどうぞ。", "Notice. In response to your call, the system has restarted. Your instructions, please."),
    };

    // Used only when Gemini can't be reached at start-up, so she still doesn't say one fixed line.
    private static readonly (string Ja, string En)[] FallbackGreetings =
    {
        ("ラファエル、起動を完了しました。異常は検出されていません。", "Raphael, startup complete. No anomalies detected."),
        ("ラファエルです。システム正常。ご用件をどうぞ。", "I am Raphael. Systems normal. Your instructions, please."),
        ("ラファエル、状況の確認が終わりました。いつでもどうぞ。", "Raphael here. Status check finished. Whenever you are ready."),
        ("ラファエル、待機状態に入りました。何なりとお申し付けください。", "Raphael, now on standby. Ask whatever you need."),
    };

    /// <summary>What is actually going on right now, for the greeting to (optionally) mention. Best-effort and bounded:
    /// Mail/Calendar may not have finished connecting yet, so a slow or failed read is simply left out.</summary>
    private static async Task<string?> GatherBriefingContextAsync()
    {
        async Task<string?> WithTimeout(Task<string>? task, TimeSpan budget)
        {
            if (task == null) return null;
            try { return await Task.WhenAny(task, Task.Delay(budget)) == task ? await task : null; }
            catch { return null; }
        }

        var mailTask = WithTimeout(Mail?.DescribeAsync(), TimeSpan.FromSeconds(3));
        var calendarTask = WithTimeout(Calendar?.DescribeAsync("today"), TimeSpan.FromSeconds(3));
        var weatherTask = WithTimeout(CommandHandler.Weather.DescribeAsync("today"), TimeSpan.FromSeconds(3));
        await Task.WhenAll(mailTask, calendarTask, weatherTask);

        var parts = new List<string>();
        var mail = await mailTask;
        if (mail != null && !mail.StartsWith("No unread") && !mail.Contains("needs to be signed") && !mail.StartsWith("Could not"))
            parts.Add($"Mail: {mail}");

        var calendar = await calendarTask;
        if (calendar != null && !calendar.StartsWith("Nothing") && !calendar.Contains("needs to be signed") && !calendar.StartsWith("Could not"))
            parts.Add($"Calendar: {calendar}");

        var weather = await weatherTask;
        if (weather != null && !weather.StartsWith("Could not"))
            parts.Add($"Weather: {weather}");

        return parts.Count == 0 ? null : string.Join(" ", parts);
    }

    /// <summary>Greets the user with a freshly generated line, falling back to a random canned one.</summary>
    private static async Task GreetAsync(GeminiClient llm, VoiceService voice)
    {
        voice.SetThinking(true);
        var context = await GatherBriefingContextAsync();
        var text = await llm.GreetAsync(context);
        voice.SetThinking(false);

        if (text != null)
        {
            var (_, ja, en) = ParseBilingual(text);
            Console.WriteLine($"[Raphael] {ja}");
            voice.Say(ja, en);
        }
        else
        {
            var (ja, en) = FallbackGreetings[Random.Shared.Next(FallbackGreetings.Length)];
            Console.WriteLine($"[Raphael] {ja}");
            voice.Say(ja, en);
        }
    }

    /// <summary>Splits a "[HEARD: ...]\nJA: ...\nEN: ..." reply. Falls back to using the whole text for both.</summary>
    private static (string? heard, string ja, string en) ParseBilingual(string text)
    {
        string? heard = null, ja = null, en = null;
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("HEARD:", StringComparison.OrdinalIgnoreCase)) heard = trimmed[6..].Trim();
            else if (trimmed.StartsWith("JA:", StringComparison.OrdinalIgnoreCase)) ja = trimmed[3..].Trim();
            else if (trimmed.StartsWith("EN:", StringComparison.OrdinalIgnoreCase)) en = trimmed[3..].Trim();
        }
        return (heard, ja ?? text, en ?? text);
    }

    /// <summary>Sends the request and runs any tool calls until Raphael gives a final text reply. Callers hold CommandLock.</summary>
    private static async Task<string> RunAsync(GeminiClient llm, string userText, byte[]? audio, string audioMime, bool allowShell)
    {
        llm.AllowShell = allowShell;
        var reply = await llm.SendUserAsync(userText, audio, audioMime);
        var calledTool = false;

        // Loop in case Raphael chains a tool call before giving a final text reply.
        async Task RunToolsAsync()
        {
            for (int i = 0; i < 5 && reply.ToolCalls.Count > 0; i++)
            {
                calledTool = true;
                var results = new List<(ToolCall, string)>();
                foreach (var call in reply.ToolCalls)
                {
                    var result = CommandHandler.Execute(call, allowShell);
                    Console.WriteLine($"[tool:{call.Name}] {result}");
                    if (!ToolFailed(result)) HabitLog.Record(call.Name, call.Input);
                    results.Add((call, result));
                }

                // A plain "it worked" needs no second Gemini round-trip: answer it locally, instantly.
                if (LocalReplies.Enabled && results.Count == 1
                    && LocalReplies.TryBuild(results[0].Item1, results[0].Item2) is { } local)
                {
                    Console.WriteLine("  [instant reply: no second Gemini call needed]");
                    reply = llm.FinishLocally(results, local.Ja, local.En);
                    break;
                }

                reply = await llm.SendToolResultsAsync(results);
            }
        }

        await RunToolsAsync();

        // The small model sometimes answers "done" without having called a tool. If the request sounded like
        // an action, make her try again rather than let a false "done" stand.
        if (!calledTool && LooksLikeAction(ParseBilingual(reply.Text ?? "").heard ?? userText))
        {
            Console.WriteLine("[guard] that sounded like an action but no tool was called; asking her to try again");
            reply = await llm.SendUserAsync(
                "[System note: your last reply called no tool. If the user's request needed an action on the PC "
                + "(opening, playing, pausing, volume, mute, notes, memory), call the matching tool now, and do not say "
                + "it is done unless a tool result confirms it. If it was only a question, answer it again normally.]");
            await RunToolsAsync();
        }

        return llm.WithTitle(reply.Text ?? "JA: 応答がありません。\nEN: No response.");
    }

    private static readonly System.Text.RegularExpressions.Regex ActionWords = new(
        @"\b(volume|mute|unmute|louder|quieter|pause|resume|skip|next|previous|shuffle|repeat|play|open|launch|start|write|note|remember|forget|turn (it )?(up|down)|power (off|down)|shut( yourself)? ?down|goodbye|close( yourself| for now)?|go to sleep|stand down|timers?|remind|reminder|alarm|countdown|cancel|wordle|scan|virus|malware|e-?mails?|gmail|inbox|calendar|schedule|agenda|meetings?|appointments?|mark (it |this )?down|block out|recap|weather|rain|temperature|forecast|screen|git|repo|commit(ted)?|uncommitted|pushed|code|compile|run (it|that|this)|delete|remove|look (it |that |this )?up|google|search for|latest news|calculate|compute|convert|pdf|document|summarize)\b",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>A rough check for "the user asked for something to be done" (used only to catch a skipped tool call).</summary>
    private static bool LooksLikeAction(string request) => !CapabilityQuestion.IsMatch(request) && ActionWords.IsMatch(request);

    /// <summary>"Are you capable of...", "is it possible to...": she answers and asks first, so no tool call is expected.</summary>
    private static readonly System.Text.RegularExpressions.Regex CapabilityQuestion = new(
        @"\b(are you (capable|able)|is it possible|is there a way|do you know how|would it be possible|are you (even )?(capable|able))\b",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>
    /// A command from the phone: same brain and memory, but without the shell tool. The answer goes back
    /// to the chat and is also spoken (with the orb and subtitle) on the PC.
    /// </summary>
    private static async Task<string> HandleRemoteAsync(string text, byte[]? audio, string audioMime, GeminiClient llm, VoiceService voice)
    {
        await CommandLock.WaitAsync();
        try
        {
            if (audio == null && PowerOffPhrase.IsMatch(text))
            {
                var (farewellJa, farewellEn) = await PowerOffAsync(voice);
                return $"{farewellJa}\n{farewellEn}";
            }

            var prepared = Prepare(text, audio, out var declinedReply);
            if (declinedReply != null)
            {
                voice.Say(declinedReply.Value.Ja, declinedReply.Value.En);
                return $"{declinedReply.Value.Ja}\n{declinedReply.Value.En}";
            }

            voice.SetThinking(true);
            var raw = await RunAsync(llm, prepared, audio, audioMime, allowShell: false);
            var (heard, ja, en) = ParseBilingual(raw);
            if (heard != null) Console.WriteLine($"[heard] {heard}");
            Console.WriteLine($"[Raphael] {ja}");
            var speech = voice.SayAsync(ja, en);
            if (ShutdownRequested.Task.IsCompleted) await speech; // she asked to power off: finish the goodbye first

            return (heard != null ? $"🎤 {heard}\n\n" : "") + (ja == en ? ja : $"{ja}\n{en}");
        }
        finally
        {
            llm.ForgetAudio();
            llm.AllowShell = true;
            voice.SetThinking(false);
            CommandLock.Release();
            LearnInBackground(llm);
        }
    }

    private static async Task HandleCommand(string userText, byte[]? audio, GeminiClient llm, VoiceService voice)
    {
        await CommandLock.WaitAsync();
        try
        {
            if (audio == null && PowerOffPhrase.IsMatch(userText))
            {
                await PowerOffAsync(voice);
                return;
            }

            var prepared = Prepare(userText, audio, out var declinedReply);
            if (declinedReply != null)
            {
                Console.WriteLine($"[Raphael] {declinedReply.Value.Ja}");
                voice.Say(declinedReply.Value.Ja, declinedReply.Value.En);
                return;
            }

            voice.SetThinking(true);
            var text = await RunAsync(llm, prepared, audio, "audio/wav", allowShell: true);
            var (heard, ja, en) = ParseBilingual(text);
            if (heard != null) Console.WriteLine($"[heard] {heard}");
            Console.WriteLine($"[Raphael] {ja}");
            var speech = voice.SayAsync(ja, en);
            if (ShutdownRequested.Task.IsCompleted) await speech; // she asked to power off: finish the goodbye first
        }
        finally
        {
            llm.ForgetAudio();
            voice.SetThinking(false);
            CommandLock.Release();
            LearnInBackground(llm);
        }
    }

    // ---- offers, routines and what she learns about you ------------------------------------------------------

    private static Offer? _pendingOffer;
    private static DateTimeOffset _pendingOfferAt;

    private static readonly System.Text.RegularExpressions.Regex Yes = new(
        @"^\s*(?:(?:yes|yeah|yep|yup|sure|ok(?:ay)?|please|go ahead|do it|proceed|execute|affirmative|はい|お願い(?:します)?)[\s,.!]*)+$",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    private static readonly System.Text.RegularExpressions.Regex No = new(
        @"^\s*(?:no|nope|nah|not now|cancel|never mind|skip(?: it)?|いいえ|やめて)[\s,.!]*$",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>
    /// Handles what she offered a moment ago ("You usually open Spotify around now. Proceed?"): a yes becomes an instruction to do
    /// it, a no cancels it, and anything else simply lets the offer lapse. Also notes ordinary requests in the journal.
    /// </summary>
    private static string Prepare(string text, byte[]? audio, out (string Ja, string En)? declined)
    {
        declined = null;
        var offer = _pendingOffer;
        _pendingOffer = null; // whatever you answer, the offer is closed
        if (audio != null) return text;

        var isYes = Yes.IsMatch(text);
        var isNo = No.IsMatch(text);
        if (!isYes && !isNo) PersonalityStore.Note(text);

        if (offer == null || DateTimeOffset.Now - _pendingOfferAt > TimeSpan.FromMinutes(10)) return text;
        if (isNo) { declined = ("了解しました。実行を取りやめます。", "Understood. Cancelled."); return text; }
        if (!isYes) return text;

        var args = offer.Args == null ? "" : $" with {offer.Args.ToJsonString()}";
        return $"Yes, go ahead. (You just offered to {offer.Description}. Call the {offer.Tool} tool now{args}, then report the result briefly.)";
    }

    /// <summary>A tool result that says it did not work: not worth remembering as a routine.</summary>
    private static bool ToolFailed(string result) =>
        result.StartsWith("Execution failed", StringComparison.OrdinalIgnoreCase)
        || result.Contains("No installed", StringComparison.OrdinalIgnoreCase)
        || result.Contains("not found", StringComparison.OrdinalIgnoreCase)
        || result.StartsWith("Could not", StringComparison.OrdinalIgnoreCase)
        || result.Contains("not connected", StringComparison.OrdinalIgnoreCase);

    /// <summary>About once a day, after enough requests, she rewrites her short notes on how you talk (one small Gemini call).</summary>
    private static void LearnInBackground(GeminiClient llm)
    {
        if (string.Equals(Environment.GetEnvironmentVariable("RAPHAEL_LEARN"), "off", StringComparison.OrdinalIgnoreCase)) return;
        _ = Task.Run(async () =>
        {
            try { if (await PersonalityStore.MaybeUpdateAsync(llm.SideQuestionAsync)) Console.WriteLine("[learned] updated her notes on how you talk to her."); }
            catch { }
        });
    }
}



