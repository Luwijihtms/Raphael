using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;

namespace Raphael;

/// <summary>
/// Draws Raphael's magic circle into a bitmap with per-pixel transparency. It knows nothing about windows: the full-screen
/// overlay (<see cref="OrbOverlay"/>) and the orb panel in the chat window both just ask it for the next frame.
/// All sizes are for a design size of <see cref="DesignSize"/> pixels and are multiplied by the scale.
/// </summary>
internal sealed class OrbRenderer : IDisposable
{
    public const float DesignSize = 1040f;

    private readonly int _size;
    private readonly float _scale;   // 1 at the design size; smaller when the orb is drawn smaller
    private readonly Bitmap _bmp;
    private readonly Random _rng = new();
    private readonly List<float> _ripples = new(); // age of each ripple, 0..1

    private float _level;
    private DateTime _lastRipple = DateTime.MinValue;

    public OrbRenderer(int size, float scale)
    {
        _size = size;
        _scale = scale;
        _bmp = new Bitmap(size, size, PixelFormat.Format32bppPArgb);
    }

    /// <summary>The last frame drawn by <see cref="Draw"/>.</summary>
    public Bitmap Image => _bmp;

    /// <summary>Fast attack, slow release, so the orb snaps to a syllable and settles smoothly.</summary>
    public void SetTarget(float target) => _level = target > _level ? target : _level * 0.80f + target * 0.20f;

    public void ClearRipples() => _ripples.Clear();

    /// <summary>Ripples on emphasis, and how far each layer of runes and rays has turned.</summary>
    public void Advance(float dt, DateTime now, bool speaking, bool thinking)
    {
        for (var i = _ripples.Count - 1; i >= 0; i--)
        {
            _ripples[i] += dt / 0.9f;
            if (_ripples[i] >= 1f) _ripples.RemoveAt(i);
        }
        if (speaking && _level > 0.35f && (now - _lastRipple).TotalSeconds > 0.22)
        {
            _ripples.Add(0f);
            _lastRipple = now;
        }

        // The rune bands and rays turn slowly at rest, and spin up with her voice or while she thinks.
        var spin = 1f + _level * 5f + (thinking && !speaking ? 3f : 0f);
        _angA += dt * 10f * spin;
        _angB -= dt * 14f * spin;
        _angC += dt * 5f * spin;
        _angD -= dt * 4f * spin;
        _angRays += dt * 3f * spin;
    }

    // ---- the Raphael look: a ring of light around a golden core, rotating bands of runes, radiating rays ----

    private sealed class RuneBand : IDisposable
    {
        public Bitmap[][] Levels = Array.Empty<Bitmap[]>();   // [brightness level][glyph]
        public float Radius;
        public int Width, Height;
        public void Dispose() { foreach (var level in Levels) foreach (var glyph in level) glyph.Dispose(); }
    }

    private const int BrightLevels = 4;   // each rune is pre-drawn at this many brightnesses (0.35 .. 1.0)
    private Bitmap? _layer;               // the slow-moving backdrop, redrawn every other frame
    private int _frame;

    private float _angA, _angB, _angC, _angD, _angRays;   // how far each layer has turned, in degrees
    private Bitmap? _hazeTeal;                            // faint teal haze, drawn once at a fixed opacity
    private Bitmap[]? _glowGold;                          // the golden glow pre-drawn at several opacities
    private RuneBand? _bandA, _bandB, _bandC, _bandD;
    private (float Angle, float Radius, float Size, float Phase)[]? _sparkles;

    private static readonly Color Gold = Color.FromArgb(255, 205, 80);
    private static readonly Color Teal = Color.FromArgb(90, 205, 225);

