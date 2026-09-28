using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows.Automation;

namespace Raphael;

/// <summary>One open browser tab: an id that stays the same while the tab lives, its cleaned-up title, and whether you are looking at it.</summary>
public sealed record TabInfo(string Id, string Title, bool InView);

/// <summary>
/// Something a tab wants your attention for: an unread counter ("(2) Messenger"), or a title such as
/// "Nicole Louise messaged you". <see cref="Sender"/> is filled in when the title says who it is from.
/// </summary>
public sealed record TabAlert(string Site, int Count, string? Text = null, string? Sender = null);

/// <summary>
/// Watches the tab titles of your browser. Many sites tell you about new messages in the title of a background tab
/// ("(1) Messenger", "Inbox (3) - Gmail", "Nicole Louise messaged you"), even when they send no popup, so this works
/// with browser notifications turned off. It reads the tab strip through Windows' accessibility API (Chrome, Edge,
/// Brave, Opera, Vivaldi), including tabs in the background.
/// </summary>
public sealed class TabTitleWatcher : IDisposable
{
    private static readonly string[] Browsers = { "chrome", "msedge", "brave", "opera", "vivaldi" };

    // Chrome adds hover-card text to a tab's accessible name: "Title - Audio playing - Memory usage - 318 MB".
    private static readonly Regex ChromeSuffix = new(
        @"\s+-\s+(?:Memory usage\s+-\s+[\d.,]+\s*[KMG]?B|Audio playing|Audio muted|Muted|Camera recording|Microphone recording|Recording|Sharing your screen|Using Bluetooth|Using USB|Crashed|Alert.*)\s*$",
        RegexOptions.IgnoreCase);

    private static readonly Regex Counter = new(@"\((\d{1,3})\+?\)");

    // Titles a site uses when something new has arrived for you.
    private static readonly Regex AttentionTitle = new(
        @"\b(messaged you|sent you|new message|mentioned you|replied to you|tagged you|is calling you|calling you|wants to chat)\b",
        RegexOptions.IgnoreCase);

    private static readonly Regex SenderOf = new(
        @"^(?<name>.+?)\s+(?:messaged you|sent you|mentioned you|replied to you|tagged you|is calling you|wants to chat)\b",
        RegexOptions.IgnoreCase);

    private sealed class TabState
    {
        public bool Announced;                        // she has already told you about this tab's current unread
        public int Count;                             // the largest counter she has told you about
        public readonly HashSet<string> Texts = new(); // attention titles she has already told you about
        public DateTime LastAttention;                // when the tab last asked for attention
        public DateTime? CounterSince;                // a counter she is still waiting to announce (see _settle)
    }

    private readonly NotificationRules _rules;
    private readonly Func<IReadOnlyList<TabInfo>> _scan;
    private readonly TimeSpan _interval;
    private readonly TimeSpan _settle;
    private readonly TimeSpan _reset;
    private readonly Func<IReadOnlyList<TabAlert>, bool, Task> _announce;
    private readonly CancellationTokenSource _cts = new();
    private readonly Dictionary<string, TabState> _tabs = new();

    /// <param name="scan">Reads the open tabs. Defaults to the real browser; tests can supply their own.</param>
    /// <param name="announce">Called with the alerts and whether this is the start-up summary.</param>
    /// <param name="settle">How long a bare counter waits, in case the title switches to "X messaged you" (which says who).</param>
    /// <param name="reset">How long a tab must stay quiet before its next message counts as new (a flashing title must not repeat itself).</param>
    public TabTitleWatcher(NotificationRules rules, Func<IReadOnlyList<TabAlert>, bool, Task> announce,
        Func<IReadOnlyList<TabInfo>>? scan = null, TimeSpan? interval = null, TimeSpan? settle = null, TimeSpan? reset = null)
    {
        _rules = rules;
        _announce = announce;
        _scan = scan ?? Scan;
        _interval = interval ?? TimeSpan.FromSeconds(3);
        _settle = settle ?? TimeSpan.FromSeconds(3);
        _reset = reset ?? TimeSpan.FromSeconds(12);
    }

    /// <summary>Splits "(3) Messenger" into the site ("Messenger") and its counter (3). Count is 0 if there is none.</summary>
    public static (string Site, int Count) ParseCounter(string title)
    {
        var m = Counter.Match(title);
        if (!m.Success) return (title.Trim(), 0);

        var site = Regex.Replace(title.Remove(m.Index, m.Length), @"\s{2,}", " ").Trim(' ', '-', '|', ':');
        return (site, int.Parse(m.Groups[1].Value));
    }

    /// <summary>True for a title like "Nicole Louise messaged you"; <paramref name="sender"/> is the name if the title has one.</summary>
    public static bool IsAttentionTitle(string title, out string? sender)
    {
        sender = null;
        if (!AttentionTitle.IsMatch(title)) return false;
        var m = SenderOf.Match(title);
        if (m.Success) sender = m.Groups["name"].Value.Trim();
        return true;
    }

    /// <summary>Removes the extra text Chrome tacks onto a tab's accessible name.</summary>
    public static string CleanTitle(string name)
    {
        string previous;
        do { previous = name; name = ChromeSuffix.Replace(name, ""); } while (name != previous);
        return name.Trim();
    }

