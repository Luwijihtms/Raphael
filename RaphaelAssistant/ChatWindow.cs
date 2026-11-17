using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using System.Windows.Forms;

namespace Raphael;

/// <summary>
/// Raphael's window, in four parts. Top left: what she says (English, with the Japanese she speaks beneath it; each can be
/// switched off). Top right: her picture. Bottom left: where you type. Bottom right: the orb, which follows her voice.
/// It runs on its own UI thread; everything else talks to it through the static methods, which do nothing when the window
/// isn't used (classic console mode).
/// </summary>
public sealed class ChatWindow
{
    private static ChatWindow? _current;
    private static volatile bool _shown;

    private readonly Channel<string> _lines = Channel.CreateUnbounded<string>();
    private readonly ManualResetEventSlim _ready = new(false);
    private MainForm? _form;

    private const int MaxEntries = 300;

    private sealed record Entry(DateTime At, string Kind, string Ja, string En);

    /// <summary>True while the window is open and not minimised: then a subtitle bar over the screen would only repeat it.</summary>
    public static bool IsShowing => _shown;

    public static void Start(OrbOverlay orb, bool startMinimized)
    {
        var window = new ChatWindow();
        _current = window;
        var thread = new Thread(() =>
        {
            try
            {
                Application.EnableVisualStyles();
                window._form = new MainForm(window, orb, startMinimized);
                _ = window._form.Handle; // so BeginInvoke works before the window is first shown
            }
            finally
            {
                window._ready.Set();
            }
            if (window._form != null) Application.Run(window._form);
        }) { IsBackground = true, Name = "ChatWindow" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        window._ready.Wait(TimeSpan.FromSeconds(5));
    }

    /// <summary>What she said: the Japanese she speaks and its English.</summary>
    public static void AddReply(string japanese, string english) => _current?.Post(f => f.AddEntry(new Entry(DateTime.Now, "reply", japanese, english)));

    /// <summary>A line from the user that did not come from the typing box ("phone", "voice").</summary>
    public static void AddUser(string text, string source) => _current?.Post(f => f.AddEntry(new Entry(DateTime.Now, source, "", text)));

    /// <summary>The next line the user typed, or null when the window is not in use (then it reads the console).</summary>
    public static Task<string?> ReadLineAsync() => _current != null ? _current.ReadAsync() : Task.Run(Console.ReadLine);

    private async Task<string?> ReadAsync() => await _lines.Reader.ReadAsync();

    private void Post(Action<MainForm> action)
    {
        var form = _form;
        if (form == null || form.IsDisposed) return;
        try { form.BeginInvoke(() => action(form)); } catch { /* the window is going away */ }
    }

    // ---------------------------------------------------------------------------------------------

    private sealed class MainForm : Form
    {
        private static readonly Color Bg = Color.FromArgb(8, 12, 22);
        private static readonly Color Card = Color.FromArgb(13, 20, 36);
        private static readonly Color Edge = Color.FromArgb(32, 48, 78);
        private static readonly Color Ink = Color.FromArgb(232, 238, 255);
        private static readonly Color Dim = Color.FromArgb(128, 146, 178);
        private static readonly Color Teal = Color.FromArgb(90, 205, 225);
        private static readonly Color Gold = Color.FromArgb(255, 205, 80);

        private readonly ChatWindow _owner;
        private readonly List<Entry> _entries = new();
        private readonly List<string> _history = new();
        private int _historyIndex;
        private bool _closing;

        private readonly RichTextBox _log = new();
        private readonly CheckBox _showEn = new() { Text = "English", Checked = true };
        private readonly CheckBox _showJa = new() { Text = "日本語", Checked = true };
        private readonly TextBox _input = new();
        private readonly Label _status = new() { Text = "Ready" };

        private readonly Font _body = new("Segoe UI", 11f);
        private readonly Font _jp = new("Yu Gothic UI", 10f);
        private readonly Font _bold = new("Segoe UI Semibold", 9.5f);
        private readonly Font _small = new("Segoe UI", 8.5f);

        public MainForm(ChatWindow owner, OrbOverlay orb, bool startMinimized)
        {
            _owner = owner;

            Text = "Raphael";
            BackColor = Bg;
            ForeColor = Ink;
            Font = new Font("Segoe UI", 10f);
            AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(1180, 760);
            MinimumSize = new Size(880, 580);
            StartPosition = FormStartPosition.CenterScreen;
            ShowInTaskbar = true;
            if (startMinimized) WindowState = FormWindowState.Minimized; // started by Windows at login: stay out of the way

            var picture = LoadPicture();
            try { Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!); } // the same icon the exe file has
            catch
            {
                if (picture != null) try { Icon = Icon.FromHandle(new Bitmap(picture, 64, 64).GetHicon()); } catch { }
            }

            var grid = new TableLayoutPanel
            {
                Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2, BackColor = Bg, Padding = new Padding(8)
            };
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 57));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 43));
            grid.RowStyles.Add(new RowStyle(SizeType.Percent, 58));
            grid.RowStyles.Add(new RowStyle(SizeType.Percent, 42));
            Controls.Add(grid);

            grid.Controls.Add(BuildReplies(), 0, 0);
            grid.Controls.Add(BuildPicture(picture, orb), 1, 0);
            grid.Controls.Add(BuildInput(), 0, 1);
            grid.Controls.Add(BuildOrb(orb), 1, 1);

            _showEn.CheckedChanged += (_, _) => { if (!_showEn.Checked && !_showJa.Checked) _showJa.Checked = true; else Render(); };
            _showJa.CheckedChanged += (_, _) => { if (!_showEn.Checked && !_showJa.Checked) _showEn.Checked = true; else Render(); };

            Resize += (_, _) => UpdateShown();
            VisibleChanged += (_, _) => UpdateShown();
            Shown += (_, _) => _input.Focus();
        }

        private void UpdateShown() => _shown = Visible && WindowState != FormWindowState.Minimized;

        // ---- the four parts ------------------------------------------------------------------

        private static Panel NewCard() => new()
        {
            Dock = DockStyle.Fill, Margin = new Padding(6), BackColor = Card, Padding = new Padding(1)
        };

        private static void PaintEdge(Control card) => card.Paint += (_, e) =>
        {
            using var pen = new Pen(Edge);
            e.Graphics.DrawRectangle(pen, 0, 0, card.Width - 1, card.Height - 1);
        };

        private Panel Header(string title, params Control[] right)
        {
            var header = new Panel { Dock = DockStyle.Top, Height = 34, BackColor = Card, Padding = new Padding(14, 0, 10, 0) };
            var label = new Label
            {
                Text = title, Dock = DockStyle.Fill, AutoSize = false, ForeColor = Teal,
                Font = new Font("Segoe UI Semibold", 9f), TextAlign = ContentAlignment.MiddleLeft
            };
            header.Controls.Add(label);
            foreach (var c in right)
            {
                c.Dock = DockStyle.Right;   // the first one given ends up furthest left
                header.Controls.Add(c);
            }
            label.BringToFront();   // the title takes whatever room the right-hand items leave
            return header;
        }

        private Panel BuildReplies()
        {
            var card = NewCard();
            PaintEdge(card);

            foreach (var box in new[] { _showEn, _showJa })
            {
                box.AutoSize = true; box.ForeColor = Dim; box.Font = _small;
                box.Padding = new Padding(8, 0, 0, 0); box.TextAlign = ContentAlignment.MiddleLeft;
            }

            _log.Dock = DockStyle.Fill;
            _log.ReadOnly = true;
            _log.BorderStyle = BorderStyle.None;
            _log.BackColor = Card;
            _log.ForeColor = Ink;
            _log.HideSelection = false;
            _log.DetectUrls = true;
            _log.ScrollBars = RichTextBoxScrollBars.Vertical;
            _log.TabStop = false;

            var body = new Panel { Dock = DockStyle.Fill, BackColor = Card, Padding = new Padding(14, 2, 10, 12) };
            body.Controls.Add(_log);
            card.Controls.Add(body);
            card.Controls.Add(Header("RAPHAEL", _showEn, _showJa));
            return card;
        }

        private Panel BuildPicture(Image? picture, OrbOverlay orb)
        {
            var card = NewCard();
            PaintEdge(card);
            card.BackColor = Color.Black;
            card.Controls.Add(new PortraitPanel(picture, orb) { Dock = DockStyle.Fill });
            return card;
        }

        private Panel BuildInput()
        {
            var card = NewCard();
            PaintEdge(card);

            _status.AutoSize = false;
            _status.Width = 120;
            _status.ForeColor = Dim;
            _status.Font = _small;
            _status.TextAlign = ContentAlignment.MiddleRight;

            _input.Multiline = true;
            _input.AcceptsReturn = true;
            _input.BorderStyle = BorderStyle.None;
            _input.BackColor = Color.FromArgb(18, 28, 50);
            _input.ForeColor = Ink;
            _input.Font = _body;
            _input.Dock = DockStyle.Fill;
            _input.ScrollBars = ScrollBars.None;   // long text still scrolls with the cursor
            _input.KeyDown += InputKeyDown;

            var send = new Button
            {
                Text = "Send", Dock = DockStyle.Bottom, Height = 46, FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(22, 62, 84), ForeColor = Ink, Font = _bold, Cursor = Cursors.Hand
            };
            send.FlatAppearance.BorderColor = Teal;
            send.FlatAppearance.BorderSize = 1;
            send.Click += (_, _) => Submit();

            var inputBox = new Panel { Dock = DockStyle.Fill, BackColor = Color.FromArgb(18, 28, 50), Padding = new Padding(12, 10, 12, 10) };
            inputBox.Controls.Add(_input);

            var row = new Panel { Dock = DockStyle.Fill, BackColor = Card, Padding = new Padding(14, 4, 14, 14) };
            var side = new Panel { Dock = DockStyle.Right, Width = 102, BackColor = Card, Padding = new Padding(10, 0, 0, 0) };
            side.Controls.Add(send);
            row.Controls.Add(inputBox);
            row.Controls.Add(side);

            card.Controls.Add(row);
            card.Controls.Add(Header("YOU   ·   Enter to send, Shift+Enter for a new line", _status));
            return card;
        }

        private Panel BuildOrb(OrbOverlay orb)
        {
            var card = NewCard();
            PaintEdge(card);
            var panel = new OrbPanel(orb) { Dock = DockStyle.Fill };
            panel.StatusChanged += text => _status.Text = text;
            card.Controls.Add(panel);
            return card;
        }

        private static Image? LoadPicture()
        {
            try
            {
                using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("raphael.png");
                return stream == null ? null : new Bitmap(stream);
            }
            catch { return null; }
        }

        // ---- typing --------------------------------------------------------------------------

        private void InputKeyDown(object? sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter && !e.Shift)
            {
                e.SuppressKeyPress = true;
                e.Handled = true;
                Submit();
            }
            else if (e.KeyCode == Keys.Up && _input.GetLineFromCharIndex(_input.SelectionStart) == 0 && _history.Count > 0)
            {
                _historyIndex = Math.Max(0, _historyIndex - 1);
                _input.Text = _history[_historyIndex];
                _input.SelectionStart = _input.TextLength;
                e.SuppressKeyPress = true;
            }
            else if (e.KeyCode == Keys.Down && _historyIndex < _history.Count)
            {
                _historyIndex++;
                _input.Text = _historyIndex < _history.Count ? _history[_historyIndex] : "";
                _input.SelectionStart = _input.TextLength;
                e.SuppressKeyPress = true;
            }
        }

        private void Submit()
        {
            var text = _input.Text.Trim();
            if (text.Length == 0) return;

            _input.Clear();
            _history.Add(text);
            _historyIndex = _history.Count;
            AddEntry(new Entry(DateTime.Now, "you", "", text));
            _owner._lines.Writer.TryWrite(text);
        }

        // ---- the reply log -------------------------------------------------------------------

        public void AddEntry(Entry entry)
        {
            _entries.Add(entry);
            if (_entries.Count > MaxEntries)
            {
                _entries.RemoveRange(0, _entries.Count - MaxEntries);
                Render();
                return;
            }
            AppendEntry(entry);
            ScrollToEnd();
        }

        private void Render()
        {
            _log.SuspendLayout();
            _log.Clear();
            foreach (var entry in _entries) AppendEntry(entry);
            _log.ResumeLayout();
            ScrollToEnd();
        }

        private void ScrollToEnd()
        {
            _log.SelectionStart = _log.TextLength;
            _log.SelectionLength = 0;
            _log.ScrollToCaret();
        }

        private void Run(string text, Font font, Color color)
        {
            _log.SelectionStart = _log.TextLength;
            _log.SelectionLength = 0;
            _log.SelectionFont = font;
            _log.SelectionColor = color;
            _log.AppendText(text);
        }

        private void AppendEntry(Entry entry)
        {
            var (name, color) = entry.Kind switch
            {
                "reply" => ("Raphael", Teal),
                "phone" => ("You · phone", Gold),
                "voice" => ("You · voice", Gold),
                _ => ("You", Gold)
            };
            Run($"{name}  ", _bold, color);
            Run($"{entry.At:HH:mm}\n", _small, Dim);

            if (entry.Kind != "reply")
            {
                Run(entry.En + "\n\n", _body, Ink);
                return;
            }

            var en = entry.En.Trim();
            var ja = entry.Ja.Trim();
            var sameText = ja.Length == 0 || ja == en;   // a line with no separate Japanese is shown once
            var wroteAny = false;

            if (_showEn.Checked && en.Length > 0) { Run(en + "\n", _body, Ink); wroteAny = true; }
            if (_showJa.Checked && !sameText) { Run(ja + "\n", _jp, Dim); wroteAny = true; }
            if (!wroteAny) Run((en.Length > 0 ? en : ja) + "\n", _body, Ink);
            Run("\n", _small, Dim);
        }

        // ---- closing -------------------------------------------------------------------------

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!_closing)
            {
                // Closing the window ends Raphael, like closing the old console did. The main loop does the tidy-up.
                _closing = true;
                e.Cancel = true;
                Hide();
                _owner._lines.Writer.TryWrite("exit");
                _ = Task.Delay(TimeSpan.FromSeconds(20)).ContinueWith(_ => Environment.Exit(0)); // if she is busy, don't linger
            }
            base.OnFormClosing(e);
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            try { var on = 1; DwmSetWindowAttribute(Handle, 20, ref on, sizeof(int)); } catch { } // dark title bar
        }

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        protected override void Dispose(bool disposing)
        {
            if (disposing) { _body.Dispose(); _jp.Dispose(); _bold.Dispose(); _small.Dispose(); }
            base.Dispose(disposing);
        }
    }

    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Her picture, made to feel present without changing her face (she is expressionless in the anime): it breathes very
    /// slowly, a glow around the edge swells with her voice (teal at rest, turning gold as she speaks), and sparkles and
    /// runes drift upwards, faster while she speaks.
    /// </summary>
    private sealed class PortraitPanel : Control
    {
        private sealed class Mote { public float X, Y, Speed, Phase, Size; public char Rune; public bool Gold; }

        private static readonly Color Teal = Color.FromArgb(90, 205, 225);
        private static readonly Color Gold = Color.FromArgb(255, 205, 80);

        private readonly Image? _image;
        private readonly OrbOverlay _orb;
        private readonly System.Windows.Forms.Timer _timer = new() { Interval = 40 };
        private readonly DateTime _t0 = DateTime.Now;
        private readonly Random _rng = new(7);
        private readonly List<Mote> _motes = new();
        private readonly Font _runeFont = new("Segoe UI Historic", 14f, FontStyle.Bold, GraphicsUnit.Pixel);
        private Bitmap? _fitted;
        private DateTime _last = DateTime.Now;
        private float _level, _glow = 0.12f;

        public PortraitPanel(Image? image, OrbOverlay orb)
        {
            _image = image;
            _orb = orb;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);
            BackColor = Color.Black;

            for (var i = 0; i < 34; i++)
            {
                var rune = i % 6 == 0;   // a few of the motes are runes rather than dots
                _motes.Add(new Mote
                {
                    X = (float)_rng.NextDouble(), Y = (float)_rng.NextDouble(),
                    Speed = 0.015f + (float)_rng.NextDouble() * 0.035f, Phase = (float)(_rng.NextDouble() * Math.PI * 2),
                    Size = 1.5f + (float)_rng.NextDouble() * 2.2f, Gold = i % 2 == 0,
                    Rune = rune ? (char)_rng.Next(0x16A0, 0x16EB) : '\0'
                });
            }

            _timer.Tick += (_, _) => Tick();
            _timer.Start();
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            _fitted?.Dispose();
            _fitted = null;   // rebuilt at the new size when next drawn
        }

        private void Tick()
        {
            var form = FindForm();
            if (!Visible || form == null || form.WindowState == FormWindowState.Minimized) return;

            var now = DateTime.Now;
            var dt = (float)Math.Min(0.1, (now - _last).TotalSeconds);
            _last = now;

            var (target, speaking, listening, thinking) = _orb.Sample();
            var active = speaking || listening || thinking;
            _level = target > _level ? target : _level * 0.80f + target * 0.20f;

            var glowTarget = speaking ? 0.35f + 0.65f * _level : active ? 0.3f : 0.12f;
            _glow += (glowTarget - _glow) * 0.15f;

            foreach (var m in _motes)
            {
                m.Y -= m.Speed * dt * (1f + 4f * _level + (active ? 0.6f : 0f));
                if (m.Y < -0.05f)
                {
                    m.Y = 1.05f;
                    m.X = (float)_rng.NextDouble();
                }
            }

            _timer.Interval = active ? 40 : 66;
            Invalidate();
        }

        private void BuildFitted()
        {
            if (_image == null || Width < 20 || Height < 20) return;
            var scale = Math.Min((float)Width / _image.Width, (float)Height / _image.Height);
            var w = Math.Max(1, (int)(_image.Width * scale));
            var h = Math.Max(1, (int)(_image.Height * scale));
            _fitted = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
            using var g = Graphics.FromImage(_fitted);
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.DrawImage(_image, 0, 0, w, h);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Color.Black);
            var t = (float)(DateTime.Now - _t0).TotalSeconds;

            if (_fitted == null) BuildFitted();
            if (_fitted != null)
            {
                // Breathing: a slow swell of about 1.5%, a little more with her voice. Never smaller than the fitted picture.
                var breathe = 1f + 0.015f * (0.5f + 0.5f * MathF.Sin(t * 0.9f)) + 0.02f * _level;
                var w = _fitted.Width * breathe;
                var h = _fitted.Height * breathe;
                g.InterpolationMode = InterpolationMode.Bilinear;
                g.DrawImage(_fitted, (Width - w) / 2f, (Height - h) / 2f, w, h);
            }

            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;

            // Sparkles and runes drifting up.
            foreach (var m in _motes)
            {
                var twinkle = 0.5f + 0.5f * MathF.Sin(t * 2.2f + m.Phase);
                var fade = Math.Clamp(Math.Min(m.Y, 1f - m.Y) / 0.12f, 0f, 1f);   // fade in and out near the top and bottom
                var alpha = (int)Math.Clamp((25 + 140 * twinkle) * (0.45f + 0.55f * _level + 0.2f) * fade, 0, 255);
                if (alpha < 4) continue;
                var x = (m.X + MathF.Sin(t * 0.7f + m.Phase) * 0.02f) * Width;
                var y = m.Y * Height;
                var color = Color.FromArgb(alpha, m.Gold ? Gold : Teal);

                if (m.Rune != '\0')
                {
                    using var brush = new SolidBrush(color);
                    g.DrawString(m.Rune.ToString(), _runeFont, brush, x, y);
                }
                else
                {
                    var size = m.Size * (1f + 0.5f * _level);
                    using var brush = new SolidBrush(color);
                    g.FillEllipse(brush, x - size, y - size, size * 2, size * 2);
                }
            }

            // The edge glow: teal at rest, shifting to gold and swelling as she speaks.
            var edge = Color.FromArgb(
                (int)(Teal.R + (Gold.R - Teal.R) * _level), (int)(Teal.G + (Gold.G - Teal.G) * _level), (int)(Teal.B + (Gold.B - Teal.B) * _level));
            for (var i = 0; i < 10; i++)
            {
                var a = (int)Math.Clamp(_glow * 75 * (1f - i / 10f), 0, 255);
                using var pen = new Pen(Color.FromArgb(a, edge), 3f);
                g.DrawRectangle(pen, i * 3, i * 3, Width - 1 - i * 6, Height - 1 - i * 6);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { _timer.Dispose(); _fitted?.Dispose(); _runeFont.Dispose(); }
            base.Dispose(disposing);
        }
    }

    /// <summary>The orb, drawn into a panel instead of over the whole screen. Dimmer at rest, bright while she speaks.</summary>
    private sealed class OrbPanel : Control
    {
        private readonly OrbOverlay _orb;
        private readonly System.Windows.Forms.Timer _timer = new() { Interval = 40 };
        private readonly DateTime _t0 = DateTime.Now;
        private OrbRenderer? _renderer;
        private DateTime _last = DateTime.Now;
        private float _brightness = 0.5f;
        private string _status = "Ready";

        public event Action<string>? StatusChanged;

        public OrbPanel(OrbOverlay orb)
        {
            _orb = orb;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);
            BackColor = Color.FromArgb(7, 11, 20);
            _timer.Tick += (_, _) => Tick();
            _timer.Start();
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            _renderer?.Dispose();
            _renderer = null;   // rebuilt at the new size on the next frame
        }

        private void Tick()
        {
            var form = FindForm();
            if (!Visible || form == null || form.WindowState == FormWindowState.Minimized) return; // nothing to draw for

            var side = Math.Min(Width, Height);
            if (side < 80) return;
            _renderer ??= new OrbRenderer(side, side / OrbRenderer.DesignSize);

            var now = DateTime.Now;
            var dt = (float)Math.Min(0.1, (now - _last).TotalSeconds);
            _last = now;

            var (level, speaking, listening, thinking) = _orb.Sample();
            var active = speaking || listening || thinking;

            _renderer.SetTarget(level);
            _renderer.Advance(dt, now, speaking, thinking);
            _renderer.Draw((float)(now - _t0).TotalSeconds, listening && !speaking, thinking && !speaking);

            _brightness += ((active ? 1f : 0.5f) - _brightness) * (active ? 0.18f : 0.06f);
            _timer.Interval = active ? 40 : 80;   // the idle breathing doesn't need 25 frames a second

            var status = speaking ? "Speaking" : thinking ? "Thinking…" : listening ? "Listening" : "Ready";
            if (status != _status) { _status = status; StatusChanged?.Invoke(status); }

            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(BackColor);
            var image = _renderer?.Image;
            if (image == null) return;

            var matrix = new ColorMatrix { Matrix33 = Math.Clamp(_brightness, 0f, 1f) };
            using var attributes = new ImageAttributes();
            attributes.SetColorMatrix(matrix);

            var x = (Width - image.Width) / 2;
            var y = (Height - image.Height) / 2;
            e.Graphics.DrawImage(image, new Rectangle(x, y, image.Width, image.Height), 0, 0, image.Width, image.Height, GraphicsUnit.Pixel, attributes);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { _timer.Dispose(); _renderer?.Dispose(); }
            base.Dispose(disposing);
        }
    }
}
