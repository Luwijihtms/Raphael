using System.Text.RegularExpressions;
using Windows.UI.Notifications;
using Windows.UI.Notifications.Management;

namespace Raphael;

/// <summary>A notification Windows is showing, or is holding in the notification centre.</summary>
public sealed record Notice(uint Id, string App, string Title, string Body, string? Site, DateTimeOffset Time)
{
    /// <summary>Where it came from: the website for a browser notification, otherwise the app.</summary>
    public string Source => Site ?? App;
}

/// <summary>
/// Which notifications are worth announcing. Sensible defaults (browsers and chat apps, plus squad.dinoxxit.com),
/// extended by %LOCALAPPDATA%\Raphael\notifications.txt, which is created on first run with instructions.
/// </summary>
public sealed class NotificationRules
{
    private static readonly string Path = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Raphael", "notifications.txt");

    private const string Template = """
        # Which notifications Raphael announces. Lines starting with # are ignored. Restart her after editing.
        #
        # watch: TEXT   announce a notification if its app, its website, or its title contains TEXT.
        # block: TEXT   never announce one containing TEXT (checked first).
        # name: TEXT = HOW SHE SAYS IT   how she pronounces a source out loud.
        # body: on      also show the message text in the subtitle (off by default, so private messages stay off the screen).
        #
        # Examples:
        # watch: my-other-site.com
        # block: Downloads
        # name: squad.dinoxxit.com = スクワッド
        """;

    public List<string> Watch { get; } = new()
    {
        "chrome", "msedge", "edge", "firefox", "opera", "brave", "vivaldi",
        "messenger", "discord", "telegram", "whatsapp", "slack", "teams", "viber", "line",
        "squad.dinoxxit.com"
    };

    public List<string> Block { get; } = new();

    public Dictionary<string, string> Names { get; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["squad.dinoxxit.com"] = "スクワッド",
        ["squad"] = "スクワッド",
        ["messenger"] = "メッセンジャー",
        ["gmail"] = "ジーメール",
        ["facebook"] = "フェイスブック",
        ["instagram"] = "インスタグラム",
        ["discord"] = "ディスコード",
        ["telegram"] = "テレグラム",
        ["whatsapp"] = "ワッツアップ",
        ["slack"] = "スラック",
        ["teams"] = "チームズ",
        ["chrome"] = "ブラウザ",
        ["edge"] = "ブラウザ",
        ["firefox"] = "ブラウザ",
    };

    public bool ShowBody { get; private set; }

    public static NotificationRules Load()
    {
        var rules = new NotificationRules();
        try
        {
            if (!File.Exists(Path))
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
                File.WriteAllText(Path, Template);
                return rules;
            }

            foreach (var raw in File.ReadAllLines(Path))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith('#')) continue;

                var colon = line.IndexOf(':');
                if (colon < 0) continue;
                var key = line[..colon].Trim().ToLowerInvariant();
                var value = line[(colon + 1)..].Trim();
                if (value.Length == 0) continue;

                switch (key)
                {
                    case "watch": rules.Watch.Add(value); break;
                    case "block": rules.Block.Add(value); break;
                    case "body": rules.ShowBody = value.Equals("on", StringComparison.OrdinalIgnoreCase); break;
                    case "name":
                        var eq = value.IndexOf('=');
                        if (eq > 0) rules.Names[value[..eq].Trim()] = value[(eq + 1)..].Trim();
                        break;
                }
            }
        }
        catch { /* a bad file just leaves the defaults */ }
        return rules;
    }

    private static bool Has(string haystack, string needle) => haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);

    public bool IsRelevant(Notice n)
    {
        var text = $"{n.App}\n{n.Site}\n{n.Title}";
        if (Block.Any(b => Has(text, b))) return false;
        return Watch.Any(w => Has(text, w));
    }

    /// <summary>How to say the source aloud ("スクワッド" for the site), falling back to its name as written.</summary>
    public string SpokenName(Notice n)
    {
        foreach (var (key, spoken) in Names)
        {
            if (Has(n.Source, key) || Has(n.App, key)) return spoken;
        }
        return n.Source;
    }

    // The browsers' own names say nothing about which site a tab is.
    private static readonly string[] BrowserNames = { "chrome", "edge", "firefox", "msedge", "opera", "brave", "vivaldi" };

    private string? KnownSite(string tabTitle) =>
        Names.Keys.FirstOrDefault(k => !BrowserNames.Contains(k, StringComparer.OrdinalIgnoreCase) && Has(tabTitle, k));

    /// <summary>A short name for a browser tab, for the subtitle: "Messenger" for "(1) Messenger", else the title itself.</summary>
    public string DisplayForTab(string tabTitle)
    {
        var known = KnownSite(tabTitle);
        if (known != null) return char.ToUpperInvariant(known[0]) + known[1..];
        return tabTitle.Length <= 40 ? tabTitle : tabTitle[..40] + "…";
    }

    /// <summary>How to say a browser tab's name aloud.</summary>
    public string SpokenForTab(string tabTitle)
    {
        var known = KnownSite(tabTitle);
        return known != null ? Names[known] : (tabTitle.Length <= 30 ? tabTitle : tabTitle[..30]);
    }

    public bool IsBlocked(string text) => Block.Any(b => Has(text, b));
}

