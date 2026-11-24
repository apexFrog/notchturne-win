using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using NotchturneWin.Models;
using NotchturneWin.Services;

namespace NotchturneWin.Controls;

public partial class LyricsView : UserControl
{
    private List<LyricLine> _synced = new();
    private readonly List<TextBlock> _lineBlocks = new();
    private Border? _topSpacer;
    private Border? _bottomSpacer;
    private TextBlock? _lockedActiveBlock;

    private string? _plain;
    private LyricsMode _mode = LyricsMode.Karaoke;
    private double _baseFontSize = 13.5;
    private bool _compact = true;
    private bool _lockActiveLine = true;
    private bool _useShadow = false;

    private int _lastIdx = -99;
    private int _lastReveal = -99;
    private bool _lastBlink = false;
    private double _lastPos = -99;
    private DateTime _manualScrollUntil = DateTime.MinValue;
    private double _targetScrollOffset = -1;

    public event Action<double>? LineClicked;

    private static readonly SolidColorBrush BrushActive = Freeze(new SolidColorBrush(Colors.White));
    private static readonly SolidColorBrush BrushPast = Freeze(new SolidColorBrush(Color.FromRgb(210, 210, 220)));
    private static readonly SolidColorBrush BrushFuture = Freeze(new SolidColorBrush(Color.FromRgb(220, 220, 230)));
    private static readonly SolidColorBrush BrushUnsung = Freeze(new SolidColorBrush(Color.FromRgb(112, 112, 122)));
    private static readonly CubicEase SmoothEase = new() { EasingMode = EasingMode.EaseOut };

    private static SolidColorBrush Freeze(SolidColorBrush b) { b.Freeze(); return b; }

    public LyricsView()
    {
        InitializeComponent();
        SizeChanged += (_, _) => UpdateSpacersAndCenter(immediate: true);
    }

    public bool CompactMode
    {
        get => _compact;
        set
        {
            _compact = value;
            ApplyModeLayout();
            RebuildAllLines();
            ForceRefresh();
        }
    }

    public double BaseFontSize
    {
        get => _baseFontSize;
        set
        {
            _baseFontSize = Math.Clamp(value, 10, 44);
            PlainText.FontSize = _baseFontSize;
            StatusText.FontSize = _baseFontSize;
            RebuildAllLines();
            ForceRefresh();
        }
    }

    public bool UseTextShadow
    {
        get => _useShadow;
        set
        {
            _useShadow = value;
            SyncedHost.Effect = value
                ? new DropShadowEffect { Color = Colors.Black, BlurRadius = 10, ShadowDepth = 1.5, Opacity = 0.85 }
                : null;
            StatusText.Effect = SyncedHost.Effect;
        }
    }

    public LyricsMode Mode
    {
        get => _mode;
        set
        {
            if (_mode == value) return;
            _mode = value;
            ApplyModeLayout();
            RebuildAllLines();
            ForceRefresh();
        }
    }

    public bool LockActiveLine
    {
        get => _lockActiveLine;
        set
        {
            if (_lockActiveLine == value) return;
            _lockActiveLine = value;
            ApplyModeLayout();
            RebuildAllLines();
            ForceRefresh();
        }
    }

    private bool UseScrollableList => _compact && !_lockActiveLine && _mode != LyricsMode.SingleLine;