    private void EnsureAssets()
    {
        if (_bandA != null) return;

        var u = _scale;
        // Radii are for the design size; u scales them on smaller screens.
        _bandA = MakeBand(36, 190f * u, 31f * u, Gold, seed: 11);
        _bandB = MakeBand(42, 232f * u, 25f * u, Color.FromArgb(235, 165, 55), seed: 23);
        _bandC = MakeBand(52, 306f * u, 31f * u, Teal, seed: 37);

        // The huge outer circle: big glyphs, well apart, framing everything else as in the anime.
        _bandD = MakeBand(44, 466f * u, 54f * u, Color.FromArgb(70, 175, 235), seed: 53);

        var rng = new Random(5);
        _sparkles = Enumerable.Range(0, 70).Select(_ => (
            Angle: (float)(rng.NextDouble() * Math.PI * 2),
            Radius: (float)Math.Sqrt(rng.NextDouble()),        // sqrt: even spread over the disc
            Size: (float)(1.2 + rng.NextDouble() * 2.6),
            Phase: (float)(rng.NextDouble() * Math.PI * 2))).ToArray();
    }

    /// <summary>A ring of runic characters, each pre-drawn once as a small image so a frame only has to rotate them.</summary>
    private static RuneBand MakeBand(int count, float radius, float fontPixels, Color color, int seed)
    {
        var w = (int)(fontPixels * 1.35f);
        using var font = new Font("Segoe UI Historic", fontPixels, FontStyle.Bold, GraphicsUnit.Pixel);
        using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };

