using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Raphael;

/// <summary>
/// Raphael's magic circle (the Great Sage / Raphael look from the anime): a golden core inside a ring of light, turning
/// bands of runes, and radiating rays, drawn in the middle of the screen with per-pixel transparency. It fades in while
/// Raphael is speaking, listening or thinking, spins up and brightens with the loudness of her voice, and fades out when idle.
/// Runs on its own UI thread. Set RAPHAEL_ORB=off to disable it.
/// </summary>
public sealed class OrbOverlay : IDisposable
{
    private const int EnvStepMs = 20;

    private readonly bool _enabled = !string.Equals(Environment.GetEnvironmentVariable("RAPHAEL_ORB"), "off", StringComparison.OrdinalIgnoreCase);
    private readonly object _lock = new();
    private readonly Thread? _thread;
    private readonly ManualResetEventSlim _ready = new(false);
    private OrbForm? _form;

    // State shared with other threads (guarded by _lock).
    private DateTime _speechStart = DateTime.MinValue;
    private DateTime _speechEnd = DateTime.MinValue;
    private float[]? _envelope;
    private DateTime _listenUntil = DateTime.MinValue;
    private bool _thinking;

    public OrbOverlay()
    {
        // In the chat window the orb is drawn inside its panel (see ChatWindow), so there is no full-screen overlay: this
        // object then only keeps the speaking/listening/thinking state for that panel to read.
        if (!_enabled || UiMode.Chat) return;
        _thread = new Thread(Run) { IsBackground = true, Name = "OrbOverlay" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        _ready.Wait(TimeSpan.FromSeconds(5));
    }

    private void Run()
    {
        try
        {
            _form = new OrbForm(this);
            _ = _form.Handle;
        }
        finally
        {
            _ready.Set();
        }
        if (_form != null) Application.Run();
    }

    /// <summary>Called when a WAV starts playing: the orb follows its loudness.</summary>
    public void Speak(byte[] wav)
    {
        if (!_enabled) return;
        var env = BuildEnvelope(wav);
        lock (_lock)
        {
            _envelope = env;
            _speechStart = DateTime.Now;
            _speechEnd = env != null ? _speechStart.AddMilliseconds(env.Length * EnvStepMs) : _speechStart.AddSeconds(2);
        }
    }

    /// <summary>For the fallback Windows voice, where there is no audio to analyse.</summary>
    public void SpeakSynthetic(TimeSpan duration)
    {
        if (!_enabled) return;
        lock (_lock)
        {
            _envelope = null;
            _speechStart = DateTime.Now;
            _speechEnd = _speechStart + duration;
        }
    }

    public void Listen(DateTime until)
    {
        lock (_lock) _listenUntil = until;
    }

    public void SetThinking(bool on)
    {
        lock (_lock) _thinking = on;
    }

    public void Dispose()
    {
        var form = _form;
        if (form == null || form.IsDisposed) return;
        try { form.BeginInvoke(() => { form.Close(); Application.ExitThread(); }); } catch { }
    }

    /// <summary>Loudness (0..1) per 20 ms of a 16-bit PCM WAV, normalised to the clip's own peak.</summary>
    private static float[]? BuildEnvelope(byte[] wav)
    {
        try
        {
            if (wav.Length < 48 || BitConverter.ToInt16(wav, 34) != 16) return null;
            var channels = Math.Max(1, (int)BitConverter.ToInt16(wav, 22));
            var sampleRate = BitConverter.ToInt32(wav, 24);
            var frame = 2 * channels;
            var perWindow = Math.Max(1, sampleRate * EnvStepMs / 1000);

            var windows = (wav.Length - 44) / frame / perWindow;
            if (windows <= 0) return null;

            var env = new float[windows];
            for (var w = 0; w < windows; w++)
            {
                double sum = 0;
                for (var i = 0; i < perWindow; i++)
                {
                    var s = BitConverter.ToInt16(wav, 44 + (w * perWindow + i) * frame) / 32768.0;
                    sum += s * s;
                }
                env[w] = (float)Math.Sqrt(sum / perWindow);
            }

            var peak = env.Max();
            if (peak <= 0.0001f) return null;
            for (var w = 0; w < env.Length; w++) env[w] = Math.Min(1f, env[w] / (peak * 0.9f));
            return env;
        }
        catch
        {
            return null;
        }
    }

    // Current loudness target, plus which states are active. Called from the render thread.
    internal (float Level, bool Speaking, bool Listening, bool Thinking) Sample()
    {
        lock (_lock)
        {
            var now = DateTime.Now;
            var speaking = now < _speechEnd;
            var level = 0f;
            if (speaking)
            {
                var elapsed = (now - _speechStart).TotalSeconds;
                if (_envelope != null)
                {
                    var i = (int)(elapsed * 1000 / EnvStepMs);
                    level = i >= 0 && i < _envelope.Length ? _envelope[i] : 0f;
                }
                else
                {
                    level = (float)Math.Abs(Math.Sin(elapsed * 9) * Math.Cos(elapsed * 4.3)) * 0.9f;
                }
            }
            return (level, speaking, now < _listenUntil, _thinking);
        }
    }

    // ------------------------------------------------------------------------------------

    private sealed class OrbForm : Form
    {
        // The window is a square of this many pixels, or the screen's shorter side if that is smaller. All sizes below are
        // drawn for this design size (u = 1); on a smaller screen everything scales down together. It is big enough to hold
        // the whole huge outer circle, not just its corners.
        private const int MaxSize = 1040;
        private const float DesignSize = OrbRenderer.DesignSize;

        private const int WS_EX_LAYERED = 0x80000;
        private const int WS_EX_TOPMOST = 0x8;
        private const int WS_EX_TRANSPARENT = 0x20;
        private const int WS_EX_TOOLWINDOW = 0x80;
        private const int WS_EX_NOACTIVATE = 0x08000000;

        private readonly OrbOverlay _owner;
        private readonly int _size;
        private readonly float _scale;   // 1 on a screen big enough for the design; smaller otherwise
        private readonly OrbRenderer _renderer;
        private readonly System.Windows.Forms.Timer _timer = new() { Interval = 40 }; // 25 frames per second
        private readonly DateTime _t0 = DateTime.Now;

        private float _vis;
        private DateTime _lastFrame = DateTime.Now;
        private DateTime _lastOnTop = DateTime.MinValue;
        private Point _position;

        public OrbForm(OrbOverlay owner)
        {
            _owner = owner;
            var area = Screen.PrimaryScreen!.WorkingArea;
            var shortSide = Math.Min(area.Width, area.Height);
            _scale = Math.Min(1f, shortSide / DesignSize);   // the orb keeps its size unless the screen is too small for it

            // RAPHAEL_ORB_SIZE=800 gives a smaller window (same orb): cheaper, but the huge outer circle is then cut off
            // at the window's edges and only its corner arcs show. The default shows the whole circle.
            var wanted = int.TryParse(Environment.GetEnvironmentVariable("RAPHAEL_ORB_SIZE"), out var w) ? Math.Clamp(w, 600, MaxSize) : MaxSize;
            _size = Math.Min(wanted, shortSide);
            _renderer = new OrbRenderer(_size, _scale);

            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            Size = new Size(_size, _size);

            // Centered on the primary screen. For the bottom-right corner instead, use:
            //   new Point(area.Right - _size - 16, area.Bottom - _size - 16)
            _position = new Point(area.Left + (area.Width - _size) / 2, area.Top + (area.Height - _size) / 2);

            _timer.Tick += (_, _) => Frame();
            _timer.Start();
        }

        protected override bool ShowWithoutActivation => true;

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_TOPMOST;
                return cp;
            }
        }