/// <summary>
/// Watches Windows' notification centre and reports new notifications (a browser site's message, Messenger, ...) through a
/// callback. It reads the same notifications you would see pop up, so a site only shows up here if it sends browser
/// notifications and Chrome is allowed to show them.
/// </summary>
public sealed class NotificationWatcher : IDisposable
{
    private static readonly Regex HostOnly = new(@"^(?:https?://)?[a-z0-9-]+(?:\.[a-z0-9-]+)+/?$", RegexOptions.IgnoreCase);

    private readonly NotificationRules _rules;
    private readonly Func<IReadOnlyList<Notice>, bool, Task> _announce;
    private readonly CancellationTokenSource _cts = new();
    private readonly HashSet<uint> _seen = new();

    /// <param name="announce">Called with the notices and whether this is the start-up summary of unread ones.</param>
    public NotificationWatcher(NotificationRules rules, Func<IReadOnlyList<Notice>, bool, Task> announce)
    {
        _rules = rules;
        _announce = announce;
    }

    public static bool CanRead()
    {
        try { return UserNotificationListener.Current.GetAccessStatus() == UserNotificationListenerAccessStatus.Allowed; }
        catch { return false; }
    }

    public void Start() => _ = Task.Run(LoopAsync);

    private async Task LoopAsync()
    {
        var first = true;
        var pending = new List<Notice>();
        var lastNew = DateTime.MinValue;

        while (!_cts.IsCancellationRequested)
        {
            try
            {
                var current = await ReadAsync();

                if (first)
                {
                    // What is already waiting is summarised once; only genuinely new ones are announced from now on.
                    first = false;
                    foreach (var n in current) _seen.Add(n.Id);
                    var unread = current.Where(n => _rules.IsRelevant(n) && DateTimeOffset.Now - n.Time < TimeSpan.FromHours(24)).ToList();
                    if (unread.Count > 0) await _announce(unread, true);
                }
                else
                {
                    foreach (var n in current)
                    {
                        if (_seen.Add(n.Id) && _rules.IsRelevant(n))
                        {
                            pending.Add(n);
                            lastNew = DateTime.Now;
                        }
                    }

                    // Wait a moment so a burst of notifications becomes one announcement.
                    if (pending.Count > 0 && DateTime.Now - lastNew > TimeSpan.FromSeconds(2.5))
                    {
                        var batch = pending.ToList();
                        pending.Clear();
                        await _announce(batch, false);
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Windows can refuse a read now and then; try again on the next round.
            }

            try { await Task.Delay(TimeSpan.FromSeconds(3), _cts.Token); } catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>Every toast notification currently in the notification centre.</summary>
    public static async Task<IReadOnlyList<Notice>> ReadAsync()
    {
        var toasts = await UserNotificationListener.Current.GetNotificationsAsync(NotificationKinds.Toast);
        var notices = new List<Notice>();
        foreach (var toast in toasts)
        {
            try
            {
                var binding = toast.Notification.Visual.GetBinding(KnownNotificationBindings.ToastGeneric);
                var texts = binding?.GetTextElements().Select(t => t.Text).Where(t => !string.IsNullOrWhiteSpace(t)).ToList() ?? new List<string>();

                // Chrome puts the website's address on the last line of a web notification.
                string? site = texts.Count > 1 && HostOnly.IsMatch(texts[^1].Trim()) ? texts[^1].Trim().TrimEnd('/') : null;
                var bodyLines = site != null ? texts.Skip(1).SkipLast(1) : texts.Skip(1);

                notices.Add(new Notice(toast.Id, toast.AppInfo.DisplayInfo.DisplayName, texts.FirstOrDefault() ?? "",
                    string.Join(" ", bodyLines), site, toast.CreationTime));
            }
            catch { /* one odd notification shouldn't hide the rest */ }
        }
        return notices;
    }

    /// <summary>A short list of what is waiting (sender and title only, never the message text), for the check_notifications tool.</summary>
    public static async Task<string> DescribeAsync()
    {
        // Unread counters in the browser's tab titles (these exist even when popups are turned off).
        var tabs = TabTitleWatcher.DescribeUnread();

        if (!CanRead()) return $"{tabs} Raphael is not allowed to read Windows notifications.";
        var notices = await ReadAsync();
        if (notices.Count == 0) return $"{tabs} There are no Windows notifications waiting.";

        var lines = notices.OrderByDescending(n => n.Time).Take(10)
            .Select(n => $"{n.Source}{(string.IsNullOrWhiteSpace(n.Title) ? "" : ": " + n.Title)}");
        return $"{tabs} {notices.Count} Windows notification(s) waiting. Most recent first: {string.Join(" | ", lines)}";
    }

    public void Dispose() => _cts.Cancel();
}
