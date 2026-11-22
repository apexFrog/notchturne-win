using System.Windows;
using System.Windows.Media;
using NotchturneWin.Models;

namespace NotchturneWin.Controls;

/// <summary>
/// Animated album-cover color shimmer background for the Notch.
/// Supports Variant 1 (Aurora: flowing diagonal gradient + breathing highlights)
/// and Variant 2 (SpotifyMesh: multi-spot organic color mesh inspired by spotify-lyrics).
/// </summary>
public class CoverShimmerLayer : FrameworkElement
{
    private const int MaxColors = 6;

    private readonly double[,] _cur = new double[MaxColors, 3];
    private readonly double[,] _tgt = new double[MaxColors, 3];
    private readonly double[] _avgCur = { 28, 32, 48 };
    private readonly double[] _avgTgt = { 28, 32, 48 };
    private int _colorCount = 3;

    private double _time;
    private long _lastTicks;
    private long _lastRedrawTicks;

    public bool IsActive { get; set; }
    public CoverShimmerStyle StyleMode { get; set; } = CoverShimmerStyle.Aurora;
    public double Intensity { get; set; } = 1.0;

    public CoverShimmerLayer()
    {
        IsHitTestVisible = false;
        SetDefaultPalette();
    }

    private void SetDefaultPalette()
    {
        var defaults = new[]
        {
            Color.FromRgb(68, 86, 150),
            Color.FromRgb(120, 62, 138),
            Color.FromRgb(42, 118, 146)
        };
        _colorCount = defaults.Length;
        for (int i = 0; i < MaxColors; i++)
        {
            var c = defaults[i % defaults.Length];
            _cur[i, 0] = _tgt[i, 0] = c.R;
            _cur[i, 1] = _tgt[i, 1] = c.G;
            _cur[i, 2] = _tgt[i, 2] = c.B;
        }
    }

    public void SetPalette(IReadOnlyList<Color> colors, Color avgColor)
    {
        if (colors == null || colors.Count == 0)
        {
            SetDefaultPalette();
            return;
        }

        _colorCount = Math.Clamp(colors.Count, 1, MaxColors);
        for (int i = 0; i < MaxColors; i++)
        {
            var c = colors[i % _colorCount];
            _tgt[i, 0] = c.R;
            _tgt[i, 1] = c.G;
            _tgt[i, 2] = c.B;
        }

        _avgTgt[0] = avgColor.R;
        _avgTgt[1] = avgColor.G;
        _avgTgt[2] = avgColor.B;
    }

    public void StepFrame(long nowTicks, bool isPlaying)
    {
        if (!IsActive || Visibility != Visibility.Visible)
        {
            _lastTicks = nowTicks;
            return;
        }

        // ~40 FPS is silky smooth for slow ambient gradients while keeping CPU ultra-low
        if (_lastRedrawTicks != 0 && nowTicks - _lastRedrawTicks < TimeSpan.TicksPerMillisecond * 24)
            return;

        double dt = _lastTicks == 0 ? 0.025 : Math.Min(0.1, (nowTicks - _lastTicks) / (double)TimeSpan.TicksPerSecond);
        _lastTicks = nowTicks;
        _lastRedrawTicks = nowTicks;

        double speed = isPlaying ? 1.0 : 0.42;
        _time += dt * speed;

        // Smooth color cross-fade between tracks
        double lerp = Math.Min(1.0, dt * 2.6);
        for (int i = 0; i < MaxColors; i++)
        {
            _cur[i, 0] += (_tgt[i, 0] - _cur[i, 0]) * lerp;
            _cur[i, 1] += (_tgt[i, 1] - _cur[i, 1]) * lerp;
            _cur[i, 2] += (_tgt[i, 2] - _cur[i, 2]) * lerp;
        }
        for (int j = 0; j < 3; j++)
            _avgCur[j] += (_avgTgt[j] - _avgCur[j]) * lerp;

        InvalidateVisual();
    }

    private Color GetCurColor(int idx, byte alpha)
    {
        int i = idx % Math.Max(1, _colorCount);
        return Color.FromArgb(
            alpha,
            (byte)Math.Clamp(_cur[i, 0], 0, 255),
            (byte)Math.Clamp(_cur[i, 1], 0, 255),
            (byte)Math.Clamp(_cur[i, 2], 0, 255));
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (!IsActive) return;

        double w = ActualWidth;
        double h = ActualHeight;
        if (w <= 1 || h <= 1) return;

        var rect = new Rect(0, 0, w, h);
        double intensity = Math.Clamp(Intensity, 0.35, 1.15);

        if (StyleMode == CoverShimmerStyle.Aurora)
            RenderAurora(dc, rect, w, h, intensity);
        else
            RenderSpotifyMesh(dc, rect, w, h, intensity);
    }