        private void Frame()
        {
            var now = DateTime.Now;
            var dt = (float)Math.Min(0.1, (now - _lastFrame).TotalSeconds);
            _lastFrame = now;

            var (target, speaking, listening, thinking) = _owner.Sample();
            var active = speaking || listening || thinking;

            _renderer.SetTarget(target);
            _vis += ((active ? 1f : 0f) - _vis) * (active ? 0.18f : 0.05f);

            if (!active && _vis < 0.01f)
            {
                if (Visible) Hide();
                _renderer.ClearRipples();
                return;
            }
            if (!Visible) Show();

            // A game that went full-screen after we appeared can take the top spot: take it back.
            if ((now - _lastOnTop).TotalMilliseconds > 250)
            {
                _lastOnTop = now;
                WindowZ.KeepOnTop(Handle);
            }

            _renderer.Advance(dt, now, speaking, thinking);
            _renderer.Draw((float)(now - _t0).TotalSeconds, listening && !speaking, thinking && !speaking);
            Present((byte)Math.Clamp((int)(_vis * 255), 0, 255));
        }

        // ---- per-pixel-alpha window plumbing ---------------------------------------------

        private void Present(byte constantAlpha)
        {
            var screenDc = GetDC(IntPtr.Zero);
            var memDc = CreateCompatibleDC(screenDc);
            var hBitmap = IntPtr.Zero;
            var old = IntPtr.Zero;
            try
            {
                hBitmap = _renderer.Image.GetHbitmap(Color.FromArgb(0));
                old = SelectObject(memDc, hBitmap);

                var size = new Size(_size, _size);
                var src = new Point(0, 0);
                var dst = _position;
                var blend = new BLENDFUNCTION { BlendOp = 0, BlendFlags = 0, SourceConstantAlpha = constantAlpha, AlphaFormat = 1 };
                UpdateLayeredWindow(Handle, screenDc, ref dst, ref size, memDc, ref src, 0, ref blend, 2);
            }
            finally
            {
                if (hBitmap != IntPtr.Zero)
                {
                    SelectObject(memDc, old);
                    DeleteObject(hBitmap);
                }
                DeleteDC(memDc);
                ReleaseDC(IntPtr.Zero, screenDc);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _timer.Dispose();
                _renderer.Dispose();
            }
            base.Dispose(disposing);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct BLENDFUNCTION
        {
            public byte BlendOp;
            public byte BlendFlags;
            public byte SourceConstantAlpha;
            public byte AlphaFormat;
        }

        [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
        [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hDC);
        [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hDC);
        [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hDC, IntPtr hObject);
        [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr hObject);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref Point pptDst, ref Size psize,
            IntPtr hdcSrc, ref Point pprSrc, int crKey, ref BLENDFUNCTION pblend, int dwFlags);
    }
}
