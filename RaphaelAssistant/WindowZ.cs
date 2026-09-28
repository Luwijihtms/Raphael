using System.Runtime.InteropServices;

namespace Raphael;

/// <summary>
/// Keeps Raphael's overlay windows above other windows. A game that goes full-screen after the overlay
/// appeared can take the top spot, so the overlays call this repeatedly while they are visible.
/// This works over windowed and borderless-fullscreen games; it cannot draw over a game running in
/// exclusive fullscreen, because that mode bypasses the desktop compositor.
/// </summary>
internal static class WindowZ
{
    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_SHOWWINDOW = 0x0040;

    public static void KeepOnTop(IntPtr hwnd)
    {
        if (hwnd != IntPtr.Zero)
            SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW);
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);
}