    /// <summary>
    /// Variant 1 (Original): Sweeping aurora wave gradient + 2 breathing orbital glow pools.
    /// </summary>
    private void RenderAurora(DrawingContext dc, Rect rect, double w, double h, double intensity)
    {
        double t = _time;

        // 1. Rotating multi-stop diagonal aurora base
        double angle = t * 0.32;
        double dx = Math.Cos(angle) * 0.52;
        double dy = Math.Sin(angle) * 0.42;

        byte aBase1 = (byte)Math.Clamp(112 * intensity, 0, 220);
        byte aBase2 = (byte)Math.Clamp(92 * intensity, 0, 200);
        byte aBase3 = (byte)Math.Clamp(78 * intensity, 0, 180);

        var sweep = new LinearGradientBrush
        {
            StartPoint = new Point(0.5 - dx, 0.5 - dy),
            EndPoint = new Point(0.5 + dx, 0.5 + dy),
            GradientStops = new GradientStopCollection
            {
                new(GetCurColor(0, aBase1), 0.0),
                new(GetCurColor(1, aBase2), 0.48 + 0.14 * Math.Sin(t * 0.55)),
                new(GetCurColor(2, aBase3), 1.0)
            }
        };
        dc.DrawRectangle(sweep, null, rect);

        // 2. Primary gliding radial highlight (horizontal aurora ribbon)
        double x1 = 0.25 + 0.50 * (0.5 + 0.5 * Math.Sin(t * 0.44));
        double y1 = 0.35 + 0.30 * Math.Cos(t * 0.31);
        byte aGlow1 = (byte)Math.Clamp((108 + 22 * Math.Sin(t * 0.7)) * intensity, 0, 220);
        var orb1 = new RadialGradientBrush
        {
            GradientOrigin = new Point(x1, y1),
            Center = new Point(x1, y1),
            RadiusX = 0.58 + 0.10 * Math.Sin(t * 0.48),
            RadiusY = 1.05,
            GradientStops = new GradientStopCollection
            {
                new(GetCurColor(0, aGlow1), 0.0),
                new(GetCurColor(1, (byte)(aGlow1 * 0.42)), 0.52),
                new(GetCurColor(1, 0), 1.0)
            }
        };
        dc.DrawRectangle(orb1, null, rect);

        // 3. Counter-drifting secondary accent glow
        double x2 = 0.75 - 0.50 * (0.5 + 0.5 * Math.Cos(t * 0.38 + 1.2));
        double y2 = 0.65 + 0.25 * Math.Sin(t * 0.41 + 0.7);
        byte aGlow2 = (byte)Math.Clamp((92 + 18 * Math.Cos(t * 0.63)) * intensity, 0, 200);
        var orb2 = new RadialGradientBrush
        {
            GradientOrigin = new Point(x2, y2),
            Center = new Point(x2, y2),
            RadiusX = 0.52,
            RadiusY = 0.95,
            GradientStops = new GradientStopCollection
            {
                new(GetCurColor(2, aGlow2), 0.0),
                new(GetCurColor(2, 0), 1.0)
            }
        };
        dc.DrawRectangle(orb2, null, rect);

        // 4. Subtle readability scrim so text stays razor-sharp
        var scrim = new LinearGradientBrush(
            Color.FromArgb(46, 0, 0, 0),
            Color.FromArgb(88, 0, 0, 0),
            90);
        dc.DrawRectangle(scrim, null, rect);
    }

    /// <summary>
    /// Variant 2 (Inspired by spotify-lyrics drawMesh):
    /// Dark average-tinted canvas with 8 softly drifting, breathing radial color spots from the cover palette.
    /// </summary>
    private void RenderSpotifyMesh(DrawingContext dc, Rect rect, double w, double h, double intensity)
    {
        double t = _time * 1.45; // gentle organic drift

        // Base fill tinted by average cover color (same 0.4 factor as spotify-lyrics, darkened for notch UI)
        byte baseA = (byte)Math.Clamp(115 * intensity, 0, 210);
        var baseColor = Color.FromArgb(
            baseA,
            (byte)Math.Clamp(_avgCur[0] * 0.36, 0, 255),
            (byte)Math.Clamp(_avgCur[1] * 0.36, 0, 255),
            (byte)Math.Clamp(_avgCur[2] * 0.36, 0, 255));
        dc.DrawRectangle(new SolidColorBrush(baseColor), null, rect);

        // 8 drifting soft radial spots (exact orbital formula from spotify-lyrics drawMesh)
        double maxDim = Math.Max(w, h);
        double baseR = maxDim * 0.56;
        byte spotAlpha = (byte)Math.Clamp(118 * intensity, 0, 215);

        for (int i = 0; i < 8; i++)
        {
            double px = 0.5 + 0.42 * Math.Sin(t * 0.13 * (1.0 + i * 0.21) + i * 2.1);
            double py = 0.5 + 0.42 * Math.Cos(t * 0.11 * (1.0 + i * 0.17) + i * 1.3);
            double rFactor = 0.65 + 0.25 * Math.Sin(t * 0.20 + i);
            double rx = (baseR * rFactor) / w;
            double ry = (baseR * rFactor) / h;

            var cCenter = GetCurColor(i, spotAlpha);
            var cEdge = GetCurColor(i, 0);

            var grad = new RadialGradientBrush
            {
                GradientOrigin = new Point(px, py),
                Center = new Point(px, py),
                RadiusX = rx,
                RadiusY = ry,
                GradientStops = new GradientStopCollection
                {
                    new(cCenter, 0.0),
                    new(Color.FromArgb((byte)(spotAlpha * 0.38), cCenter.R, cCenter.G, cCenter.B), 0.55),
                    new(cEdge, 1.0)
                }
            };
            dc.DrawRectangle(grad, null, rect);
        }

        // Subtle dark readability veil so lyrics/controls remain crisp on bright album covers
        dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(84, 6, 6, 10)), null, rect);
    }
}