    private void ApplyModeLayout()
    {
        bool scrollable = UseScrollableList;
        SyncedHost.OpacityMask = null;
        SyncedScroll.OpacityMask = scrollable
            ? new LinearGradientBrush(
                new GradientStopCollection
                {
                    new(Color.FromArgb(0, 0, 0, 0), 0.0),
                    new(Color.FromArgb(255, 0, 0, 0), 0.22),
                    new(Color.FromArgb(255, 0, 0, 0), 0.78),
                    new(Color.FromArgb(0, 0, 0, 0), 1.0)
                }, 90)
            : null;

        SyncedScroll.Visibility = scrollable ? Visibility.Visible : Visibility.Collapsed;
        LockedGrid.Visibility = scrollable ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnLyricsWheel(object sender, MouseWheelEventArgs e)
    {
        if (!UseScrollableList || _synced.Count == 0) return;
        _manualScrollUntil = DateTime.Now.AddSeconds(4.5);
        double next = SyncedScroll.VerticalOffset - e.Delta * 0.45;
        SyncedScroll.ScrollToVerticalOffset(Math.Clamp(next, 0, SyncedScroll.ScrollableHeight));
        e.Handled = true;
    }

    public void SetStatus(string message)
    {
        _synced = new();
        _plain = null;
        _lineBlocks.Clear();
        _lockedActiveBlock = null;
        _lastIdx = -99;
        _lastReveal = -99;
        _targetScrollOffset = -1;
        LinesPanel.Children.Clear();
        PastPanel.Children.Clear();
        FuturePanel.Children.Clear();
        ActiveHost.Content = null;
        PlainScroll.Visibility = Visibility.Collapsed;
        SyncedHost.Visibility = Visibility.Collapsed;
        StatusText.Text = message;
        StatusText.Visibility = string.IsNullOrEmpty(message) ? Visibility.Collapsed : Visibility.Visible;
    }

    public void SetLyrics(LyricsResult result)
    {
        _synced = result.Synced;
        _plain = result.Plain;
        _lastIdx = -99;
        _lastReveal = -99;
        _lastPos = -99;
        _manualScrollUntil = DateTime.MinValue;
        _targetScrollOffset = -1;

        if (_synced.Count > 0)
        {
            StatusText.Visibility = Visibility.Collapsed;
            PlainScroll.Visibility = Visibility.Collapsed;
            SyncedHost.Visibility = Visibility.Visible;
            ApplyModeLayout();
            RebuildAllLines();
            UpdatePosition(0, force: true);
        }
        else if (!string.IsNullOrWhiteSpace(_plain))
        {
            StatusText.Visibility = Visibility.Collapsed;
            SyncedHost.Visibility = Visibility.Collapsed;
            PlainText.Text = _plain.Trim();
            PlainScroll.ScrollToTop();
            PlainScroll.Visibility = Visibility.Visible;
        }
        else
        {
            SetStatus("Текст не найден");
        }
    }

    public void ForceRefresh()
    {
        _lastIdx = -99;
        _lastReveal = -99;
        if (_synced.Count > 0 && _lastPos >= 0)
            UpdatePosition(_lastPos, force: true);
    }

    public string? GetCurrentLineText(double positionSeconds)
    {
        if (_synced.Count == 0) return null;
        int idx = FindActiveIndex(positionSeconds);
        return idx >= 0 && idx < _synced.Count ? _synced[idx].Text : null;
    }

    private static double OpacityForDistance(int dist, bool isPast)
    {
        if (dist == 0) return 1.0;
        return (dist, isPast) switch
        {
            (1, false) => 0.62,
            (1, true) => 0.48,
            (2, false) => 0.34,
            (2, true) => 0.24,
            (3, false) => 0.16,
            (3, true) => 0.11,
            _ => 0.08
        };
    }

    private void RebuildAllLines()
    {
        LinesPanel.Children.Clear();
        _lineBlocks.Clear();
        _topSpacer = null;
        _bottomSpacer = null;
        _lockedActiveBlock = null;

        if (_synced.Count == 0) return;

        if (UseScrollableList)
        {
            double halfH = Math.Max(60, ActualHeight / 2.0);
            _topSpacer = new Border { Height = halfH };
            _bottomSpacer = new Border { Height = halfH };
            LinesPanel.Children.Add(_topSpacer);

            for (int i = 0; i < _synced.Count; i++)
            {
                int idx = i;
                var tb = new TextBlock
                {
                    Text = _synced[i].Text,
                    TextWrapping = TextWrapping.Wrap,
                    TextAlignment = TextAlignment.Center,
                    FontSize = _baseFontSize,
                    FontWeight = FontWeights.Medium,
                    Foreground = BrushFuture,
                    Opacity = 0.35,
                    Margin = new Thickness(4, 4, 4, 4),
                    LineHeight = _baseFontSize * 1.36,
                    Cursor = Cursors.Hand
                };
                tb.MouseLeftButtonUp += (_, _) =>
                {
                    _manualScrollUntil = DateTime.MinValue;
                    if (idx >= 0 && idx < _synced.Count)
                        LineClicked?.Invoke(_synced[idx].TimeSeconds);
                };
                _lineBlocks.Add(tb);
                LinesPanel.Children.Add(tb);
            }

            LinesPanel.Children.Add(_bottomSpacer);
        }
    }

    private void UpdateSpacersAndCenter(bool immediate)
    {
        if (!UseScrollableList) return;
        if (_topSpacer != null && _bottomSpacer != null && ActualHeight > 20)
        {
            double halfH = ActualHeight / 2.0;
            _topSpacer.Height = halfH;
            _bottomSpacer.Height = halfH;
        }
        if (immediate && _lastIdx >= 0)
            Dispatcher.BeginInvoke(() => ScrollToLine(_lastIdx, immediate: true), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    public void UpdatePosition(double positionSeconds, bool force = false)
    {
        if (_synced.Count == 0) return;

        int idx = FindActiveIndex(positionSeconds);
        if (!force && idx < _lastIdx && _lastIdx >= 0 && positionSeconds > _lastPos - 1.0)
            idx = _lastIdx;

        _lastPos = positionSeconds;

        int revealCount = 0;
        bool blink = false;

        if (idx >= 0 && (_mode == LyricsMode.Karaoke || _mode == LyricsMode.Bounce || _mode == LyricsMode.Typing))
        {
            var cur = _synced[idx];
            double t0 = cur.TimeSeconds;
            double tNext = idx + 1 < _synced.Count ? _synced[idx + 1].TimeSeconds : t0 + 4.5;
            double segDur = Math.Max(0.35, tNext - t0) * 0.85;
            int len = cur.Text.Length;
            double win = _mode == LyricsMode.Typing ? Math.Min(segDur, len * 0.14) : segDur;
            double p = win > 0 ? Math.Clamp((positionSeconds - t0) / win, 0, 1) : 1;
            revealCount = p >= 1 ? len : Math.Min(len, (int)Math.Floor(p * len) + 1);
            blink = _mode == LyricsMode.Typing && ((int)(positionSeconds * 2.2) % 2 == 0);
        }

        if (UseScrollableList && _targetScrollOffset >= 0 && DateTime.Now >= _manualScrollUntil)
        {
            double curOff = SyncedScroll.VerticalOffset;
            double diff = _targetScrollOffset - curOff;
            if (Math.Abs(diff) > 0.6)
                SyncedScroll.ScrollToVerticalOffset(curOff + diff * 0.28);
            else if (Math.Abs(diff) > 0.05)
                SyncedScroll.ScrollToVerticalOffset(_targetScrollOffset);
        }

        if (!force && idx == _lastIdx && revealCount == _lastReveal && blink == _lastBlink)
            return;

        int prevIdx = _lastIdx;
        _lastIdx = idx;
        _lastReveal = revealCount;
        _lastBlink = blink;

        if (UseScrollableList)
            UpdateScrollableLines(prevIdx, idx, revealCount, blink, force);
        else
            UpdateLockedLines(prevIdx, idx, revealCount, blink, force);
    }

    private void UpdateScrollableLines(int prevIdx, int idx, int revealCount, bool blink, bool force)
    {
        if (_lineBlocks.Count != _synced.Count) RebuildAllLines();
        if (_lineBlocks.Count == 0) return;

        bool idxChanged = force || prevIdx != idx;
        var animDur = TimeSpan.FromMilliseconds(260);

        if (idxChanged)
        {
            for (int k = 0; k < _lineBlocks.Count; k++)
            {
                var tb = _lineBlocks[k];
                if (k == idx) continue;

                tb.Inlines.Clear();
                tb.Text = _synced[k].Text;
                tb.FontSize = _baseFontSize;
                tb.FontWeight = FontWeights.Medium;
                bool isFuture = k > idx;
                tb.Foreground = isFuture ? BrushFuture : BrushPast;

                int dist = Math.Abs(k - Math.Max(idx, 0));
                double targetOp = (_mode == LyricsMode.Typing && isFuture) ? 0.0 : OpacityForDistance(dist, !isFuture);
                if (force) tb.Opacity = targetOp;
                else tb.BeginAnimation(OpacityProperty, new DoubleAnimation(targetOp, animDur) { EasingFunction = SmoothEase });
            }
        }

        if (idx >= 0 && idx < _lineBlocks.Count)
        {
            var activeTb = _lineBlocks[idx];
            if (idxChanged)
            {
                if (force) activeTb.Opacity = 1.0;
                else activeTb.BeginAnimation(OpacityProperty, new DoubleAnimation(1.0, animDur) { EasingFunction = SmoothEase });
            }
            StyleActiveLine(activeTb, _synced[idx].Text, revealCount, blink);

            if (idxChanged)
            {
                activeTb.UpdateLayout();
                ScrollToLine(idx, immediate: force);
            }
        }
        else if (idxChanged)
        {
            ScrollToLine(0, immediate: true);
        }
    }

    private void ScrollToLine(int idx, bool immediate)
    {
        if (idx < 0 || idx >= _lineBlocks.Count) return;
        if (DateTime.Now < _manualScrollUntil && !immediate) return;

        try
        {
            var tb = _lineBlocks[idx];
            var pt = tb.TransformToAncestor(LinesPanel).Transform(new Point(0, 0));
            double lineCenterY = pt.Y + tb.ActualHeight / 2.0;
            double viewportH = SyncedScroll.ViewportHeight > 10 ? SyncedScroll.ViewportHeight : ActualHeight;
            if (viewportH <= 10) return;

            double target = Math.Clamp(lineCenterY - viewportH / 2.0, 0, Math.Max(0, LinesPanel.ActualHeight - viewportH));
            _targetScrollOffset = target;
            if (immediate)
                SyncedScroll.ScrollToVerticalOffset(target);
        }
        catch { }
    }

    private void UpdateLockedLines(int prevIdx, int idx, int revealCount, bool blink, bool force)
    {
        bool idxChanged = force || prevIdx != idx || _lockedActiveBlock == null;
        int activeIndex = Math.Max(idx, 0);

        if (idxChanged)
        {
            PastPanel.Children.Clear();
            FuturePanel.Children.Clear();

            int pastCount = _mode == LyricsMode.SingleLine ? 0 : (_compact ? 3 : 1);
            int futureCount = (_mode == LyricsMode.SingleLine || _mode == LyricsMode.Typing) ? 0 : (_compact ? 3 : 1);

            var animDur = TimeSpan.FromMilliseconds(260);
            bool animateSlide = !force && prevIdx >= -1 && idx > prevIdx;

            // Past lines (stacked above active line, fading smoothly upward)
            for (int k = activeIndex - pastCount; k < activeIndex; k++)
            {
                if (k < 0 || k >= _synced.Count) continue;
                int dist = activeIndex - k;
                double targetOp = OpacityForDistance(dist, isPast: true);
                var tb = CreateContextLine(k, isPast: true, targetOp, animateSlide, animDur);
                PastPanel.Children.Add(tb);
            }
            PastScroll.UpdateLayout();
            PastScroll.ScrollToEnd();

            // Active line (locked dead-center in Row 1)
            _lockedActiveBlock = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Center,
                Margin = new Thickness(4, _compact ? 4 : 6, 4, _compact ? 4 : 6),
                LineHeight = (_compact ? _baseFontSize + 1.0 : _baseFontSize + 2.0) * 1.30,
                Cursor = _compact ? Cursors.Hand : Cursors.Arrow
            };
            if (_compact && activeIndex < _synced.Count)
            {
                int clickIdx = activeIndex;
                _lockedActiveBlock.MouseLeftButtonUp += (_, _) =>
                {
                    if (clickIdx >= 0 && clickIdx < _synced.Count)
                        LineClicked?.Invoke(_synced[clickIdx].TimeSeconds);
                };
            }
            if (animateSlide)
            {
                var tr = new TranslateTransform(0, 6);
                _lockedActiveBlock.RenderTransform = tr;
                tr.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(6, 0, animDur) { EasingFunction = SmoothEase });
            }
            _lockedActiveBlock.Opacity = 1.0;
            ActiveHost.Content = _lockedActiveBlock;

            // Future lines (stacked below active line, fading smoothly downward)
            for (int k = activeIndex + 1; k <= activeIndex + futureCount; k++)
            {
                if (k < 0 || k >= _synced.Count) continue;
                int dist = k - activeIndex;
                double targetOp = OpacityForDistance(dist, isPast: false);
                var tb = CreateContextLine(k, isPast: false, targetOp, animateSlide, animDur);
                FuturePanel.Children.Add(tb);
            }
        }

        if (_lockedActiveBlock != null && activeIndex < _synced.Count)
        {
            if (idx >= 0)
                StyleActiveLine(_lockedActiveBlock, _synced[idx].Text, revealCount, blink);
            else
            {
                _lockedActiveBlock.Inlines.Clear();
                _lockedActiveBlock.Text = _synced[0].Text;
                _lockedActiveBlock.FontSize = _baseFontSize;
                _lockedActiveBlock.FontWeight = FontWeights.Medium;
                _lockedActiveBlock.Foreground = BrushFuture;
                _lockedActiveBlock.Opacity = 0.6;
            }
        }
    }

    private TextBlock CreateContextLine(int lineIdx, bool isPast, double targetOpacity, bool animate, TimeSpan dur)
    {
        var tb = new TextBlock
        {
            Text = _synced[lineIdx].Text,
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            FontSize = _baseFontSize - 0.5,
            FontWeight = FontWeights.Medium,
            Foreground = isPast ? BrushPast : BrushFuture,
            Opacity = animate ? Math.Max(0, targetOpacity - 0.18) : targetOpacity,
            Margin = new Thickness(4, _compact ? 3 : 4, 4, _compact ? 3 : 4),
            LineHeight = _baseFontSize * 1.28,
            Cursor = _compact ? Cursors.Hand : Cursors.Arrow
        };

        if (_compact)
            tb.MouseLeftButtonUp += (_, _) =>
            {
                if (lineIdx >= 0 && lineIdx < _synced.Count)
                    LineClicked?.Invoke(_synced[lineIdx].TimeSeconds);
            };

        if (animate)
        {
            var tr = new TranslateTransform(0, 7);
            tb.RenderTransform = tr;
            tr.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(7, 0, dur) { EasingFunction = SmoothEase });
            tb.BeginAnimation(OpacityProperty, new DoubleAnimation(targetOpacity, dur) { EasingFunction = SmoothEase });
        }

        return tb;
    }

