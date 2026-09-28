using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace Raphael;

/// <summary>What is in front of you right now, and how long since you last touched the keyboard or mouse.</summary>
public sealed record ForegroundInfo(string Process, string Title, bool FullScreen, int IdleSeconds);

/// <summary>
/// Knows whether you are busy (a full-screen game or video, or a call in front of you) so she doesn't talk over it: while
/// busy, her announcements are held and read out together afterwards. It only reads the front window's program name,
/// title and size, and how long you have been idle. It never looks inside any window.
/// </summary>
public sealed class PresenceMonitor : IDisposable
{
    private static readonly HashSet<string> CallPrograms = new(StringComparer.OrdinalIgnoreCase)
        { "zoom", "teams", "ms-teams", "webex", "webexmta", "skype", "slack huddle" };

    // Google Meet / Zoom / Teams in a browser tab: the front window's title says so.
    private static readonly Regex CallTitle = new(@"^Meet\s[-–]\s|Google Meet|Zoom Meeting|\bMeeting\b.*\bMicrosoft Teams\b|\|\s*Microsoft Teams",
        RegexOptions.IgnoreCase);

    // Never count these as "a game": the desktop, the taskbar, the lock screen and Raphael's own overlays.
    private static readonly HashSet<string> NeverBusy = new(StringComparer.OrdinalIgnoreCase)
        { "explorer", "searchhost", "startmenuexperiencehost", "shellexperiencehost", "lockapp", "textinputhost", "Raphael" };

    private const int ReturnAfterIdleSeconds = 30 * 60;

    private readonly Func<ForegroundInfo?> _read;
    private readonly TimeSpan _interval;
    private readonly Func<Task>? _onFree;
    private readonly Func<TimeSpan, Task>? _onReturn;
    private readonly CancellationTokenSource _cts = new();
    private string? _candidate;
    private int _candidateCount;
    private int _previousIdle;

    /// <summary>Why she is holding her tongue ("full-screen: game.exe", "a call: zoom"), or null when you are free.</summary>
    public string? BusyReason { get; private set; }
    public bool IsBusy => BusyReason != null;

    /// <summary>Seconds since you last touched the keyboard or mouse (as of the last look).</summary>
    public int LastIdleSeconds { get; private set; }

    /// <param name="onFree">Called when a busy spell ends.</param>
    /// <param name="onReturn">Called when you come back after a long idle spell, with how long it was.</param>
    public PresenceMonitor(Func<ForegroundInfo?>? read = null, TimeSpan? interval = null,
        Func<Task>? onFree = null, Func<TimeSpan, Task>? onReturn = null)
    {
        _read = read ?? ReadForeground;
        _interval = interval ?? TimeSpan.FromSeconds(3);
        _onFree = onFree;
        _onReturn = onReturn;
    }

    public void Start() => _ = Task.Run(LoopAsync);

    private async Task LoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            try { await StepAsync(); } catch { /* try again next round */ }
            try { await Task.Delay(_interval, _cts.Token); } catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>One look at the desktop. Public so it can be tested without a timer.</summary>
    public async Task StepAsync()
    {
        var info = _read();
        var reason = info == null ? null : Classify(info);

        // A change only counts once it has been seen twice in a row, so alt-tabbing past a game doesn't flip her back and forth.
        if (reason == BusyReason) { _candidate = reason; _candidateCount = 0; }
        else if (reason == _candidate) _candidateCount++;
        else { _candidate = reason; _candidateCount = 1; }

        if (reason != BusyReason && _candidateCount >= 2)
        {
            var wasBusy = BusyReason != null;
            BusyReason = reason;
            _candidateCount = 0;
            if (wasBusy && reason == null && _onFree != null) await _onFree();
        }

        // Coming back after a long time away (the idle counter drops back to nearly zero).
        if (info != null)
        {
            if (_previousIdle >= ReturnAfterIdleSeconds && info.IdleSeconds < _previousIdle && info.IdleSeconds < 60 && _onReturn != null)
                await _onReturn(TimeSpan.FromSeconds(_previousIdle));
            _previousIdle = info.IdleSeconds;
            LastIdleSeconds = info.IdleSeconds;
        }
    }

    /// <summary>The reason she should stay quiet for this foreground window, or null if she may speak.</summary>
    public static string? Classify(ForegroundInfo info)
    {
        if (NeverBusy.Contains(info.Process)) return null;
        if (CallPrograms.Contains(info.Process) || CallTitle.IsMatch(info.Title)) return $"a call ({info.Process})";
        if (info.FullScreen) return $"full-screen ({info.Process})";
        return null;
    }

    // ---- reading the desktop (Windows) ------------------------------------------------------------------

    /// <summary>The front window right now (process, title, whether it's full screen, idle seconds). Public so other
    /// components (e.g. CodingSessionTracker) can read it without needing their own PresenceMonitor instance.</summary>
    public static ForegroundInfo? ReadForeground()
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return null;

        var title = new StringBuilder(256);
        GetWindowText(hwnd, title, title.Capacity);

        GetWindowThreadProcessId(hwnd, out var pid);
        string process;
        try { process = Process.GetProcessById((int)pid).ProcessName; } catch { return null; }

        // Full screen = it covers its whole monitor and has no title bar (a maximised normal window has one).
        var fullScreen = false;
        if (GetWindowRect(hwnd, out var rect))
        {
            var screen = System.Windows.Forms.Screen.FromHandle(hwnd).Bounds;
            var covers = rect.Left <= screen.Left && rect.Top <= screen.Top && rect.Right >= screen.Right && rect.Bottom >= screen.Bottom;
            var hasCaption = (GetWindowLong(hwnd, GWL_STYLE) & WS_CAPTION) == WS_CAPTION;
            var className = new StringBuilder(64);
            GetClassName(hwnd, className, className.Capacity);
            var isDesktop = className.ToString() is "Progman" or "WorkerW" or "Shell_TrayWnd";
            fullScreen = covers && !hasCaption && !isDesktop;
        }

        return new ForegroundInfo(process, title.ToString(), fullScreen, IdleSeconds());
    }

    private static int IdleSeconds()
    {
        var last = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
        if (!GetLastInputInfo(ref last)) return 0;
        return (int)((uint)Environment.TickCount - last.dwTime) / 1000;
    }

    public void Dispose() => _cts.Cancel();

    private const int GWL_STYLE = -16;
    private const int WS_CAPTION = 0xC00000;

    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct LASTINPUTINFO { public uint cbSize; public uint dwTime; }

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int count);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hWnd, StringBuilder text, int count);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hWnd, int index);
    [DllImport("user32.dll")] private static extern bool GetLastInputInfo(ref LASTINPUTINFO info);
}


