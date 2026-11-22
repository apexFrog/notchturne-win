using System.Windows;
using System.Windows.Media;
using NotchturneWin.Models;

namespace NotchturneWin.Controls;

public class MiniVisualizer : FrameworkElement
{
    private VisualizerStyle _style = VisualizerStyle.Bars;
    private bool _isPlaying;
    private Color _accent = Colors.White;
    private readonly double[] _levels = new double[6];
    private readonly double[] _phases = { 0.4, 1.7, 3.1, 4.6, 2.3, 5.2 };
    private readonly double[] _speeds = { 5.4, 7.1, 6.2, 8.3, 5.9, 6.8 };
    private double _time;
    private long _lastTick;

    private static readonly SolidColorBrush WhiteBrush = Freeze(new SolidColorBrush(Color.FromRgb(242, 242, 247)));
    private static SolidColorBrush Freeze(SolidColorBrush b) { b.Freeze(); return b; }

    public VisualizerStyle StyleMode
    {
        get => _style;
        set
        {
            if (_style == value) return;
            _style = value;
            Visibility = value == VisualizerStyle.None ? Visibility.Collapsed : Visibility.Visible;
            InvalidateVisual();
        }
    }

    public bool IsPlaying
    {
        get => _isPlaying;
        set => _isPlaying = value;
    }

    public Color AccentColor
    {
        get => _accent;
        set => _accent = value;
    }

    public void StepFrame(long nowTicks)
    {
        if (_style == VisualizerStyle.None || Visibility != Visibility.Visible) return;

        double dt = _lastTick == 0 ? 0.016 : Math.Clamp((nowTicks - _lastTick) / (double)TimeSpan.TicksPerSecond, 0.001, 0.06);
        _lastTick = nowTicks;

        if (_isPlaying) _time += dt;

        bool anyMoving = _isPlaying;
        for (int i = 0; i < _levels.Length; i++)
        {
            double target;
            if (_isPlaying)
            {
                double w1 = Math.Sin(_time * _speeds[i] + _phases[i]);
                double w2 = Math.Sin(_time * (_speeds[i] * 1.63) + _phases[i] * 1.9);
                double w3 = Math.Cos(_time * 2.7 + i * 1.15);
                double raw = 0.5 + 0.28 * w1 + 0.16 * w2 + 0.10 * w3;
                target = Math.Clamp(raw, 0.12, 1.0);
            }
            else
            {
                target = 0.08;
            }

            double rate = target > _levels[i] ? 18.0 : 8.5;
            double next = _levels[i] + (target - _levels[i]) * Math.Min(1.0, dt * rate);
            if (Math.Abs(next - _levels[i]) > 0.003) anyMoving = true;
            _levels[i] = next;
        }

        if (anyMoving) InvalidateVisual();
    }

    private Brush GetTintedBrush()
    {
        // Blend 45% album accent with 55% white so it always pops on dark backgrounds
        byte r = (byte)Math.Clamp(_accent.R * 0.45 + 255 * 0.55, 0, 255);
        byte g = (byte)Math.Clamp(_accent.G * 0.45 + 255 * 0.55, 0, 255);
        byte b = (byte)Math.Clamp(_accent.B * 0.45 + 255 * 0.55, 0, 255);
        var br = new SolidColorBrush(Color.FromRgb(r, g, b));
        br.Freeze();
        return br;
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (_style == VisualizerStyle.None) return;

        double w = ActualWidth > 0 ? ActualWidth : Width;
        double h = ActualHeight > 0 ? ActualHeight : Height;
        if (w <= 0 || h <= 0) return;

        double cy = h / 2.0;

        switch (_style)
        {
            case VisualizerStyle.Bars:
            {
                // Original Notchturne / Dynamic Island style: 4 crisp rounded vertical pills
                int n = 4;
                double bw = 2.6;
                double gap = (w - n * bw) / (n - 1);
                for (int i = 0; i < n; i++)
                {
                    double bh = Math.Max(3.0, _levels[i] * (h - 2));
                    double x = i * (bw + gap);
                    double y = cy - bh / 2.0;
                    dc.DrawRoundedRectangle(WhiteBrush, null, new Rect(x, y, bw, bh), bw / 2.0, bw / 2.0);
                }
                break;
            }

            case VisualizerStyle.Dashes:
            {
                // Inspired by spotify-lyrics "dashes": 5 sleek symmetrical tinted bars
                int n = 5;
                double bw = 2.0;
                double gap = (w - n * bw) / (n - 1);
                var brush = GetTintedBrush();
                for (int i = 0; i < n; i++)
                {
                    double env = 1.0 - Math.Abs(i - 2) * 0.16;
                    double bh = Math.Max(2.2, _levels[i] * env * (h - 2));
                    double x = i * (bw + gap);
                    dc.DrawRoundedRectangle(brush, null, new Rect(x, cy - bh / 2.0, bw, bh), 1.0, 1.0);
                }
                break;
            }

            case VisualizerStyle.Wave:
            {
                // Inspired by spotify-lyrics "wave": smooth mini audio waveform
                var brush = GetTintedBrush();
                var pen = new Pen(brush, 1.8)
                {
                    StartLineCap = PenLineCap.Round,
                    EndLineCap = PenLineCap.Round,
                    LineJoin = PenLineJoin.Round
                };
                pen.Freeze();

                var geo = new StreamGeometry();
                using (var ctx = geo.Open())
                {
                    const int steps = 16;
                    double amp = (_isPlaying ? 1.0 : 0.08) * (h * 0.42);
                    for (int k = 0; k <= steps; k++)
                    {
                        double t = k / (double)steps;
                        double env = Math.Sin(Math.PI * t);
                        double y = cy + Math.Sin(t * Math.PI * 3.0 - _time * 8.5) * amp * env * (0.65 + 0.35 * _levels[k % 4]);
                        var pt = new Point(t * w, y);
                        if (k == 0) ctx.BeginFigure(pt, false, false);
                        else ctx.LineTo(pt, true, true);
                    }
                }
                geo.Freeze();
                dc.DrawGeometry(null, pen, geo);
                break;
            }

            case VisualizerStyle.Blocks:
            {
                // Inspired by spotify-lyrics "blocks": 4x4 micro LED pixel equalizer
                int cols = 4, rows = 4;
                double gap = 1.6;
                double cw = (w - (cols - 1) * gap) / cols;
                double ch = (h - (rows - 1) * gap) / rows;
                var brush = GetTintedBrush();

                for (int c = 0; c < cols; c++)
                {
                    int lit = Math.Clamp((int)Math.Ceiling(_levels[c] * rows), 1, rows);
                    double x = c * (cw + gap);
                    for (int r = 0; r < lit; r++)
                    {
                        double y = h - (r + 1) * ch - r * gap;
                        dc.DrawRoundedRectangle(brush, null, new Rect(x, y, cw, ch), 0.9, 0.9);
                    }
                }
                break;
            }
        }
    }
}