        // The same characters at every brightness, so a frame never needs a colour filter.
        var levels = new Bitmap[BrightLevels][];
        for (var level = 0; level < BrightLevels; level++)
        {
            var rng = new Random(seed);
            var brightness = 0.35f + 0.65f * level / (BrightLevels - 1);
            using var brush = new SolidBrush(Color.FromArgb((int)(color.A * brightness), color));

            levels[level] = new Bitmap[count];
            for (var i = 0; i < count; i++)
            {
                var bmp = new Bitmap(w, w, PixelFormat.Format32bppPArgb);
                using var g = Graphics.FromImage(bmp);
                g.TextRenderingHint = TextRenderingHint.AntiAlias;
                g.DrawString(((char)rng.Next(0x16A0, 0x16EB)).ToString(), font, brush, new RectangleF(0, 0, w, w), format); // the Runic block
                levels[level][i] = bmp;
            }
        }
        return new RuneBand { Levels = levels, Radius = radius, Width = w, Height = w };
    }

    private static void DrawBand(Graphics g, float cx, float cy, RuneBand band, float angle, float brightness)
    {
        var level = Math.Clamp((int)MathF.Round((brightness - 0.35f) / 0.65f * (BrightLevels - 1)), 0, BrightLevels - 1);
        var glyphs = band.Levels[level];
        var step = 360f / glyphs.Length;
        var bounds = g.VisibleClipBounds;
        for (var i = 0; i < glyphs.Length; i++)
        {
            // A band can be bigger than the window (the huge outer circle): skip glyphs that are off the canvas.
            var a = (angle + i * step) * MathF.PI / 180f;
            var gx = cx + band.Radius * MathF.Sin(a);
            var gy = cy - band.Radius * MathF.Cos(a);
            if (gx < bounds.Left - band.Width || gx > bounds.Right + band.Width ||
                gy < bounds.Top - band.Width || gy > bounds.Bottom + band.Width) continue;

            var state = g.Save();
            g.TranslateTransform(cx, cy);
            g.RotateTransform(angle + i * step);
            g.DrawImage(glyphs[i], new Rectangle(-band.Width / 2, (int)(-band.Radius - band.Height / 2f), band.Width, band.Height));
            g.Restore(state);
        }
    }

    /// <summary>
    /// The slow-moving backdrop: haze, golden glow, long rays and thin rings. It changes slowly, so it is drawn into its
    /// own image only every other frame and copied onto each frame. (The rune bands are not part of it: they spin fast
    /// while she speaks and need every frame.)
    /// </summary>
    private void RedrawBackdrop(float t, float cx, float cy, float R, float lum, float u)
    {
        _layer ??= new Bitmap(_size, _size, PixelFormat.Format32bppPArgb);
        using var g = Graphics.FromImage(_layer);
        g.Clear(Color.Transparent);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.Bilinear;

        var L = _level;
        var edge = _size / 2f - 4f;

        // Soft glows: a wide teal haze and a golden glow nearer the core.
        DrawHaze(g, cx, cy);
        DrawGoldGlow(g, cx, cy, (95 + 100 * L) / 255f * lum);

        // Long rays crossing the whole window, turning slowly.
        using (var longRay = new Pen(Color.FromArgb(A((34 + 60 * L) * lum), 255, 215, 120), Math.Max(1f, 1.5f * u)))
        {
            for (var i = 0; i < 14; i++)
            {
                var a = (_angRays * 0.4f + i * 25.7f + (i % 3) * 3f) * MathF.PI / 180f;
                g.DrawLine(longRay, cx + MathF.Cos(a) * R * 1.3f, cy + MathF.Sin(a) * R * 1.3f, cx + MathF.Cos(a) * edge, cy + MathF.Sin(a) * edge);
            }
        }

        // Thin rings that frame the runic bands.
        foreach (var (radius, color, alpha) in new[]
        {
            (160f, Gold, 90f), (212f, Gold, 70f), (250f, Gold, 80f), (276f, Teal, 90f), (338f, Teal, 90f),
            (432f, Teal, 90f), (502f, Teal, 90f)   // the thin rings framing the huge outer circle
        })
        {
            using var ringPen = new Pen(Color.FromArgb(A(alpha * lum), color), Math.Max(1f, 1.4f * u));
            var rr = radius * u * (1f + 0.02f * L * MathF.Sin(t * 6f + radius));
            g.DrawEllipse(ringPen, cx - rr, cy - rr, rr * 2, rr * 2);
        }
    }

    private static int A(float value) => Math.Clamp((int)value, 0, 255);

    public void Draw(float t, bool listeningOnly, bool thinkingOnly)
    {
        EnsureAssets();
        var u = _scale;

        using var g = Graphics.FromImage(_bmp);
        g.Clear(Color.Transparent);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.Bilinear;
        g.TextRenderingHint = TextRenderingHint.AntiAlias;

        float cx = _size / 2f, cy = _size / 2f;
        var L = _level;
        var lum = (listeningOnly ? 0.7f : 1f) * (thinkingOnly ? 0.78f + 0.22f * MathF.Sin(t * 11f) : 1f);

        // The ring of light breathes at rest, shimmers while thinking, and swells with her voice.
        var breath = 1f + 0.03f * MathF.Sin(t * (listeningOnly ? 1.5f : 2f)) + (thinkingOnly ? 0.03f * MathF.Sin(t * 9f) : 0f);
        var baseR = 120f * u;
        var R = baseR * breath * (1f + 0.22f * L);

        var shake = L * 4f * u;
        cx += (float)(_rng.NextDouble() * 2 - 1) * shake;
        cy += (float)(_rng.NextDouble() * 2 - 1) * shake;

        var edge = _size / 2f - 4f;

        // 1-3. The backdrop (haze, glow, long rays, thin rings) barely moves: redrawn every other frame, copied every frame.
        if (_layer == null || (_frame++ & 1) == 0) RedrawBackdrop(t, cx, cy, R, lum, u);
        g.DrawImage(_layer!, new Rectangle(0, 0, _size, _size));

        // 4. The three bands of runes, turning against one another; faster and brighter with her voice. These are drawn
        // on every frame: when she speaks they spin fast, and drawing them only every other frame made that stutter.
        DrawBand(g, cx, cy, _bandD!, _angD, (0.6f + 0.3f * L) * lum);
        DrawBand(g, cx, cy, _bandC!, _angC, (0.62f + 0.3f * L) * lum);
        DrawBand(g, cx, cy, _bandB!, _angB, (0.55f + 0.3f * L) * lum);
        DrawBand(g, cx, cy, _bandA!, _angA, (0.85f + 0.15f * L) * lum);

        // 5. Ripples on emphasis.
        foreach (var age in _ripples)
        {
            var start = R * 1.15f;
            var r = start + age * (edge - start);
            using var ripple = new Pen(Color.FromArgb(A((1f - age) * 150 * lum), 255, 235, 170), Math.Max(1f, 2f * u));
            g.DrawEllipse(ripple, cx - r, cy - r, r * 2, r * 2);
        }

        // 6. Short rays bursting out of the ring; they lengthen with her voice.
        using (var shortRay = new Pen(Color.FromArgb(A((110 + 120 * L) * lum), 255, 225, 140), Math.Max(1f, 2f * u)))
        {
            for (var i = 0; i < 30; i++)
            {
                var a = (_angRays + i * 12f) * MathF.PI / 180f;
                var len = R * (0.18f + (0.22f + 0.5f * L) * (0.5f + 0.5f * MathF.Sin(i * 2.3f + t * 3f)));
                g.DrawLine(shortRay, cx + MathF.Cos(a) * R * 1.1f, cy + MathF.Sin(a) * R * 1.1f,
                                     cx + MathF.Cos(a) * (R * 1.1f + len), cy + MathF.Sin(a) * (R * 1.1f + len));
            }
        }

        // 7. The core: a dark-amber disc, brightest at the centre, with rays and sparkles.
        var disc = R - 10f * u;
        using (var discPath = new GraphicsPath())
        {
            discPath.AddEllipse(cx - disc, cy - disc, disc * 2, disc * 2);
            using var discBrush = new PathGradientBrush(discPath)
            {
                CenterPoint = new PointF(cx, cy),
                InterpolationColors = new ColorBlend
                {
                    Positions = new[] { 0f, 0.55f, 1f },
                    Colors = new[]
                    {
                        Color.FromArgb(255, 150, 70, 12),
                        Color.FromArgb(255, 220, 135, 28),
                        Color.FromArgb(255, 255, A(215 + 40 * L), A(120 + 80 * L))
                    }
                }
            };
            g.FillPath(discBrush, discPath);
        }

        using (var discRay = new Pen(Color.FromArgb(A((45 + 60 * L) * lum), 255, 240, 170), Math.Max(1f, 1.5f * u)))
        {
            for (var i = 0; i < 20; i++)
            {
                var a = (_angRays * 0.6f + i * 360f / 20f) * MathF.PI / 180f;
                g.DrawLine(discRay, cx, cy, cx + MathF.Cos(a) * disc * 0.95f, cy + MathF.Sin(a) * disc * 0.95f);
            }
        }

        foreach (var s in _sparkles!)
        {
            var a = s.Angle + _angRays * 0.25f * MathF.PI / 180f;
            var d = s.Radius * disc * 0.92f;
            var size = s.Size * u * (1f + 0.6f * L);
            var glint = 0.5f + 0.5f * MathF.Sin(t * 3f + s.Phase);
            using var sparkle = new SolidBrush(Color.FromArgb(A((40 + 170 * glint) * lum), 255, 245, 200));
            g.FillEllipse(sparkle, cx + MathF.Cos(a) * d - size, cy + MathF.Sin(a) * d - size, size * 2, size * 2);
        }

        // 8. The bright ring of light, a slightly irregular loop that wobbles more the louder she speaks.
        const int points = 12;
        var loop = new PointF[points];
        for (var i = 0; i < points; i++)
        {
            var a = i * MathF.PI * 2 / points;
            var r = R * (1f + 0.04f * MathF.Sin(2 * a + t * 1.7f + i) + L * 0.07f * MathF.Sin(3 * a - t * 8f));
            loop[i] = new PointF(cx + r * MathF.Cos(a), cy + r * MathF.Sin(a));
        }
        using (var ringPath = new GraphicsPath())
        {
            ringPath.AddClosedCurve(loop, 0.45f);
            foreach (var (width, alpha, color) in new[]
            {
                (32f, 55f,  Color.FromArgb(255, 215, 110)),
                (20f, 115f, Color.FromArgb(255, 235, 160)),
                (13f, 255f, Color.FromArgb(255, 252, 232))
            })
            {
                using var glowPen = new Pen(Color.FromArgb(A(alpha * lum), color), Math.Max(1.5f, width * u)) { LineJoin = LineJoin.Round };
                g.DrawPath(glowPen, ringPath);
            }
        }

        // 9. A white-gold flare at the very centre.
        FillGlow(g, cx, cy, R * 0.6f, Color.FromArgb(A(190 * lum), 255, 246, 205));
    }

    private const int GlowLevels = 8;

    /// <summary>
    /// The faint teal haze over the whole window. Drawn once; unlike the golden glow it doesn't change with her voice.
    /// (These glows fade to nothing before the edge of their image, so there is no visible square.)
    /// </summary>
    private void DrawHaze(Graphics g, float cx, float cy)
    {
        var r = 440f * _scale;   // a fixed reach (not the whole window): a smaller image is cheaper to copy each frame
        if (_hazeTeal == null)
        {
            _hazeTeal = new Bitmap((int)(r * 2), (int)(r * 2), PixelFormat.Format32bppPArgb);
            using var gg = Graphics.FromImage(_hazeTeal);
            gg.SmoothingMode = SmoothingMode.AntiAlias;
            FillGlow(gg, r, r, r, Color.FromArgb(70, 40, 170, 200));
        }
        g.DrawImage(_hazeTeal, new Rectangle((int)(cx - r), (int)(cy - r), _hazeTeal.Width, _hazeTeal.Height));
    }

    /// <summary>
    /// The golden glow around the core, brighter with her voice. It is pre-drawn at a few opacities and the closest one is
    /// copied straight onto the frame: blending one huge image through a colour filter every frame was the main cost.
    /// </summary>
    private void DrawGoldGlow(Graphics g, float cx, float cy, float alpha)
    {
        var r = 300f * _scale;
        if (_glowGold == null)
        {
            _glowGold = new Bitmap[GlowLevels];
            for (var i = 0; i < GlowLevels; i++)
            {
                var level = 0.2f + 0.6f * i / (GlowLevels - 1);   // opacities 0.2 .. 0.8
                _glowGold[i] = new Bitmap((int)(r * 2), (int)(r * 2), PixelFormat.Format32bppPArgb);
                using var gg = Graphics.FromImage(_glowGold[i]);
                gg.SmoothingMode = SmoothingMode.AntiAlias;
                FillGlow(gg, r, r, r, Color.FromArgb((int)(level * 255), 255, 170, 40));
            }
        }

        var index = Math.Clamp((int)MathF.Round((alpha - 0.2f) / 0.6f * (GlowLevels - 1)), 0, GlowLevels - 1);
        var glow = _glowGold[index];
        g.DrawImage(glow, new Rectangle((int)(cx - r), (int)(cy - r), glow.Width, glow.Height));
    }

    private static void FillGlow(Graphics g, float cx, float cy, float r, Color color)
    {
        using var path = new GraphicsPath();
        path.AddEllipse(cx - r, cy - r, r * 2, r * 2);
        using var brush = new PathGradientBrush(path)
        {
            CenterColor = color,
            SurroundColors = new[] { Color.FromArgb(0, color.R, color.G, color.B) }
        };
        g.FillPath(brush, path);
    }

    public void Dispose()
    {
        _bmp.Dispose();
        _hazeTeal?.Dispose();
        if (_glowGold != null) foreach (var glow in _glowGold) glow.Dispose();
        _layer?.Dispose();
        _bandA?.Dispose();
        _bandB?.Dispose();
        _bandC?.Dispose();
        _bandD?.Dispose();
    }
}