    public void Start() => _ = Task.Run(LoopAsync);

    private async Task LoopAsync()
    {
        var first = true;
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                var tabs = await Task.Run(_scan);
                var startup = first;
                first = false;
                var alerts = Evaluate(tabs, DateTime.Now, startup);
                if (alerts.Count > 0) await _announce(alerts, startup);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The browser can be busy or closing; try again on the next round.
            }

            try { await Task.Delay(_interval, _cts.Token); } catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>Works out what, if anything, is new since the last scan.</summary>
    private List<TabAlert> Evaluate(IReadOnlyList<TabInfo> tabs, DateTime now, bool first)
    {
        var alerts = new List<TabAlert>();

        foreach (var tab in tabs)
        {
            if (!_tabs.TryGetValue(tab.Id, out var state)) _tabs[tab.Id] = state = new TabState();

            var (site, count) = ParseCounter(tab.Title);
            var isAttention = IsAttentionTitle(tab.Title, out var sender);
            var wantsAttention = !tab.InView && !_rules.IsBlocked(tab.Title) && (count > 0 || isAttention);

            if (wantsAttention)
            {
                state.LastAttention = now;

                if (isAttention)
                {
                    // "Nicole Louise messaged you": says who. Announced once per distinct title.
                    if (state.Texts.Add(tab.Title))
                    {
                        alerts.Add(new TabAlert(site, count, tab.Title, sender));
                        state.Announced = true;
                        state.CounterSince = null;
                        state.Count = Math.Max(state.Count, count);
                    }
                }
                else if (!state.Announced)
                {
                    // A bare counter: give the title a moment to switch to "X messaged you" before settling for the count.
                    state.CounterSince ??= now;
                    if (first || now - state.CounterSince >= _settle)
                    {
                        alerts.Add(new TabAlert(site, count));
                        state.Announced = true;
                        state.Count = count;
                        state.CounterSince = null;
                    }
                }
                else if (count > state.Count)
                {
                    alerts.Add(new TabAlert(site, count));   // more unread than she has told you about
                    state.Count = count;
                }
            }
            else if (now - state.LastAttention > _reset)
            {
                // Quiet for a while (or you have looked at it): its next message is a new one.
                state.Announced = false;
                state.Count = 0;
                state.Texts.Clear();
                state.CounterSince = null;
            }
        }

        // Forget tabs that were closed.
        var open = tabs.Select(t => t.Id).ToHashSet();
        foreach (var gone in _tabs.Keys.Where(k => !open.Contains(k)).ToList()) _tabs.Remove(gone);

        return alerts;
    }

    // ---- reading the real browser -----------------------------------------------------------------------

    private static readonly Dictionary<int, string> ProcessNames = new();

    /// <summary>The tabs of every open Chromium-based browser window, as Windows' accessibility API reports them.</summary>
    public static IReadOnlyList<TabInfo> Scan()
    {
        var found = new List<TabInfo>();
        var foreground = GetForegroundWindow();
        var windows = AutomationElement.RootElement.FindAll(TreeScope.Children,
            new PropertyCondition(AutomationElement.ClassNameProperty, "Chrome_WidgetWin_1"));

        foreach (AutomationElement window in windows)
        {
            try
            {
                if (!IsBrowser(window.Current.ProcessId)) continue;
                var inFront = new IntPtr(window.Current.NativeWindowHandle) == foreground;

                var tabs = window.FindAll(TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TabItem));
                foreach (AutomationElement tab in tabs)
                {
                    var title = CleanTitle(tab.Current.Name ?? "");
                    if (title.Length == 0) continue;

                    var selected = tab.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var pattern)
                        && ((SelectionItemPattern)pattern).Current.IsSelected;
                    // The runtime id identifies this tab for as long as it exists, whatever its title says.
                    var id = $"{window.Current.NativeWindowHandle}:{string.Join('.', tab.GetRuntimeId())}";
                    found.Add(new TabInfo(id, title, selected && inFront));
                }
            }
            catch { /* a window that closes mid-scan is simply skipped */ }
        }
        return found;
    }

    private static bool IsBrowser(int processId)
    {
        lock (ProcessNames)
        {
            if (!ProcessNames.TryGetValue(processId, out var name))
            {
                try { name = Process.GetProcessById(processId).ProcessName.ToLowerInvariant(); } catch { name = ""; }
                ProcessNames[processId] = name;
            }
            return Browsers.Contains(name);
        }
    }

    /// <summary>What the browser's tabs are asking attention for right now (whether or not you are looking), for the check_notifications tool.</summary>
    public static string DescribeUnread()
    {
        try
        {
            var items = new List<string>();
            foreach (var tab in Scan())
            {
                var (site, count) = ParseCounter(tab.Title);
                if (IsAttentionTitle(tab.Title, out _)) items.Add($"\"{tab.Title}\"");
                else if (count > 0) items.Add($"{site}: {count} unread");
            }
            return items.Count == 0 ? "No browser tab is asking for attention." : "Browser tabs asking for attention: " + string.Join(", ", items) + ".";
        }
        catch
        {
            return "Could not read the browser's tabs.";
        }
    }

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();

    public void Dispose() => _cts.Cancel();
}
