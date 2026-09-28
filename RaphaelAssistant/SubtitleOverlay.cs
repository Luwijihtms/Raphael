using System.Drawing;
using System.Windows.Forms;

namespace Raphael;

/// <summary>
/// A borderless, always-on-top, click-through subtitle bar at the bottom of the primary screen.
/// Runs its own UI thread so it doesn't interfere with the console loop.
/// </summary>
public sealed class SubtitleOverlay : IDisposable
{
    private OverlayForm? _form;
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _ready = new(false);

    public SubtitleOverlay()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = "SubtitleOverlay" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        _ready.Wait(TimeSpan.FromSeconds(5));
    }

    private void Run()
    {
        try
        {
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            _form = new OverlayForm();
            _ = _form.Handle; // force handle creation so BeginInvoke works before first show
        }
        finally
        {
            _ready.Set();
        }

        if (_form != null) Application.Run();
    }

    public void Show(string text)
    {
        var form = _form;
        if (form == null || form.IsDisposed || string.IsNullOrWhiteSpace(text)) return;
        try { form.BeginInvoke(() => form.ShowText(text)); } catch { /* overlay is best-effort */ }
    }

    public void Dispose()
    {
        var form = _form;
        if (form == null || form.IsDisposed) return;
        try { form.BeginInvoke(() => { form.Close(); Application.ExitThread(); }); } catch { }
    }

    private sealed class OverlayForm : Form
    {
        private const int WS_EX_TRANSPARENT = 0x20;   // click-through
        private const int WS_EX_TOOLWINDOW = 0x80;    // hide from Alt-Tab
        private const int WS_EX_NOACTIVATE = 0x08000000;

        private readonly Label _label;
        private readonly System.Windows.Forms.Timer _hideTimer = new();
        private readonly System.Windows.Forms.Timer _onTopTimer = new() { Interval = 300 };

        public OverlayForm()
        {
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            TopMost = true;
            BackColor = Color.FromArgb(18, 18, 24);
            Opacity = 0.88;

            _label = new Label
            {
                AutoSize = true,
                ForeColor = Color.FromArgb(235, 240, 255),
                Font = new Font("Segoe UI", 18f, FontStyle.Regular),
                Location = new Point(28, 16),
                BackColor = Color.Transparent
            };
            Controls.Add(_label);

            _hideTimer.Tick += (_, _) => { _hideTimer.Stop(); Hide(); };

            // A game that went full-screen after the subtitle appeared can take the top spot: take it back.
            _onTopTimer.Tick += (_, _) => { if (Visible) WindowZ.KeepOnTop(Handle); };
            _onTopTimer.Start();
        }

        protected override bool ShowWithoutActivation => true;

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
                return cp;
            }
        }

        public void ShowText(string text)
        {
            var area = Screen.PrimaryScreen!.WorkingArea;
            var maxTextWidth = (int)(area.Width * 0.7);

            _label.MaximumSize = new Size(maxTextWidth, 0);
            _label.Text = text;
            var size = _label.GetPreferredSize(new Size(maxTextWidth, 0));

            ClientSize = new Size(size.Width + 56, size.Height + 32);
            Location = new Point(area.Left + (area.Width - Width) / 2, area.Bottom - Height - 48);

            if (!Visible) Show();
            WindowZ.KeepOnTop(Handle);
            _hideTimer.Stop();
            _hideTimer.Interval = 3000 + text.Length * 60; // linger long enough to read
            _hideTimer.Start();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _hideTimer.Dispose();
                _onTopTimer.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
