using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Raphael;

/// <summary>
/// Decides how Raphael shows herself. By default she opens a window (picture, replies, typing box, her orb). The old
/// console look (a text window plus the big orb and subtitle bar over the screen) is still there for testing:
/// start her with <c>--console</c>, set <c>RAPHAEL_UI=classic</c>, or pipe commands into her.
/// The program is a Windows (no console) program, so the console is only attached or created when classic mode is wanted.
/// </summary>
public static class UiMode
{
    /// <summary>True when the chat window is used instead of the console.</summary>
    public static bool Chat { get; private set; }

    private const int ATTACH_PARENT_PROCESS = -1;
    private const int STD_INPUT_HANDLE = -10;

    [DllImport("kernel32.dll")] private static extern bool AttachConsole(int processId);
    [DllImport("kernel32.dll")] private static extern bool AllocConsole();
    [DllImport("kernel32.dll")] private static extern IntPtr GetStdHandle(int handle);
    [DllImport("kernel32.dll")] private static extern uint GetFileType(IntPtr handle);

    public static void Decide(string[] args)
    {
        bool Has(string a) => args.Contains(a, StringComparer.OrdinalIgnoreCase);

        var standby = Has("--standby");
        var classic = Has("--console") || Has("--mic-test")
                      || string.Equals(Environment.GetEnvironmentVariable("RAPHAEL_UI"), "classic", StringComparison.OrdinalIgnoreCase)
                      || InputIsPiped();

        Chat = !classic && !standby;
        if (classic && !standby && !AttachConsole(ATTACH_PARENT_PROCESS)) AllocConsole();

        if (Chat)
        {
            // There is no console to show a crash in, so write it to her log.
            AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            {
                try { File.AppendAllText(SessionLog.Path, $"[crash] {e.ExceptionObject}{Environment.NewLine}"); } catch { }
            };
        }
    }

    /// <summary>Commands piped in (for testing) are read from the console, so that counts as classic mode.</summary>
    private static bool InputIsPiped()
    {
        var type = GetFileType(GetStdHandle(STD_INPUT_HANDLE));
        return type == 1 || type == 3; // a file or a pipe
    }

    /// <summary>A message the user must see even though there is no console (a missing key, for example).</summary>
    public static void Report(string text)
    {
        if (Chat) MessageBox.Show(text, "Raphael", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        else Console.WriteLine(text);
    }
}
