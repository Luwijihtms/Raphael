using System.Text;

namespace Raphael;

/// <summary>
/// Copies everything Raphael prints to %LOCALAPPDATA%\Raphael\raphael.log (with times), so it can be
/// read after the console window has closed. The previous log is kept as raphael.old.log once it
/// passes 1 MB. The log contains what you said and what she answered, but never API keys or tokens.
/// </summary>
public static class SessionLog
{
    private const long MaxBytes = 1_000_000;

    public static string Path { get; } = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Raphael", "raphael.log");

    public static void Start()
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);

            if (File.Exists(Path) && new FileInfo(Path).Length > MaxBytes)
            {
                var old = System.IO.Path.ChangeExtension(Path, ".old.log");
                File.Move(Path, old, overwrite: true);
            }

            var file = new StreamWriter(new FileStream(Path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite), new UTF8Encoding(false))
            {
                AutoFlush = true
            };
            file.WriteLine($"=== session started {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===");

            Console.SetOut(new TeeWriter(Console.Out, file));
        }
        catch
        {
            // Logging is a convenience: never stop Raphael from starting because of it.
        }
    }

    private sealed class TeeWriter : TextWriter
    {
        private readonly TextWriter _console;
        private readonly TextWriter _file;
        private readonly object _lock = new();
        private bool _atLineStart = true;

        public TeeWriter(TextWriter console, TextWriter file)
        {
            _console = console;
            _file = file;
        }

        public override Encoding Encoding => _console.Encoding;

        public override void Write(char value) => Write(value.ToString());

        public override void Write(string? value)
        {
            if (value == null) return;
            lock (_lock)
            {
                _console.Write(value);
                try
                {
                    // Prefix each new line in the file with the time.
                    foreach (var part in value.Split('\n'))
                    {
                        if (part.Length == 0) continue;
                        if (_atLineStart) _file.Write($"{DateTime.Now:HH:mm:ss} ");
                        _file.Write(part.TrimEnd('\r'));
                        _atLineStart = false;
                    }
                    if (value.EndsWith('\n'))
                    {
                        _file.WriteLine();
                        _atLineStart = true;
                    }
                }
                catch { /* keep the console working even if the file can't be written */ }
            }
        }

        public override void WriteLine(string? value) => Write((value ?? "") + Environment.NewLine);
        public override void WriteLine() => Write(Environment.NewLine);

        public override void Flush()
        {
            _console.Flush();
            try { _file.Flush(); } catch { }
        }
    }
}