    private void StyleActiveLine(TextBlock tb, string lineText, int revealCount, bool blink)
    {
        double activeSize = _compact ? _baseFontSize + 1.0 : _baseFontSize + 2.0;
        tb.Inlines.Clear();
        tb.FontSize = activeSize;
        tb.FontWeight = FontWeights.SemiBold;

        if (_mode == LyricsMode.Karaoke)
        {
            int c = Math.Clamp(revealCount, 0, lineText.Length);
            if (c > 0) tb.Inlines.Add(new Run(lineText[..c]) { Foreground = BrushActive });
            if (c < lineText.Length) tb.Inlines.Add(new Run(lineText[c..]) { Foreground = BrushUnsung });
        }
        else if (_mode == LyricsMode.Bounce)
        {
            // Apple Music style: all sung words/letters stay lifted upward and bright white,
            // while unsung words/letters remain at baseline and dimmed.
            int c = Math.Clamp(revealCount, 0, lineText.Length);
            double liftY = _compact ? -2.2 : -3.5;

            if (c > 0)
            {
                string sung = lineText[..c];
                int pos = 0;
                while (pos < sung.Length)
                {
                    if (char.IsWhiteSpace(sung[pos]))
                    {
                        int start = pos;
                        while (pos < sung.Length && char.IsWhiteSpace(sung[pos])) pos++;
                        tb.Inlines.Add(new Run(sung[start..pos]) { Foreground = BrushActive });
                    }
                    else
                    {
                        int start = pos;
                        while (pos < sung.Length && !char.IsWhiteSpace(sung[pos])) pos++;
                        var wordBlock = new TextBlock
                        {
                            Text = sung[start..pos],
                            Foreground = BrushActive,
                            FontSize = activeSize,
                            FontWeight = FontWeights.SemiBold,
                            RenderTransform = new TranslateTransform(0, liftY)
                        };
                        tb.Inlines.Add(new InlineUIContainer(wordBlock) { BaselineAlignment = BaselineAlignment.Baseline });
                    }
                }
            }

            if (c < lineText.Length)
                tb.Inlines.Add(new Run(lineText[c..]) { Foreground = BrushUnsung });
        }
        else if (_mode == LyricsMode.Typing)
        {
            int c = Math.Clamp(revealCount, 0, lineText.Length);
            bool stillTyping = c < lineText.Length;
            if (c > 0)
                tb.Inlines.Add(new Run(lineText[..c]) { Foreground = BrushActive });
            // Cursor stays solid while actively typing and blinks once the line finishes
            if (stillTyping || blink)
                tb.Inlines.Add(new Run("|") { Foreground = BrushActive });
            else
                tb.Inlines.Add(new Run("|") { Foreground = Brushes.Transparent });
            // Keep untyped characters invisible so the centered line never jumps horizontally or vertically
            if (c < lineText.Length)
                tb.Inlines.Add(new Run(lineText[c..]) { Foreground = Brushes.Transparent });
        }
        else
        {
            tb.Text = lineText;
            tb.Foreground = BrushActive;
        }
    }

    private int FindActiveIndex(double pos)
    {
        int lo = 0, hi = _synced.Count - 1, ans = -1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >> 1;
            if (_synced[mid].TimeSeconds <= pos)
            {
                ans = mid;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }
        return ans;
    }
}
