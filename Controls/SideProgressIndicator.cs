using System.Globalization;
using System.Windows;
using System.Windows.Media;
using NotchturneWin.Models;

namespace NotchturneWin.Controls;

/// <summary>
/// Multi-style compact progress/timer indicator for the vertical side pill and corner-wrap arm.
/// Styles: Ring (circular arc + mini time), Bar, Dots, Time (digital stack), Capsule (liquid fill).
/// </summary>
public class SideProgressIndicator : FrameworkElement
{
    private double _progress;
    private string _elapsed = "0:00";
    private string _remaining = "-0:00";
    private Color _accent = Color.FromRgb(110, 185, 255);
    private SideProgressStyle _styleMode = SideProgressStyle.Ring;
    private bool _invertDirection = true;

    private static readonly Typeface MonoFace = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
    private static readonly Typeface LightFace = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

    public SideProgressStyle StyleMode
    {
        get => _styleMode;
        set { if (_styleMode != value) { _styleMode = value; InvalidateVisual(); } }
    }

    public bool InvertDirection
    {
        get => _invertDirection;
        set { if (_invertDirection != value) { _invertDirection = value; InvalidateVisual(); } }
    }

    public Color AccentColor
    {
        get => _accent;
        set { if (_accent != value) { _accent = value; InvalidateVisual(); } }
    }

    public void UpdateProgress(double ratio, string elapsed, string remaining)
    {
        ratio = Math.Clamp(ratio, 0, 1);
        if (Math.Abs(_progress - ratio) < 0.002 && _elapsed == elapsed && _remaining == remaining)
            return;
        _progress = ratio;
        _elapsed = elapsed;
        _remaining = remaining;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth;
        double h = ActualHeight;
        if (w <= 2 || h <= 2) return;

        switch (_styleMode)
        {
            case SideProgressStyle.Ring:
                DrawRing(dc, w, h);
                break;
            case SideProgressStyle.Bar:
                DrawBar(dc, w, h);
                break;
            case SideProgressStyle.Dots:
                DrawDots(dc, w, h);
                break;
            case SideProgressStyle.Time:
                DrawTime(dc, w, h);
                break;
            case SideProgressStyle.Capsule:
                DrawCapsule(dc, w, h);
                break;
        }
    }

    private void DrawRing(DrawingContext dc, double w, double h)
    {
        double size = Math.Min(w, h) - 4;
        double r = Math.Max(8, size / 2.0);
        var center = new Point(w / 2.0, h / 2.0);
        double stroke = 3.0;

        // Track ring
        var trackPen = new Pen(new SolidColorBrush(Color.FromArgb(48, 255, 255, 255)), stroke);
        dc.DrawEllipse(null, trackPen, center, r, r);

        // Progress arc from 12 o'clock clockwise
        if (_progress > 0.004)
        {
            double angle = Math.Min(359.5, _progress * 360.0);
            double startRad = -Math.PI / 2.0;
            double endRad = startRad + angle * Math.PI / 180.0;

            var startPt = new Point(center.X + r * Math.Cos(startRad), center.Y + r * Math.Sin(startRad));
            var endPt = new Point(center.X + r * Math.Cos(endRad), center.Y + r * Math.Sin(endRad));

            var fig = new PathFigure { StartPoint = startPt, IsClosed = false };
            fig.Segments.Add(new ArcSegment(endPt, new Size(r, r), 0, angle > 180, SweepDirection.Clockwise, true));
            var geo = new PathGeometry(new[] { fig });

            // Subtle accent halo
            var glowPen = new Pen(new SolidColorBrush(Color.FromArgb(85, _accent.R, _accent.G, _accent.B)), stroke + 2.2)
            {
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round
            };
            dc.DrawGeometry(null, glowPen, geo);

            // Crisp bright arc
            var arcPen = new Pen(Brushes.White, stroke)
            {
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round
            };
            dc.DrawGeometry(null, arcPen, geo);
        }

        // Miniature elapsed time inside the ring
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var ft = new FormattedText(_elapsed, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            MonoFace, 8.8, new SolidColorBrush(Color.FromArgb(230, 245, 245, 250)), dpi);
        dc.DrawText(ft, new Point(center.X - ft.Width / 2.0, center.Y - ft.Height / 2.0 - 0.5));
    }

    private void DrawBar(DrawingContext dc, double w, double h)
    {
        double barW = 4.0;
        double barH = Math.Min(h - 4, 62);
        double x = (w - barW) / 2.0;
        double y = (h - barH) / 2.0;

        dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(46, 255, 255, 255)), null,
            new Rect(x, y, barW, barH), 2, 2);

        double fillH = Math.Max(0, barH * _progress);
        if (fillH > 0.5)
        {
            double fy = _invertDirection ? (y + barH - fillH) : y;
            var grad = new LinearGradientBrush(
                _invertDirection ? Color.FromRgb((byte)((255 + _accent.R) / 2), (byte)((255 + _accent.G) / 2), (byte)((255 + _accent.B) / 2)) : Colors.White,
                _invertDirection ? Colors.White : Color.FromRgb((byte)((255 + _accent.R) / 2), (byte)((255 + _accent.G) / 2), (byte)((255 + _accent.B) / 2)),
                90);
            dc.DrawRoundedRectangle(grad, null, new Rect(x, fy, barW, fillH), 2, 2);
        }
    }

    private void DrawDots(DrawingContext dc, double w, double h)
    {
        const int count = 7;
        double totalH = Math.Min(h - 4, 62);
        double step = totalH / (count - 1);
        double topY = (h - totalH) / 2.0;
        double cx = w / 2.0;

        double exact = _progress * count;
        for (int i = 0; i < count; i++)
        {
            int posIdx = _invertDirection ? (count - 1 - i) : i;
            double cy = topY + posIdx * step;
            bool filled = i < (int)exact;
            bool active = i == (int)exact && _progress > 0.01 && _progress < 0.995;

            if (active)
            {
                double r = 3.6;
                dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(95, _accent.R, _accent.G, _accent.B)), null, new Point(cx, cy), r + 2.2, r + 2.2);
                dc.DrawEllipse(Brushes.White, null, new Point(cx, cy), r, r);
            }
            else if (filled)
            {
                dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(225, 255, 255, 255)), null, new Point(cx, cy), 2.8, 2.8);
            }
            else
            {
                dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(52, 255, 255, 255)), null, new Point(cx, cy), 2.3, 2.3);
            }
        }
    }

    private void DrawTime(DrawingContext dc, double w, double h)
    {
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        double cx = w / 2.0;
        double cy = h / 2.0;

        var ftTop = new FormattedText(_elapsed, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            MonoFace, 10.5, Brushes.White, dpi);
        var ftBot = new FormattedText(_remaining, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            LightFace, 9.2, new SolidColorBrush(Color.FromRgb(158, 158, 168)), dpi);

        dc.DrawText(ftTop, new Point(cx - ftTop.Width / 2.0, cy - ftTop.Height - 4));

        // Thin horizontal progress hairline between elapsed & remaining
        double lineW = Math.Min(w - 10, 32);
        double lx = cx - lineW / 2.0;
        dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(46, 255, 255, 255)), null, new Rect(lx, cy - 1, lineW, 2), 1, 1);
        if (_progress > 0.01)
        {
            dc.DrawRoundedRectangle(new SolidColorBrush(_accent), null, new Rect(lx, cy - 1, lineW * _progress, 2), 1, 1);
        }

        dc.DrawText(ftBot, new Point(cx - ftBot.Width / 2.0, cy + 4));
    }

    private void DrawCapsule(DrawingContext dc, double w, double h)
    {
        double capW = 12.0;
        double capH = Math.Min(h - 4, 58);
        double x = (w - capW) / 2.0;
        double y = (h - capH) / 2.0;
        var capRect = new Rect(x, y, capW, capH);

        // Glass outer capsule
        dc.DrawRoundedRectangle(
            new SolidColorBrush(Color.FromArgb(30, 255, 255, 255)),
            new Pen(new SolidColorBrush(Color.FromArgb(65, 255, 255, 255)), 1),
            capRect, capW / 2.0, capW / 2.0);

        double fillH = Math.Max(0, (capH - 2) * _progress);
        if (fillH > 1.0)
        {
            var clip = new RectangleGeometry(new Rect(x + 1, y + 1, capW - 2, capH - 2), (capW - 2) / 2.0, (capW - 2) / 2.0);
            dc.PushClip(clip);
            double fy = _invertDirection ? (y + capH - 1 - fillH) : (y + 1);
            var fillGrad = new LinearGradientBrush(
                _invertDirection ? Color.FromArgb(235, 255, 255, 255) : Color.FromArgb(220, _accent.R, _accent.G, _accent.B),
                _invertDirection ? Color.FromArgb(220, _accent.R, _accent.G, _accent.B) : Color.FromArgb(235, 255, 255, 255),
                90);
            dc.DrawRectangle(fillGrad, null, new Rect(x + 1, fy, capW - 2, fillH));
            // Bright meniscus highlight
            double menY = _invertDirection ? fy : (fy + fillH - 1.5);
            dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(210, 255, 255, 255)), null, new Rect(x + 1, menY, capW - 2, 1.5));
            dc.Pop();
        }
    }
}
