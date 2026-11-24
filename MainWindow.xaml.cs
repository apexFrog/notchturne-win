using System.Globalization;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using NotchturneWin.Models;
using NotchturneWin.Services;

namespace NotchturneWin;

public partial class MainWindow : Window
{
    [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr h, int i);
    [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr h, int i, int v);
    [DllImport("user32.dll")] static extern bool AddClipboardFormatListener(IntPtr h);
    struct POINT { public int X, Y; }

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_NOACTIVATE = 0x08000000;

    private static readonly Geometry PlayGeometry = Geometry.Parse("M8,5v14l11-7L8,5z");
    private static readonly Geometry PauseGeometry = Geometry.Parse("M6,5h4v14H6V5z M14,5h4v14h-4V5z");
    private static readonly Geometry CopyGeometry = Geometry.Parse("M16,1H4C2.9,1,2,1.9,2,3v14h2V3h12V1z M19,5H8C6.9,5,6,5.9,6,7v14c0,1.1,0.9,2,2,2h11c1.1,0,2-0.9,2-2V7C21,5.9,20.1,5,19,5z M19,21H8V7h11V21z");

    private static readonly SolidColorBrush PillActiveBg = Freeze(new SolidColorBrush(Colors.White));
    private static readonly SolidColorBrush PillActiveFg = Freeze(new SolidColorBrush(Color.FromRgb(17, 17, 20)));
    private static readonly SolidColorBrush PillIdleBg = Freeze(new SolidColorBrush(Color.FromRgb(30, 30, 34)));
    private static readonly SolidColorBrush PillIdleFg = Freeze(new SolidColorBrush(Color.FromRgb(142, 142, 147)));

    private static SolidColorBrush Freeze(SolidColorBrush b) { b.Freeze(); return b; }

    private readonly AppSettings _settings;
    private readonly HttpClient _http = new();
    private readonly MediaService _media;
    private readonly ArtworkService _artwork;
    private readonly LyricsService _lyricsService;
    private readonly List<string> _clips = new();

    private FloatingLyricsWindow? _floatingWindow;

    private CancellationTokenSource? _trackCts;
    private LyricsResult _currentLyrics = new(new(), null, false);
    private TrackSnapshot? _currentTrack;
    private string _trackKey = "";
    private string _hdCoverKey = "";
    private string _defaultMiniText = "Ничего не играет";

    private bool _open;
    private bool _inSettings;
    private bool _syncingUi;
    private bool _lyricsColumnVisible = false;
    private DateTime? _idleSince;
    private DateTime _calendarDate = DateTime.MinValue;
    private Color _coverAccentColor = Color.FromRgb(65, 75, 110);
    private long _lastHoverCheckTicks;
    private static readonly QuarticEase SilkyEase = new() { EasingMode = EasingMode.EaseOut };

    // Dragging, magnetic edge docking & smooth window glide
    private NotchDockEdge _dockEdge = NotchDockEdge.Top;
    private bool _dragPending;
    private bool _isDragging;
    private Point _dragStartCursor;
    private Point _dragStartShellPos;
    private double _dragCurrentX, _dragCurrentY;
    private double _dragTargetX, _dragTargetY;
    private NotchDockEdge _dragPreviewEdge = NotchDockEdge.Top;
    private double _anchorShellX, _anchorShellY;
    private bool _hasCustomAnchor;
    private bool _winAnimActive;
    private double _winTargetLeft, _winTargetTop;
    private long _lastGlideTicks;
    private int _sideTab; // 0 = Lyrics, 1 = Calendar, 2 = Clipboard

    // Corner-wrap L-shape live morph dimensions
    private double _cornerCurW = 284, _cornerCurH = 158, _cornerCurArmW = 58, _cornerCurArmH = 42;
    private double _cornerTgtW = 284, _cornerTgtH = 158, _cornerTgtArmW = 58, _cornerTgtArmH = 42;

    private bool IsEn => string.Equals(_settings.Language, "en", StringComparison.OrdinalIgnoreCase);
    private static bool IsCornerEdge(NotchDockEdge e) =>
        e is NotchDockEdge.CornerTopLeft or NotchDockEdge.CornerTopRight
          or NotchDockEdge.CornerBottomLeft or NotchDockEdge.CornerBottomRight;
    private bool IsCornerMode => _settings.CornerWrapBetaEnabled && IsCornerEdge(_dockEdge);
    private bool IsSideVerticalMode => !IsCornerMode && _settings.SideEdgeAutoExpand && _dockEdge is NotchDockEdge.Left or NotchDockEdge.Right;

    public MainWindow()
    {
        InitializeComponent();

        _settings = AppSettings.Load();
        _settings.ClipboardMaxEntries = Math.Clamp(_settings.ClipboardMaxEntries, 1, 15);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("NotchturneWin/1.1");
        _http.Timeout = TimeSpan.FromSeconds(8);

        _media = new MediaService(_settings);
        _artwork = new ArtworkService(_http);
        _lyricsService = new LyricsService(_http);

        _media.TrackChanged += snap => Dispatcher.Invoke(() => OnTrackChanged(snap));
        _media.PlaybackChanged += (playing, pos, dur) => Dispatcher.Invoke(() => OnPlaybackChanged(playing, pos, dur));
        _media.SeenAppsChanged += () => Dispatcher.Invoke(() => { if (_inSettings) RebuildInlineApps(); });

        NotchLyrics.CompactMode = true;
        NotchLyrics.BaseFontSize = 13;
        NotchLyrics.LineClicked += sec => _media.SeekTo(TimeSpan.FromSeconds(sec));

        SideLyrics.CompactMode = false;
        SideLyrics.BaseFontSize = 14.5;
        SideLyrics.LineClicked += sec => _media.SeekTo(TimeSpan.FromSeconds(sec));

        CornerLyrics.CompactMode = true;
        CornerLyrics.BaseFontSize = 13.5;
        CornerLyrics.LineClicked += sec => _media.SeekTo(TimeSpan.FromSeconds(sec));

        Width = Math.Min(1160, SystemParameters.PrimaryScreenWidth);

        _dockEdge = _settings.DockEdge;
        if (!_settings.CornerWrapBetaEnabled && IsCornerEdge(_dockEdge))
            _dockEdge = NotchDockEdge.Top;

        Height = IsSideVerticalMode
            ? Math.Min(640, SystemParameters.WorkArea.Height)
            : (IsCornerMode ? 480 : 420);

        if (_settings.NotchCustomX.HasValue && _settings.NotchCustomY.HasValue)
        {
            _anchorShellX = _settings.NotchCustomX.Value;
            _anchorShellY = _settings.NotchCustomY.Value;
            _hasCustomAnchor = true;
        }

        ApplySettings(initial: true);
        BuildCalendar(DateTime.Today);
        RebuildClips();

        ApplyDockShape(_dockEdge);
        UpdateLayoutOrientation();
        PositionWindowToAnchor(animate: false);

        Loaded += async (_, _) => await _media.InitializeAsync();
        Closed += (_, _) =>
        {
            CompositionTarget.Rendering -= OnRenderFrame;
            _floatingWindow?.Close();
        };

        CompositionTarget.Rendering += OnRenderFrame;
    }

    private void Center() => Left = (SystemParameters.PrimaryScreenWidth - Width) / 2;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var h = new WindowInteropHelper(this).Handle;
        SetWindowLong(h, GWL_EXSTYLE, GetWindowLong(h, GWL_EXSTYLE) | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);
        AddClipboardFormatListener(h);
        HwndSource.FromHwnd(h).AddHook((IntPtr hw, int msg, IntPtr w, IntPtr l, ref bool handled) =>
        {
            if (msg == 0x031D) OnClip(); // WM_CLIPBOARDUPDATE
            return IntPtr.Zero;
        });
    }

    private void SetNoActivate(bool noActivate)
    {
        var h = new WindowInteropHelper(this).Handle;
        if (h == IntPtr.Zero) return;
        int ex = GetWindowLong(h, GWL_EXSTYLE) | WS_EX_TOOLWINDOW;
        if (noActivate) ex |= WS_EX_NOACTIVATE;
        else ex &= ~WS_EX_NOACTIVATE;
        SetWindowLong(h, GWL_EXSTYLE, ex);
    }

    // ---------- Settings & Layout ----------
    private bool IsMiniLyricsActive() =>
        _settings.ShowLyrics && _settings.ShowLyricsInMiniNotch
        && _currentLyrics.Synced.Count > 0 && _currentTrack is { HasSession: true };

    private bool IsMiniTwoLine() =>
        IsMiniLyricsActive() && _settings.MiniLyricsStyle == MiniLyricsStyle.BelowTitle;

    private double GetCollapsedWidth()
    {
        if (IsSideVerticalMode) return 54;
        return IsMiniLyricsActive() ? 360 : 250;
    }

    private double GetCollapsedHeight()
    {
        if (IsSideVerticalMode) return 190;
        return IsMiniTwoLine() ? 52 : 34;
    }

    private bool HasAnySideBottomPanel() =>
        _lyricsColumnVisible || _settings.ShowCalendar || _settings.ShowClipboard;

    private bool WantNotchLyricsColumn() =>
        _settings.ShowLyrics
        && _settings.LyricsPlacement is LyricsPlacement.Notch or LyricsPlacement.NotchAndFloating
        && _currentLyrics.Found;

    private double GetExpandedWidth()
    {
        if (_inSettings) return 540;
        if (IsSideVerticalMode) return 330;

        bool showLyrics = _lyricsColumnVisible;
        bool showCal = _settings.ShowCalendar;
        bool showClip = _settings.ShowClipboard;

        double w = 340 + 40;
        if (showLyrics) w += 230 + 31;
        if (showCal) w += 188 + 31;
        if (showClip) w += 178 + 31;

        return Math.Min(w, SystemParameters.PrimaryScreenWidth - 24);
    }

    private double GetExpandedHeight()
    {
        if (_inSettings) return 390;
        if (IsSideVerticalMode)
            return HasAnySideBottomPanel()
                ? Math.Min(580, SystemParameters.WorkArea.Height - 24)
                : 316;
        return 212;
    }

    private void UpdateWindowDimensionsForDock()
    {
        double targetWinH = IsSideVerticalMode
            ? Math.Min(640, SystemParameters.WorkArea.Height)
            : (IsCornerMode ? 480 : 420);
        if (Math.Abs(Height - targetWinH) > 1)
            Height = targetWinH;
    }

    private void NotchBgContainer_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateNotchBgClip();

    private void UpdateNotchBgClip()
    {
        double w = NotchBgContainer.ActualWidth;
        double h = NotchBgContainer.ActualHeight;
        if (w <= 1 || h <= 1) return;

        var cr = NotchShell.CornerRadius;
        double maxR = Math.Min(w, h) / 2.0;
        double tl = Math.Clamp(cr.TopLeft, 0, maxR);
        double tr = Math.Clamp(cr.TopRight, 0, maxR);
        double br = Math.Clamp(cr.BottomRight, 0, maxR);
        double bl = Math.Clamp(cr.BottomLeft, 0, maxR);

        var fig = new PathFigure { StartPoint = new Point(tl, 0), IsClosed = true };
        fig.Segments.Add(new LineSegment(new Point(w - tr, 0), true));
        if (tr > 0) fig.Segments.Add(new ArcSegment(new Point(w, tr), new Size(tr, tr), 0, false, SweepDirection.Clockwise, true));
        fig.Segments.Add(new LineSegment(new Point(w, h - br), true));
        if (br > 0) fig.Segments.Add(new ArcSegment(new Point(w - br, h), new Size(br, br), 0, false, SweepDirection.Clockwise, true));
        fig.Segments.Add(new LineSegment(new Point(bl, h), true));
        if (bl > 0) fig.Segments.Add(new ArcSegment(new Point(0, h - bl), new Size(bl, bl), 0, false, SweepDirection.Clockwise, true));
        fig.Segments.Add(new LineSegment(new Point(0, tl), true));
        if (tl > 0) fig.Segments.Add(new ArcSegment(new Point(tl, 0), new Size(tl, tl), 0, false, SweepDirection.Clockwise, true));

        var geo = new PathGeometry(new[] { fig });
        geo.Freeze();
        NotchBgContainer.Clip = geo;
    }

    private void UpdateCornerTargets(bool snapImmediate = false)
    {
        if (_open)
        {
            _cornerTgtW = 580;
            _cornerTgtH = HasAnySideBottomPanel() ? 456 : 168;
            _cornerTgtArmW = 248;
            _cornerTgtArmH = 164;
        }
        else
        {
            _cornerTgtW = 300;
            _cornerTgtH = 148;
            _cornerTgtArmW = 58;
            _cornerTgtArmH = 42;
        }

        if (snapImmediate)
        {
            _cornerCurW = _cornerTgtW;
            _cornerCurH = _cornerTgtH;
            _cornerCurArmW = _cornerTgtArmW;
            _cornerCurArmH = _cornerTgtArmH;
            ApplyCornerGeometry();
        }
    }

    private static PathGeometry BuildCornerLGeometry(double totalW, double totalH, double armW, double armH, NotchDockEdge corner)
    {
        bool flipX = corner is NotchDockEdge.CornerTopLeft or NotchDockEdge.CornerBottomLeft;
        bool flipY = corner is NotchDockEdge.CornerBottomLeft or NotchDockEdge.CornerBottomRight;
        bool flipSweep = flipX ^ flipY;

        Point P(double x, double y) => new(flipX ? totalW - x : x, flipY ? totalH - y : y);
        SweepDirection S(SweepDirection d) => flipSweep
            ? (d == SweepDirection.Clockwise ? SweepDirection.Counterclockwise : SweepDirection.Clockwise)
            : d;

        double rOut = Math.Min(24.0, Math.Min(armH * 0.48, armW * 0.45));
        double vertExtra = totalH - armH;
        var fig = new PathFigure { StartPoint = P(0, 0), IsClosed = true };

        if (vertExtra <= 10)
        {
            // Compact corner rectangle (no vertical lyrics arm)
            fig.Segments.Add(new LineSegment(P(0, totalH - rOut), true));
            fig.Segments.Add(new ArcSegment(P(rOut, totalH), new Size(rOut, rOut), 0, false, S(SweepDirection.Counterclockwise), true));
            fig.Segments.Add(new LineSegment(P(totalW, totalH), true));
            fig.Segments.Add(new LineSegment(P(totalW, 0), true));
        }
        else
        {
            double rIn = Math.Min(22.0, Math.Min(vertExtra * 0.42, (totalW - armW) * 0.42));
            double rOutVert = Math.Min(rOut, vertExtra * 0.45);

            // Outer horizontal arm down-left rounded corner
            fig.Segments.Add(new LineSegment(P(0, armH - rOut), true));
            fig.Segments.Add(new ArcSegment(P(rOut, armH), new Size(rOut, rOut), 0, false, S(SweepDirection.Counterclockwise), true));
            // Along bottom of horizontal arm into smooth concave inner fillet
            fig.Segments.Add(new LineSegment(P(totalW - armW - rIn, armH), true));
            fig.Segments.Add(new ArcSegment(P(totalW - armW, armH + rIn), new Size(rIn, rIn), 0, false, S(SweepDirection.Clockwise), true));
            // Down vertical arm into bottom-left rounded corner
            fig.Segments.Add(new LineSegment(P(totalW - armW, totalH - rOutVert), true));
            fig.Segments.Add(new ArcSegment(P(totalW - armW + rOutVert, totalH), new Size(rOutVert, rOutVert), 0, false, S(SweepDirection.Counterclockwise), true));
            // Flush to screen edge and screen corner
            fig.Segments.Add(new LineSegment(P(totalW, totalH), true));
            fig.Segments.Add(new LineSegment(P(totalW, 0), true));
        }

        var geo = new PathGeometry(new[] { fig });
        geo.Freeze();
        return geo;
    }

    private void ApplyCornerGeometry()
    {
        if (!IsCornerEdge(_dockEdge)) return;

        double w = _cornerCurW;
        double h = _cornerCurH;
        double armW = _cornerCurArmW;
        double armH = _cornerCurArmH;

        CornerShell.Width = w;
        CornerShell.Height = h;

        var geo = BuildCornerLGeometry(w, h, armW, armH, _dockEdge);
        CornerBgPath.Data = geo;
        CornerBgClipHost.Clip = geo;

        bool flipX = _dockEdge is NotchDockEdge.CornerTopLeft or NotchDockEdge.CornerBottomLeft;
        bool flipY = _dockEdge is NotchDockEdge.CornerBottomLeft or NotchDockEdge.CornerBottomRight;

        double horizTop = flipY ? h - armH : 0;
        double vertLeft = flipX ? 0 : w - armW;
        double vertTop = flipY ? 0 : armH;
        double vertH = Math.Max(0, h - armH);

        // Position collapsed content inside the arms & corner junction
        CornerMiniHoriz.Width = Math.Max(100, w - armW);
        CornerMiniHoriz.Height = armH;
        Canvas.SetLeft(CornerMiniHoriz, flipX ? armW : 0);
        Canvas.SetTop(CornerMiniHoriz, horizTop);

        CornerMiniJunction.Width = armW;
        CornerMiniJunction.Height = armH;
        Canvas.SetLeft(CornerMiniJunction, vertLeft);
        Canvas.SetTop(CornerMiniJunction, horizTop);

        CornerMiniVert.Width = armW;
        CornerMiniVert.Height = Math.Max(40, vertH);
        Canvas.SetLeft(CornerMiniVert, vertLeft);
        Canvas.SetTop(CornerMiniVert, vertTop);

        // Position expanded content inside the arms
        CornerFullHoriz.Width = w;
        CornerFullHoriz.Height = armH;
        Canvas.SetLeft(CornerFullHoriz, 0);
        Canvas.SetTop(CornerFullHoriz, horizTop);

        CornerFullVert.Width = armW;
        CornerFullVert.Height = Math.Max(0, vertH);
        Canvas.SetLeft(CornerFullVert, vertLeft);
        Canvas.SetTop(CornerFullVert, vertTop);

        // Divider always sits right between Lyrics and Player (top for top corners, bottom for bottom corners)
        CornerLyricsDividerTop.Visibility = flipY ? Visibility.Collapsed : Visibility.Visible;
        CornerLyricsDividerBot.Visibility = flipY ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateLayoutOrientation(bool snapCornerImmediate = true)
    {
        bool corner = IsCornerMode && !_inSettings;
        bool vert = IsSideVerticalMode;

        NotchShell.Visibility = corner ? Visibility.Collapsed : Visibility.Visible;
        CornerShell.Visibility = corner ? Visibility.Visible : Visibility.Collapsed;

        if (corner)
        {
            CornerShell.HorizontalAlignment = _dockEdge is NotchDockEdge.CornerTopLeft or NotchDockEdge.CornerBottomLeft
                ? HorizontalAlignment.Left
                : HorizontalAlignment.Right;
            CornerShell.VerticalAlignment = _dockEdge is NotchDockEdge.CornerBottomLeft or NotchDockEdge.CornerBottomRight
                ? VerticalAlignment.Bottom
                : VerticalAlignment.Top;

            UpdateCornerTargets(snapImmediate: snapCornerImmediate);
            CornerMiniHoriz.BeginAnimation(OpacityProperty, null);
            CornerMiniJunction.BeginAnimation(OpacityProperty, null);
            CornerMiniVert.BeginAnimation(OpacityProperty, null);
            CornerFullHoriz.BeginAnimation(OpacityProperty, null);
            CornerFullVert.BeginAnimation(OpacityProperty, null);
            CornerMiniHoriz.Opacity = _open ? 0 : 1;
            CornerMiniJunction.Opacity = _open ? 0 : 1;
            CornerMiniVert.Opacity = _open ? 0 : 1;
            CornerFullHoriz.Opacity = _open ? 1 : 0;
            bool hasVertPanel = HasAnySideBottomPanel();
            CornerFullVert.Opacity = (_open && hasVertPanel) ? 1 : 0;
            CornerFullHoriz.IsHitTestVisible = _open;
            CornerFullVert.IsHitTestVisible = _open && hasVertPanel;
            UpdateSideBottomTabs();
            return;
        }

        Mini.Visibility = vert ? Visibility.Collapsed : Visibility.Visible;
        Full.Visibility = vert ? Visibility.Collapsed : Visibility.Visible;
        MiniVertical.Visibility = vert ? Visibility.Visible : Visibility.Collapsed;
        FullVertical.Visibility = vert ? Visibility.Visible : Visibility.Collapsed;

        if (_inSettings)
        {
            Mini.Opacity = 0;
            MiniVertical.Opacity = 0;
            Full.Opacity = 0;
            Full.IsHitTestVisible = false;
            FullVertical.Opacity = 0;
            FullVertical.IsHitTestVisible = false;
        }
        else if (vert)
        {
            MiniVertical.BeginAnimation(OpacityProperty, null);
            FullVertical.BeginAnimation(OpacityProperty, null);
            MiniVertical.Opacity = _open ? 0 : 1;
            FullVertical.Opacity = _open ? 1 : 0;
            FullVertical.IsHitTestVisible = _open;
            Full.IsHitTestVisible = false;
        }
        else
        {
            Mini.BeginAnimation(OpacityProperty, null);
            Full.BeginAnimation(OpacityProperty, null);
            Mini.Opacity = _open ? 0 : 1;
            Full.Opacity = _open ? 1 : 0;
            Full.IsHitTestVisible = _open;
            FullVertical.IsHitTestVisible = false;
        }

        UpdateSideBottomTabs();
    }

    private void UpdateSideBottomTabs()
    {
        bool hasLyrics = _lyricsColumnVisible;
        bool hasCal = _settings.ShowCalendar;
        bool hasClip = _settings.ShowClipboard;
        int count = (hasLyrics ? 1 : 0) + (hasCal ? 1 : 0) + (hasClip ? 1 : 0);

        if (count == 0)
        {
            SideBottomHeader.Visibility = Visibility.Collapsed;
            SideBottomContent.Visibility = Visibility.Collapsed;
            return;
        }

        SideBottomHeader.Visibility = Visibility.Visible;
        SideBottomContent.Visibility = Visibility.Visible;

        // Auto-select a valid tab if current tab isn't available
        if ((_sideTab == 0 && !hasLyrics) || (_sideTab == 1 && !hasCal) || (_sideTab == 2 && !hasClip))
            _sideTab = hasLyrics ? 0 : (hasCal ? 1 : 2);

        SideTabRow.Visibility = count > 1 ? Visibility.Visible : Visibility.Collapsed;
        BtnSideTabLyrics.Visibility = hasLyrics ? Visibility.Visible : Visibility.Collapsed;
        BtnSideTabCal.Visibility = hasCal ? Visibility.Visible : Visibility.Collapsed;
        BtnSideTabClip.Visibility = hasClip ? Visibility.Visible : Visibility.Collapsed;

        SetPill(BtnSideTabLyrics, _sideTab == 0);
        SetPill(BtnSideTabCal, _sideTab == 1);
        SetPill(BtnSideTabClip, _sideTab == 2);

        SideLyrics.Visibility = _sideTab == 0 ? Visibility.Visible : Visibility.Collapsed;
        SideCalendarPanel.Visibility = _sideTab == 1 ? Visibility.Visible : Visibility.Collapsed;
        SideClipboardPanel.Visibility = _sideTab == 2 ? Visibility.Visible : Visibility.Collapsed;

        // Sync Corner L-mode vertical wing tabs as well
        CornerTabRow.Visibility = count > 1 ? Visibility.Visible : Visibility.Collapsed;
        BtnCornerTabLyrics.Visibility = hasLyrics ? Visibility.Visible : Visibility.Collapsed;
        BtnCornerTabCal.Visibility = hasCal ? Visibility.Visible : Visibility.Collapsed;
        BtnCornerTabClip.Visibility = hasClip ? Visibility.Visible : Visibility.Collapsed;

        SetPill(BtnCornerTabLyrics, _sideTab == 0);
        SetPill(BtnCornerTabCal, _sideTab == 1);
        SetPill(BtnCornerTabClip, _sideTab == 2);

        CornerLyrics.Visibility = _sideTab == 0 ? Visibility.Visible : Visibility.Collapsed;
        CornerCalendarPanel.Visibility = _sideTab == 1 ? Visibility.Visible : Visibility.Collapsed;
        CornerClipboardPanel.Visibility = _sideTab == 2 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnSideTabLyrics(object s, RoutedEventArgs e) { _sideTab = 0; UpdateSideBottomTabs(); UpdateLyricsPosition(force: true); }
    private void OnSideTabCal(object s, RoutedEventArgs e) { _sideTab = 1; UpdateSideBottomTabs(); }
    private void OnSideTabClip(object s, RoutedEventArgs e) { _sideTab = 2; UpdateSideBottomTabs(); }

    private void SetLyricsColumnVisible(bool visible, bool animate)
    {
        if (_lyricsColumnVisible == visible && animate) return;
        _lyricsColumnVisible = visible;

        if (visible)
        {
            ColLyrics.Width = new GridLength(230);
            ColLyricsDiv.Width = GridLength.Auto;
            DivLyrics.Visibility = Visibility.Visible;
            NotchLyrics.Visibility = Visibility.Visible;
            UpdateSideBottomTabs();

            if (animate && _open && !_inSettings)
            {
                var d = TimeSpan.FromMilliseconds(360);
                var slideIn = new DoubleAnimation(28, 0, d) { EasingFunction = SilkyEase };
                var fadeIn = new DoubleAnimation(0, 1, d) { EasingFunction = SilkyEase };
                var divFade = new DoubleAnimation(0, 1, d) { EasingFunction = SilkyEase };
                Timeline.SetDesiredFrameRate(slideIn, 60);
                Timeline.SetDesiredFrameRate(fadeIn, 60);
                LyricsSlide.BeginAnimation(TranslateTransform.XProperty, slideIn);
                NotchLyrics.BeginAnimation(OpacityProperty, fadeIn);
                DivLyrics.BeginAnimation(OpacityProperty, divFade);
            }
            else
            {
                LyricsSlide.BeginAnimation(TranslateTransform.XProperty, null);
                LyricsSlide.X = 0;
                NotchLyrics.BeginAnimation(OpacityProperty, null);
                NotchLyrics.Opacity = 1;
                DivLyrics.BeginAnimation(OpacityProperty, null);
                DivLyrics.Opacity = 1;
            }

            if (_open && !_inSettings) AnimateSize(true);
        }
        else
        {
            if (animate && _open && !_inSettings && NotchLyrics.Visibility == Visibility.Visible)
            {
                var d = TimeSpan.FromMilliseconds(260);
                var slideOut = new DoubleAnimation(0, -24, d) { EasingFunction = SilkyEase };
                var fadeOut = new DoubleAnimation(1, 0, d) { EasingFunction = SilkyEase };
                var divFade = new DoubleAnimation(1, 0, d) { EasingFunction = SilkyEase };
                Timeline.SetDesiredFrameRate(slideOut, 60);
                Timeline.SetDesiredFrameRate(fadeOut, 60);

                fadeOut.Completed += (_, _) =>
                {
                    if (_lyricsColumnVisible) return;
                    ColLyrics.Width = new GridLength(0);
                    ColLyricsDiv.Width = new GridLength(0);
                    DivLyrics.Visibility = Visibility.Collapsed;
                    NotchLyrics.Visibility = Visibility.Collapsed;
                    UpdateSideBottomTabs();
                    if (_open && !_inSettings) AnimateSize(true);
                };

                LyricsSlide.BeginAnimation(TranslateTransform.XProperty, slideOut);
                NotchLyrics.BeginAnimation(OpacityProperty, fadeOut);
                DivLyrics.BeginAnimation(OpacityProperty, divFade);
            }
            else
            {
                ColLyrics.Width = new GridLength(0);
                ColLyricsDiv.Width = new GridLength(0);
                DivLyrics.Visibility = Visibility.Collapsed;
                NotchLyrics.Visibility = Visibility.Collapsed;
                LyricsSlide.BeginAnimation(TranslateTransform.XProperty, null);
                LyricsSlide.X = 0;
                NotchLyrics.BeginAnimation(OpacityProperty, null);
                NotchLyrics.Opacity = 0;
                UpdateSideBottomTabs();
                if (_open && !_inSettings) AnimateSize(true);
            }
        }
    }

    private void ApplySettings(bool initial = false)
    {
        ColPlayer.Width = new GridLength(340);
        SetLyricsColumnVisible(WantNotchLyricsColumn(), animate: !initial);
        if (_lyricsColumnVisible) ColLyrics.Width = new GridLength(230);
        bool showFloating = _settings.ShowLyrics && _settings.LyricsPlacement is LyricsPlacement.Floating or LyricsPlacement.NotchAndFloating;

        ColCal.Width = _settings.ShowCalendar ? new GridLength(188) : new GridLength(0);
        ColCalDiv.Width = _settings.ShowCalendar ? GridLength.Auto : new GridLength(0);
        DivCal.Visibility = _settings.ShowCalendar ? Visibility.Visible : Visibility.Collapsed;
        CalendarPanel.Visibility = _settings.ShowCalendar ? Visibility.Visible : Visibility.Collapsed;

        ColClip.Width = _settings.ShowClipboard ? new GridLength(178) : new GridLength(0);
        ColClipDiv.Width = _settings.ShowClipboard ? GridLength.Auto : new GridLength(0);
        DivClip.Visibility = _settings.ShowClipboard ? Visibility.Visible : Visibility.Collapsed;
        ClipboardPanel.Visibility = _settings.ShowClipboard ? Visibility.Visible : Visibility.Collapsed;

        NotchLyrics.Mode = _settings.LyricsMode;
        NotchLyrics.LockActiveLine = _settings.LockActiveLine;
        SideLyrics.Mode = _settings.LyricsMode;
        SideLyrics.LockActiveLine = _settings.LockActiveLine;
        CornerLyrics.Mode = _settings.LyricsMode;
        CornerLyrics.LockActiveLine = _settings.LockActiveLine;

        MiniViz.StyleMode = _settings.VisualizerStyle;
        MiniViz.AccentColor = _coverAccentColor;
        MiniVizVert.StyleMode = _settings.VisualizerStyle;
        MiniVizVert.AccentColor = _coverAccentColor;
        CornerMiniViz.StyleMode = _settings.VisualizerStyle;
        CornerMiniViz.AccentColor = _coverAccentColor;

        SideProgInd.StyleMode = _settings.SideProgressStyle;
        SideProgInd.InvertDirection = _settings.SideProgressBottomToTop;
        SideProgInd.AccentColor = _coverAccentColor;
        CornerMiniProg.StyleMode = _settings.SideProgressStyle;
        CornerMiniProg.InvertDirection = _settings.SideProgressBottomToTop;
        CornerMiniProg.AccentColor = _coverAccentColor;

        UpdateMiniIndicatorVisibility(_media.IsPlaying);
        var modeTip = IsEn
            ? $"Lyrics mode: {ModeDisplayName(_settings.LyricsMode)} (click to switch)"
            : $"Режим текста: {ModeDisplayName(_settings.LyricsMode)} (нажми для смены)";
        ModeQuickBtn.ToolTip = modeTip;
        ModeQuickBtnVert.ToolTip = modeTip;
        ModeQuickBtnCorner.ToolTip = modeTip;

        if (!_settings.CornerWrapBetaEnabled && IsCornerEdge(_dockEdge))
        {
            _dockEdge = NotchDockEdge.Top;
            _settings.DockEdge = NotchDockEdge.Top;
            ApplyDockShape(_dockEdge);
        }

        UpdateWindowDimensionsForDock();
        UpdateLayoutOrientation();
        ApplyNotchBackground();
        ApplyLocalization();
        SyncInlineSettingsControls();

        if (showFloating)
        {
            if (_floatingWindow == null)
            {
                _floatingWindow = new FloatingLyricsWindow(_settings, () => Dispatcher.Invoke(SyncInlineSettingsControls));
                _floatingWindow.SetLyrics(_currentLyrics);
            }
            else
            {
                _floatingWindow.ApplySettings();
            }

            bool shouldShow = (_currentTrack is { HasSession: true } && !string.IsNullOrWhiteSpace(_currentTrack.Title))
                              || !_settings.FloatingClickThrough;
            if (shouldShow) _floatingWindow.Show();
            else _floatingWindow.Hide();
        }
        else
        {
            _floatingWindow?.Hide();
        }

        if (!initial)
        {
            RebuildClips();
            AnimateSize(_open);
            PositionWindowToAnchor(animate: true);
            UpdateLyricsPosition(force: true);
        }
        else
        {
            NotchShell.Width = GetCollapsedWidth();
            NotchShell.Height = GetCollapsedHeight();
        }
    }

    private void ApplyLocalization()
    {
        bool en = IsEn;
        CtxSettings.Header = en ? "Settings…" : "Настройки…";
        CtxCycleMode.Header = en ? "Switch lyrics mode" : "Сменить режим текста";
        CtxResetPos.Header = en ? "Reset position (top center)" : "Сбросить позицию (вверх по центру)";
        CtxQuit.Header = en ? "Quit" : "Выход";
        CtxCornerSettings.Header = CtxSettings.Header;
        CtxCornerCycleMode.Header = CtxCycleMode.Header;
        CtxCornerResetPos.Header = CtxResetPos.Header;
        CtxCornerQuit.Header = CtxQuit.Header;
        ClipHeaderTitle.Text = en ? "CLIPBOARD" : "БУФЕР ОБМЕНА";

        LblSecShow.Text = en ? "WHAT TO SHOW" : "ЧТО ПОКАЗЫВАТЬ";
        LblShowLyrics.Text = en ? "Lyrics" : "Текст песни";
        LblShowCal.Text = en ? "Calendar" : "Календарь";
        LblShowClip.Text = en ? "Clipboard" : "Буфер обмена";
        LblHdArt.Text = en ? "Look up artwork online" : "HD-обложки (Apple Music)";
        LblClipCount.Text = en ? "Clipboard entries" : "Записей в буфере";
        LblLang.Text = en ? "Language" : "Язык";
        LblBgMode.Text = en ? "Notch background" : "Фон острова";
        BtnBgBlack.Content = en ? "Black" : "Чёрный";
        BtnBgGlass.Content = en ? "Glass" : "Стекло";
        BtnBgCoverGlass.Content = en ? "Cover + Glass" : "Обложка + Стекло";
        BtnBgCoverBlack.Content = en ? "Cover + Black" : "Обложка + Чёрный";

        LblCoverShimmer.Text = en ? "Cover color shimmer" : "Перелив цвета обложки";
        LblShimmerStyle.Text = en ? "Shimmer style" : "Стиль перелива";
        BtnShimmerV1.Content = en ? "Variant 1 (Aurora)" : "Вариант 1 (Сияние)";
        BtnShimmerV2.Content = en ? "Variant 2 (Mesh)" : "Вариант 2 (Mesh)";
        LblAllowDrag.Text = en ? "Allow dragging notch" : "Перетаскивание острова";
        LblSideExpand.Text = en ? "Vertical side panel at left/right edge" : "Боковая панель по краям";
        LblSideProgStyle.Text = en ? "Side timer style" : "Таймер сбоку";
        BtnProgRing.Content = en ? "Ring" : "Кольцо";
        BtnProgBar.Content = en ? "Bar" : "Полоса";
        BtnProgDots.Content = en ? "Dots" : "Точки";
        BtnProgTime.Content = en ? "Timer" : "Таймер";
        BtnProgCapsule.Content = en ? "Capsule" : "Капсула";
        LblSideProgDir.Text = en ? "Timer direction" : "Направление таймера";
        BtnProgDirUp.Content = en ? "Bottom to top" : "Снизу вверх";
        BtnProgDirDown.Content = en ? "Top to bottom" : "Сверху вниз";
        LblCornerWrap.Text = en ? "Corner wrap L-mode (Beta)" : "Угловой перегиб (Бета)";
        LblNotchPos.Text = en ? "Notch position" : "Позиция острова";
        BtnResetPos.Content = en ? "Top center" : "Вверх по центру";

        BtnSideTabLyrics.Content = en ? "Lyrics" : "Текст";
        BtnSideTabCal.Content = en ? "Calendar" : "Календарь";
        BtnSideTabClip.Content = en ? "Clipboard" : "Буфер";
        BtnCornerTabLyrics.Content = BtnSideTabLyrics.Content;
        BtnCornerTabCal.Content = BtnSideTabCal.Content;
        BtnCornerTabClip.Content = BtnSideTabClip.Content;

        LblViz.Text = en ? "Visualizer" : "Визуализатор";
        BtnVizBars.Content = en ? "Bars" : "Столбики";
        BtnVizDashes.Content = en ? "Dashes" : "Штрихи";
        BtnVizWave.Content = en ? "Wave" : "Волна";
        BtnVizBlocks.Content = en ? "Blocks" : "Блоки";
        BtnVizNone.Content = en ? "Off" : "Выкл";

        LblSecLyrics.Text = en ? "LYRICS" : "НАСТРОЙКИ ТЕКСТА";
        LblPlacement.Text = en ? "Display location" : "Где показывать";
        BtnPlaceNotch.Content = en ? "Notch" : "Остров";
        BtnPlaceFloat.Content = en ? "Overlay" : "Окно";
        BtnPlaceBoth.Content = en ? "Both" : "Оба";

        LblMode.Text = en ? "Lyrics mode" : "Режим текста";
        BtnModeKaraoke.Content = en ? "Karaoke" : "Караоке";
        BtnModeBounce.Content = en ? "Apple Lift" : "Подъём (Apple)";
        BtnModeTyping.Content = en ? "Typing" : "Печать";
        BtnModeClassic.Content = en ? "Classic" : "Классика";
        BtnModeSingle.Content = en ? "1 line" : "1 строка";

        LblLockLine.Text = en ? "Lock on active line" : "Фиксация на текущей строке";
        LblMiniLyrics.Text = en ? "Show line in collapsed notch" : "Строка в свёрнутом острове";
        LblMiniStyle.Text = en ? "Collapsed notch layout" : "Вид в свёрнутом острове";
        BtnMiniBelow.Content = en ? "Below title" : "Под названием";
        BtnMiniReplace.Content = en ? "Replace title" : "Вместо названия";
        LblOffset.Text = en ? "Lyrics offset (sec)" : "Сдвиг текста (сек)";

        LblSecFloat.Text = en ? "FLOATING OVERLAY" : "ПЛАВАЮЩЕЕ ОКНО";
        LblFloatPos.Text = en ? "Position" : "Позиция";
        BtnPosBottom.Content = en ? "Bottom" : "Снизу";
        BtnPosTop.Content = en ? "Top" : "Сверху";
        BtnPosCustom.Content = en ? "Custom" : "Своя";
        LblFloatSize.Text = en ? "Font size" : "Размер шрифта";
        LblClickThrough.Text = en ? "Click-through (lock)" : "Пропускать клики (закрепить)";

        LblSecMedia.Text = en ? "MEDIA SOURCES" : "ИСТОЧНИКИ МЕДИА";
        LblIgnoreBrowsers.Text = en ? "Ignore browsers" : "Игнорировать браузеры";
        BtnAddBlock.Content = en ? "Block" : "Исключить";

        if (_currentTrack is not { HasSession: true } || string.IsNullOrWhiteSpace(_currentTrack.Title))
        {
            _defaultMiniText = en ? "Nothing playing" : "Ничего не играет";
            TitleText.Text = _defaultMiniText;
            MiniText.Text = _defaultMiniText;
        }

        BuildCalendar(DateTime.Today);
    }

    private void SyncInlineSettingsControls()
    {
        _syncingUi = true;
        SwLyrics.IsChecked = _settings.ShowLyrics;
        SwCalendar.IsChecked = _settings.ShowCalendar;
        SwClipboard.IsChecked = _settings.ShowClipboard;
        SwHdArt.IsChecked = _settings.UseAppleMusicHdCover;
        SwCoverShimmer.IsChecked = _settings.CoverShimmerEnabled;
        SwAllowDrag.IsChecked = _settings.AllowNotchDrag;
        SwSideExpand.IsChecked = _settings.SideEdgeAutoExpand;
        SwCornerWrap.IsChecked = _settings.CornerWrapBetaEnabled;
        SwLockLine.IsChecked = _settings.LockActiveLine;
        SwMiniLyrics.IsChecked = _settings.ShowLyricsInMiniNotch;
        SwClickThrough.IsChecked = _settings.FloatingClickThrough;
        SwIgnoreBrowsers.IsChecked = _settings.IgnoreBrowsers;

        SetPill(BtnProgRing, _settings.SideProgressStyle == SideProgressStyle.Ring);
        SetPill(BtnProgBar, _settings.SideProgressStyle == SideProgressStyle.Bar);
        SetPill(BtnProgDots, _settings.SideProgressStyle == SideProgressStyle.Dots);
        SetPill(BtnProgTime, _settings.SideProgressStyle == SideProgressStyle.Time);
        SetPill(BtnProgCapsule, _settings.SideProgressStyle == SideProgressStyle.Capsule);
        SetPill(BtnProgDirUp, _settings.SideProgressBottomToTop);
        SetPill(BtnProgDirDown, !_settings.SideProgressBottomToTop);

        ShimmerStyleRow.Visibility = _settings.CoverShimmerEnabled ? Visibility.Visible : Visibility.Collapsed;
        ShimmerStyleSep.Visibility = _settings.CoverShimmerEnabled ? Visibility.Visible : Visibility.Collapsed;
        SetPill(BtnShimmerV1, _settings.CoverShimmerStyle == CoverShimmerStyle.Aurora);
        SetPill(BtnShimmerV2, _settings.CoverShimmerStyle == CoverShimmerStyle.SpotifyMesh);

        TxtClipCount.Text = _settings.ClipboardMaxEntries.ToString();
        TxtOffset.Text = $"{(_settings.LyricsOffsetSeconds >= 0 ? "+" : "")}{_settings.LyricsOffsetSeconds:0.0}s";
        TxtFontSize.Text = ((int)_settings.FloatingFontSize).ToString();

        SetPill(BtnLangRu, !IsEn);
        SetPill(BtnLangEn, IsEn);

        SetPill(BtnBgBlack, _settings.NotchBackground == NotchBackgroundMode.Black);
        SetPill(BtnBgGlass, _settings.NotchBackground == NotchBackgroundMode.Glass);
        SetPill(BtnBgCoverGlass, _settings.NotchBackground == NotchBackgroundMode.CoverGlass);
        SetPill(BtnBgCoverBlack, _settings.NotchBackground == NotchBackgroundMode.CoverBlack);

        SetPill(BtnVizBars, _settings.VisualizerStyle == VisualizerStyle.Bars);
        SetPill(BtnVizDashes, _settings.VisualizerStyle == VisualizerStyle.Dashes);
        SetPill(BtnVizWave, _settings.VisualizerStyle == VisualizerStyle.Wave);
        SetPill(BtnVizBlocks, _settings.VisualizerStyle == VisualizerStyle.Blocks);
        SetPill(BtnVizNone, _settings.VisualizerStyle == VisualizerStyle.None);

        SetPill(BtnPlaceNotch, _settings.LyricsPlacement == LyricsPlacement.Notch);
        SetPill(BtnPlaceFloat, _settings.LyricsPlacement == LyricsPlacement.Floating);
        SetPill(BtnPlaceBoth, _settings.LyricsPlacement == LyricsPlacement.NotchAndFloating);

        SetPill(BtnModeKaraoke, _settings.LyricsMode == LyricsMode.Karaoke);
        SetPill(BtnModeBounce, _settings.LyricsMode == LyricsMode.Bounce);
        SetPill(BtnModeTyping, _settings.LyricsMode == LyricsMode.Typing);
        SetPill(BtnModeClassic, _settings.LyricsMode == LyricsMode.Classic);
        SetPill(BtnModeSingle, _settings.LyricsMode == LyricsMode.SingleLine);

        SetPill(BtnMiniBelow, _settings.MiniLyricsStyle == MiniLyricsStyle.BelowTitle);
        SetPill(BtnMiniReplace, _settings.MiniLyricsStyle == MiniLyricsStyle.ReplaceTitle);

        SetPill(BtnPosBottom, _settings.FloatingPosition == FloatingPosition.BottomCenter);
        SetPill(BtnPosTop, _settings.FloatingPosition == FloatingPosition.TopBelowNotch);
        SetPill(BtnPosCustom, _settings.FloatingPosition == FloatingPosition.Custom);

        _syncingUi = false;
    }

    private static void SetPill(Button btn, bool active)
    {
        btn.Background = active ? PillActiveBg : PillIdleBg;
        btn.Foreground = active ? PillActiveFg : PillIdleFg;
    }

    private string ModeDisplayName(LyricsMode mode) => (IsEn, mode) switch
    {
        (true, LyricsMode.Karaoke) => "Karaoke",
        (true, LyricsMode.Bounce) => "Apple Lift",
        (true, LyricsMode.Typing) => "Typing",
        (true, LyricsMode.Classic) => "Classic",
        (true, LyricsMode.SingleLine) => "Single line",
        (false, LyricsMode.Karaoke) => "Караоке",
        (false, LyricsMode.Bounce) => "Подъём (Apple)",
        (false, LyricsMode.Typing) => "Печать",
        (false, LyricsMode.Classic) => "Классический",
        (false, LyricsMode.SingleLine) => "Одна строка",
        _ => mode.ToString()
    };

    // ---------- Inline Settings Handlers ----------
    private void OpenSettings(object sender, RoutedEventArgs e)
    {
        if (_inSettings) return;
        _inSettings = true;
        _open = true;
        SetNoActivate(false);

        SyncInlineSettingsControls();
        RebuildInlineApps();
        RebuildInlineBlockedTags();

        // If currently in Corner L-mode, temporarily reveal NotchShell to host inline settings cleanly
        if (IsCornerMode)
        {
            CornerShell.Visibility = Visibility.Collapsed;
            NotchShell.Visibility = Visibility.Visible;
            ApplyDockShape(_dockEdge);
        }

        SettingsView.Visibility = Visibility.Visible;
        SettingsView.IsHitTestVisible = true;
        Full.IsHitTestVisible = false;
        FullVertical.IsHitTestVisible = false;

        var d = TimeSpan.FromMilliseconds(220);
        Full.BeginAnimation(OpacityProperty, new DoubleAnimation(0, d));
        Mini.BeginAnimation(OpacityProperty, new DoubleAnimation(0, d));
        FullVertical.BeginAnimation(OpacityProperty, new DoubleAnimation(0, d));
        MiniVertical.BeginAnimation(OpacityProperty, new DoubleAnimation(0, d));
        SettingsView.BeginAnimation(OpacityProperty, new DoubleAnimation(1, d));
        AnimateSize(true);
        PositionWindowToAnchor(animate: true);
    }

    private void CloseSettings(object sender, RoutedEventArgs e)
    {
        if (!_inSettings) return;
        _inSettings = false;
        SetNoActivate(true);

        var d = TimeSpan.FromMilliseconds(220);
        SettingsView.IsHitTestVisible = false;
        var fadeOut = new DoubleAnimation(0, d);
        fadeOut.Completed += (_, _) => { if (!_inSettings) SettingsView.Visibility = Visibility.Collapsed; };
        SettingsView.BeginAnimation(OpacityProperty, fadeOut);

        if (IsCornerMode)
        {
            UpdateLayoutOrientation();
            PositionWindowToAnchor(animate: true);
            return;
        }

        if (IsSideVerticalMode)
        {
            FullVertical.IsHitTestVisible = true;
            FullVertical.BeginAnimation(OpacityProperty, new DoubleAnimation(1, d));
        }
        else
        {
            Full.IsHitTestVisible = true;
            Full.BeginAnimation(OpacityProperty, new DoubleAnimation(1, d));
        }
        AnimateSize(true);
        PositionWindowToAnchor(animate: true);
    }

    private void OnInlineToggle(object sender, RoutedEventArgs e)
    {
        if (_syncingUi) return;
        bool wasHd = _settings.UseAppleMusicHdCover;

        _settings.ShowLyrics = SwLyrics.IsChecked == true;
        _settings.ShowCalendar = SwCalendar.IsChecked == true;
        _settings.ShowClipboard = SwClipboard.IsChecked == true;
        _settings.UseAppleMusicHdCover = SwHdArt.IsChecked == true;
        _settings.CoverShimmerEnabled = SwCoverShimmer.IsChecked == true;
        _settings.AllowNotchDrag = SwAllowDrag.IsChecked == true;
        _settings.SideEdgeAutoExpand = SwSideExpand.IsChecked == true;
        _settings.CornerWrapBetaEnabled = SwCornerWrap.IsChecked == true;
        _settings.LockActiveLine = SwLockLine.IsChecked == true;
        _settings.ShowLyricsInMiniNotch = SwMiniLyrics.IsChecked == true;
        bool clickThroughChangedToOff = _settings.FloatingClickThrough && SwClickThrough.IsChecked == false;
        _settings.FloatingClickThrough = SwClickThrough.IsChecked == true;
        if (clickThroughChangedToOff && (!_settings.ShowLyrics || _settings.LyricsPlacement == LyricsPlacement.Notch))
        {
            _settings.ShowLyrics = true;
            _settings.LyricsPlacement = LyricsPlacement.NotchAndFloating;
        }
        _settings.IgnoreBrowsers = SwIgnoreBrowsers.IsChecked == true;

        _settings.Save();
        ApplySettings();
        _media.RefreshSessionFilter();
        RebuildInlineApps();

        if (!wasHd && _settings.UseAppleMusicHdCover && _currentTrack is { HasSession: true } snap && !string.IsNullOrWhiteSpace(snap.Title))
        {
            var ct = _trackCts?.Token ?? CancellationToken.None;
            _ = LoadHdCoverAsync(_trackKey, snap.Title, snap.Artist, snap.Album, ct);
        }
    }

    private void OnClipMinus(object s, RoutedEventArgs e) { _settings.ClipboardMaxEntries = Math.Clamp(_settings.ClipboardMaxEntries - 1, 1, 15); SaveAndApply(); }
    private void OnClipPlus(object s, RoutedEventArgs e) { _settings.ClipboardMaxEntries = Math.Clamp(_settings.ClipboardMaxEntries + 1, 1, 15); SaveAndApply(); }

    private void OnLangRu(object s, RoutedEventArgs e) { _settings.Language = "ru"; SaveAndApply(); RebuildInlineApps(); }
    private void OnLangEn(object s, RoutedEventArgs e) { _settings.Language = "en"; SaveAndApply(); RebuildInlineApps(); }

    private void OnBgBlack(object s, RoutedEventArgs e) { _settings.NotchBackground = NotchBackgroundMode.Black; SaveAndApply(); }
    private void OnBgGlass(object s, RoutedEventArgs e) { _settings.NotchBackground = NotchBackgroundMode.Glass; SaveAndApply(); }
    private void OnBgCoverGlass(object s, RoutedEventArgs e) { _settings.NotchBackground = NotchBackgroundMode.CoverGlass; SaveAndApply(); }
    private void OnBgCoverBlack(object s, RoutedEventArgs e) { _settings.NotchBackground = NotchBackgroundMode.CoverBlack; SaveAndApply(); }

    private void OnShimmerV1(object s, RoutedEventArgs e) { _settings.CoverShimmerEnabled = true; _settings.CoverShimmerStyle = CoverShimmerStyle.Aurora; SaveAndApply(); }
    private void OnShimmerV2(object s, RoutedEventArgs e) { _settings.CoverShimmerEnabled = true; _settings.CoverShimmerStyle = CoverShimmerStyle.SpotifyMesh; SaveAndApply(); }

    private void OnProgRing(object s, RoutedEventArgs e) { _settings.SideProgressStyle = SideProgressStyle.Ring; SaveAndApply(); }
    private void OnProgBar(object s, RoutedEventArgs e) { _settings.SideProgressStyle = SideProgressStyle.Bar; SaveAndApply(); }
    private void OnProgDots(object s, RoutedEventArgs e) { _settings.SideProgressStyle = SideProgressStyle.Dots; SaveAndApply(); }
    private void OnProgTime(object s, RoutedEventArgs e) { _settings.SideProgressStyle = SideProgressStyle.Time; SaveAndApply(); }
    private void OnProgCapsule(object s, RoutedEventArgs e) { _settings.SideProgressStyle = SideProgressStyle.Capsule; SaveAndApply(); }
    private void OnProgDirUp(object s, RoutedEventArgs e) { _settings.SideProgressBottomToTop = true; SaveAndApply(); }
    private void OnProgDirDown(object s, RoutedEventArgs e) { _settings.SideProgressBottomToTop = false; SaveAndApply(); }

    private void ResetNotchPosition(object sender, RoutedEventArgs e)
    {
        _dockEdge = NotchDockEdge.Top;
        _hasCustomAnchor = false;
        _settings.DockEdge = NotchDockEdge.Top;
        _settings.NotchCustomX = null;
        _settings.NotchCustomY = null;
        _settings.Save();
        ApplyDockShape(_dockEdge);
        UpdateWindowDimensionsForDock();
        UpdateLayoutOrientation();
        ApplySettings();
        PositionWindowToAnchor(animate: true);
    }

    private void OnVizBars(object s, RoutedEventArgs e) { _settings.VisualizerStyle = VisualizerStyle.Bars; SaveAndApply(); }
    private void OnVizDashes(object s, RoutedEventArgs e) { _settings.VisualizerStyle = VisualizerStyle.Dashes; SaveAndApply(); }
    private void OnVizWave(object s, RoutedEventArgs e) { _settings.VisualizerStyle = VisualizerStyle.Wave; SaveAndApply(); }
    private void OnVizBlocks(object s, RoutedEventArgs e) { _settings.VisualizerStyle = VisualizerStyle.Blocks; SaveAndApply(); }
    private void OnVizNone(object s, RoutedEventArgs e) { _settings.VisualizerStyle = VisualizerStyle.None; SaveAndApply(); }

    private void UpdateMiniIndicatorVisibility(bool playing)
    {
        bool hasTrack = _currentTrack is { HasSession: true } && !string.IsNullOrWhiteSpace(_currentTrack.Title);
        MiniViz.IsPlaying = playing && hasTrack;
        MiniVizVert.IsPlaying = playing && hasTrack;
        CornerMiniViz.IsPlaying = playing && hasTrack;
        if (_settings.VisualizerStyle == VisualizerStyle.None)
        {
            MiniViz.Visibility = Visibility.Collapsed;
            MiniDot.Visibility = (playing && hasTrack) ? Visibility.Visible : Visibility.Collapsed;
            MiniVizVert.Visibility = Visibility.Collapsed;
            MiniDotVert.Visibility = (playing && hasTrack) ? Visibility.Visible : Visibility.Collapsed;
            CornerMiniViz.Visibility = Visibility.Collapsed;
            CornerMiniDot.Visibility = (playing && hasTrack) ? Visibility.Visible : Visibility.Collapsed;
        }
        else
        {
            MiniDot.Visibility = Visibility.Collapsed;
            MiniViz.Visibility = hasTrack ? Visibility.Visible : Visibility.Collapsed;
            MiniDotVert.Visibility = Visibility.Collapsed;
            MiniVizVert.Visibility = hasTrack ? Visibility.Visible : Visibility.Collapsed;
            CornerMiniDot.Visibility = Visibility.Collapsed;
            CornerMiniViz.Visibility = hasTrack ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void ApplyNotchBackground()
    {
        var c = _coverAccentColor;
        bool shimmer = _settings.CoverShimmerEnabled;

        switch (_settings.NotchBackground)
        {
            case NotchBackgroundMode.Glass:
                NotchShell.Background = new LinearGradientBrush(
                    Color.FromArgb(208, 18, 18, 24),
                    Color.FromArgb(182, 8, 8, 13),
                    90);
                NotchShell.BorderBrush = new SolidColorBrush(Color.FromArgb(58, 255, 255, 255));
                NotchGlowLayer.Background = new LinearGradientBrush(
                    Color.FromArgb(22, 255, 255, 255),
                    Color.FromArgb(0, 255, 255, 255),
                    90);
                NotchGlowLayer.Visibility = Visibility.Visible;
                NotchShimmer.Intensity = 0.88;
                break;

            case NotchBackgroundMode.CoverGlass:
                NotchShell.Background = new LinearGradientBrush(
                    Color.FromArgb(196, 14, 14, 20),
                    Color.FromArgb(174, 6, 6, 10),
                    90);
                NotchShell.BorderBrush = new SolidColorBrush(Color.FromArgb(54, 255, 255, 255));
                if (!shimmer)
                {
                    NotchGlowLayer.Background = new RadialGradientBrush
                    {
                        GradientOrigin = new Point(0.16, 0.5),
                        Center = new Point(0.16, 0.5),
                        RadiusX = 0.62,
                        RadiusY = 1.15,
                        GradientStops = new GradientStopCollection
                        {
                            new(Color.FromArgb(128, c.R, c.G, c.B), 0.0),
                            new(Color.FromArgb(52, c.R, c.G, c.B), 0.45),
                            new(Color.FromArgb(0, c.R, c.G, c.B), 1.0)
                        }
                    };
                    NotchGlowLayer.Visibility = Visibility.Visible;
                }
                else
                {
                    NotchGlowLayer.Visibility = Visibility.Collapsed;
                }
                NotchShimmer.Intensity = 1.0;
                break;

            case NotchBackgroundMode.CoverBlack:
                NotchShell.Background = new SolidColorBrush(Color.FromRgb(0, 0, 0));
                NotchShell.BorderBrush = new SolidColorBrush(Color.FromArgb(40, 255, 255, 255));
                if (!shimmer)
                {
                    NotchGlowLayer.Background = new RadialGradientBrush
                    {
                        GradientOrigin = new Point(0.16, 0.5),
                        Center = new Point(0.16, 0.5),
                        RadiusX = 0.60,
                        RadiusY = 1.15,
                        GradientStops = new GradientStopCollection
                        {
                            new(Color.FromArgb(140, c.R, c.G, c.B), 0.0),
                            new(Color.FromArgb(50, c.R, c.G, c.B), 0.45),
                            new(Color.FromArgb(0, c.R, c.G, c.B), 1.0)
                        }
                    };
                    NotchGlowLayer.Visibility = Visibility.Visible;
                }
                else
                {
                    NotchGlowLayer.Visibility = Visibility.Collapsed;
                }
                NotchShimmer.Intensity = 1.05;
                break;

            default: // Black
                NotchShell.Background = new SolidColorBrush(Color.FromRgb(0, 0, 0));
                NotchShell.BorderBrush = new SolidColorBrush(Color.FromArgb(26, 255, 255, 255));
                NotchGlowLayer.Visibility = Visibility.Collapsed;
                NotchShimmer.Intensity = 0.85;
                break;
        }

        NotchShimmer.IsActive = shimmer;
        NotchShimmer.StyleMode = _settings.CoverShimmerStyle;
        NotchShimmer.Visibility = shimmer ? Visibility.Visible : Visibility.Collapsed;
        NotchShimmer.InvalidateVisual();

        // Sync CornerShell background, glow & shimmer identically
        CornerBgPath.Fill = NotchShell.Background;
        CornerBgPath.Stroke = NotchShell.BorderBrush;
        CornerGlowLayer.Background = NotchGlowLayer.Background;
        CornerGlowLayer.Visibility = NotchGlowLayer.Visibility;
        CornerShimmer.Intensity = NotchShimmer.Intensity;
        CornerShimmer.IsActive = shimmer;
        CornerShimmer.StyleMode = _settings.CoverShimmerStyle;
        CornerShimmer.Visibility = shimmer ? Visibility.Visible : Visibility.Collapsed;
        CornerShimmer.InvalidateVisual();
    }

    private void OnPlaceNotch(object s, RoutedEventArgs e) { _settings.ShowLyrics = true; _settings.LyricsPlacement = LyricsPlacement.Notch; SaveAndApply(); }
    private void OnPlaceFloat(object s, RoutedEventArgs e) { _settings.ShowLyrics = true; _settings.LyricsPlacement = LyricsPlacement.Floating; SaveAndApply(); }
    private void OnPlaceBoth(object s, RoutedEventArgs e) { _settings.ShowLyrics = true; _settings.LyricsPlacement = LyricsPlacement.NotchAndFloating; SaveAndApply(); }

    private void OnModeKaraoke(object s, RoutedEventArgs e) { _settings.LyricsMode = LyricsMode.Karaoke; SaveAndApply(); }
    private void OnModeBounce(object s, RoutedEventArgs e) { _settings.LyricsMode = LyricsMode.Bounce; SaveAndApply(); }
    private void OnModeTyping(object s, RoutedEventArgs e) { _settings.LyricsMode = LyricsMode.Typing; SaveAndApply(); }
    private void OnModeClassic(object s, RoutedEventArgs e) { _settings.LyricsMode = LyricsMode.Classic; SaveAndApply(); }
    private void OnModeSingle(object s, RoutedEventArgs e) { _settings.LyricsMode = LyricsMode.SingleLine; SaveAndApply(); }

    private void OnMiniBelow(object s, RoutedEventArgs e) { _settings.MiniLyricsStyle = MiniLyricsStyle.BelowTitle; SaveAndApply(); }
    private void OnMiniReplace(object s, RoutedEventArgs e) { _settings.MiniLyricsStyle = MiniLyricsStyle.ReplaceTitle; SaveAndApply(); }

    private void OnOffsetMinus(object s, RoutedEventArgs e) { _settings.LyricsOffsetSeconds = Math.Round(Math.Clamp(_settings.LyricsOffsetSeconds - 0.1, -3, 3), 1); SaveAndApply(); }
    private void OnOffsetPlus(object s, RoutedEventArgs e) { _settings.LyricsOffsetSeconds = Math.Round(Math.Clamp(_settings.LyricsOffsetSeconds + 0.1, -3, 3), 1); SaveAndApply(); }

    private void EnsureFloatingVisible()
    {
        if (!_settings.ShowLyrics || _settings.LyricsPlacement == LyricsPlacement.Notch)
        {
            _settings.ShowLyrics = true;
            _settings.LyricsPlacement = LyricsPlacement.NotchAndFloating;
        }
    }

    private void OnFloatBottom(object s, RoutedEventArgs e) { EnsureFloatingVisible(); _settings.FloatingPosition = FloatingPosition.BottomCenter; SaveAndApply(); }
    private void OnFloatTop(object s, RoutedEventArgs e) { EnsureFloatingVisible(); _settings.FloatingPosition = FloatingPosition.TopBelowNotch; SaveAndApply(); }
    private void OnFloatCustom(object s, RoutedEventArgs e) { EnsureFloatingVisible(); _settings.FloatingPosition = FloatingPosition.Custom; _settings.FloatingClickThrough = false; SaveAndApply(); }

    private void OnFontMinus(object s, RoutedEventArgs e) { EnsureFloatingVisible(); _settings.FloatingFontSize = Math.Clamp(_settings.FloatingFontSize - 2, 14, 36); SaveAndApply(); }
    private void OnFontPlus(object s, RoutedEventArgs e) { EnsureFloatingVisible(); _settings.FloatingFontSize = Math.Clamp(_settings.FloatingFontSize + 2, 14, 36); SaveAndApply(); }

    private void SaveAndApply()
    {
        _settings.Save();
        ApplySettings();
    }

    private void RebuildInlineApps()
    {
        InlineAppsPanel.Children.Clear();
        var apps = _media.GetSeenApps();
        if (apps.Count == 0)
        {
            InlineAppsPanel.Children.Add(new TextBlock
            {
                Text = IsEn ? "Play something to see active media apps" : "Включи музыку, чтобы увидеть плееры",
                Foreground = new SolidColorBrush(Color.FromRgb(105, 105, 114)),
                FontSize = 12,
                Margin = new Thickness(0, 2, 0, 4)
            });
            return;
        }

        foreach (var app in apps)
        {
            bool blockedManually = _settings.BlockedApps.Any(b => b.Equals(app.AppId, StringComparison.OrdinalIgnoreCase));
            bool blockedByBrowser = _settings.IgnoreBrowsers && app.IsBrowser;

            var row = new Grid { Margin = new Thickness(0, 4, 0, 4) };
            var label = new TextBlock
            {
                Text = app.DisplayName + (app.IsBrowser ? (IsEn ? " (browser)" : " (браузер)") : "") +
                       (app.AppId.Equals(_media.ActiveAppId, StringComparison.OrdinalIgnoreCase) ? " •" : ""),
                Foreground = new SolidColorBrush(Color.FromRgb(225, 225, 232)),
                FontSize = 13,
                VerticalAlignment = VerticalAlignment.Center
            };

            var sw = new CheckBox
            {
                Style = (Style)FindResource("IosSwitch"),
                HorizontalAlignment = HorizontalAlignment.Right,
                IsChecked = !blockedManually && !blockedByBrowser,
                IsEnabled = !blockedByBrowser
            };

            var id = app.AppId;
            sw.Click += (_, _) =>
            {
                if (sw.IsChecked == true)
                    _settings.BlockedApps.RemoveAll(b => b.Equals(id, StringComparison.OrdinalIgnoreCase));
                else if (!_settings.BlockedApps.Any(b => b.Equals(id, StringComparison.OrdinalIgnoreCase)))
                    _settings.BlockedApps.Add(id);

                _settings.Save();
                _media.RefreshSessionFilter();
                RebuildInlineBlockedTags();
            };

            row.Children.Add(label);
            row.Children.Add(sw);
            InlineAppsPanel.Children.Add(row);
        }
    }

    private void RebuildInlineBlockedTags()
    {
        InlineBlockedTags.Children.Clear();
        foreach (var token in _settings.BlockedApps.ToList())
        {
            var tag = token;
            var pill = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(32, 32, 38)),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(8, 3, 6, 3),
                Margin = new Thickness(0, 0, 6, 6)
            };
            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            sp.Children.Add(new TextBlock
            {
                Text = MediaService.DescribeApp(tag).DisplayName,
                FontSize = 11.5,
                Foreground = new SolidColorBrush(Color.FromRgb(210, 210, 218)),
                VerticalAlignment = VerticalAlignment.Center
            });
            var rm = new Button
            {
                Content = "✕",
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Foreground = new SolidColorBrush(Color.FromRgb(140, 140, 150)),
                FontSize = 10,
                Margin = new Thickness(6, 0, 0, 0),
                Cursor = Cursors.Hand
            };
            rm.Click += (_, _) =>
            {
                _settings.BlockedApps.Remove(tag);
                _settings.Save();
                _media.RefreshSessionFilter();
                RebuildInlineApps();
                RebuildInlineBlockedTags();
            };
            sp.Children.Add(rm);
            pill.Child = sp;
            InlineBlockedTags.Children.Add(pill);
        }
    }

    private void OnInlineAddBlock(object sender, RoutedEventArgs e)
    {
        var txt = InlineBlockInput.Text.Trim();
        if (string.IsNullOrWhiteSpace(txt)) return;
        if (!_settings.BlockedApps.Any(b => b.Equals(txt, StringComparison.OrdinalIgnoreCase)))
        {
            _settings.BlockedApps.Add(txt);
            _settings.Save();
            _media.RefreshSessionFilter();
            RebuildInlineApps();
            RebuildInlineBlockedTags();
        }
        InlineBlockInput.Clear();
    }

    // ---------- Smooth Dragging & Magnetic Edge Docking ----------
    private FrameworkElement ActiveShellElement => (IsCornerMode && !_inSettings) ? CornerShell : NotchShell;

    private (double X, double Y, double W, double H) GetNotchScreenRect()
    {
        var el = ActiveShellElement;
        double w = el.ActualWidth > 0 ? el.ActualWidth : el.Width;
        double h = el.ActualHeight > 0 ? el.ActualHeight : el.Height;
        double winW = ActualWidth > 0 ? ActualWidth : Width;
        double winH = ActualHeight > 0 ? ActualHeight : Height;

        double x = el.HorizontalAlignment switch
        {
            HorizontalAlignment.Left => Left,
            HorizontalAlignment.Right => Left + winW - w,
            _ => Left + (winW - w) / 2.0
        };

        double y = el.VerticalAlignment switch
        {
            VerticalAlignment.Top => Top,
            VerticalAlignment.Bottom => Top + winH - h,
            _ => Top + (winH - h) / 2.0
        };

        return (x, y, w, h);
    }

    private (double WinLeft, double WinTop) ComputeWindowCoordsForShell(double shellX, double shellY, double shellW, double shellH)
    {
        var el = ActiveShellElement;
        double winW = ActualWidth > 0 ? ActualWidth : Width;
        double winH = ActualHeight > 0 ? ActualHeight : Height;

        double winLeft = el.HorizontalAlignment switch
        {
            HorizontalAlignment.Left => shellX,
            HorizontalAlignment.Right => shellX + shellW - winW,
            _ => shellX + shellW / 2.0 - winW / 2.0
        };

        double winTop = el.VerticalAlignment switch
        {
            VerticalAlignment.Top => shellY,
            VerticalAlignment.Bottom => shellY + shellH - winH,
            _ => shellY + shellH / 2.0 - winH / 2.0
        };

        return (winLeft, winTop);
    }

    private void SetShellScreenPosition(double shellX, double shellY)
    {
        var el = ActiveShellElement;
        double w = el.ActualWidth > 0 ? el.ActualWidth : el.Width;
        double h = el.ActualHeight > 0 ? el.ActualHeight : el.Height;
        var (wl, wt) = ComputeWindowCoordsForShell(shellX, shellY, w, h);
        Left = wl;
        Top = wt;
    }

    private void ApplyDockCorners(NotchDockEdge edge)
    {
        switch (edge)
        {
            case NotchDockEdge.Top:
                NotchShell.CornerRadius = new CornerRadius(0, 0, 26, 26);
                NotchShell.BorderThickness = new Thickness(1, 0, 1, 1);
                break;
            case NotchDockEdge.Bottom:
                NotchShell.CornerRadius = new CornerRadius(26, 26, 0, 0);
                NotchShell.BorderThickness = new Thickness(1, 1, 1, 0);
                break;
            case NotchDockEdge.Left:
                NotchShell.CornerRadius = new CornerRadius(0, 26, 26, 0);
                NotchShell.BorderThickness = new Thickness(0, 1, 1, 1);
                break;
            case NotchDockEdge.Right:
                NotchShell.CornerRadius = new CornerRadius(26, 0, 0, 26);
                NotchShell.BorderThickness = new Thickness(1, 1, 0, 1);
                break;
            case NotchDockEdge.CornerTopLeft:
                NotchShell.CornerRadius = new CornerRadius(0, 0, 26, 0);
                NotchShell.BorderThickness = new Thickness(0, 0, 1, 1);
                break;
            case NotchDockEdge.CornerTopRight:
                NotchShell.CornerRadius = new CornerRadius(0, 0, 0, 26);
                NotchShell.BorderThickness = new Thickness(1, 0, 0, 1);
                break;
            case NotchDockEdge.CornerBottomLeft:
                NotchShell.CornerRadius = new CornerRadius(0, 26, 0, 0);
                NotchShell.BorderThickness = new Thickness(0, 1, 1, 0);
                break;
            case NotchDockEdge.CornerBottomRight:
                NotchShell.CornerRadius = new CornerRadius(26, 0, 0, 0);
                NotchShell.BorderThickness = new Thickness(1, 1, 0, 0);
                break;
            default: // Floating
                NotchShell.CornerRadius = new CornerRadius(26);
                NotchShell.BorderThickness = new Thickness(1);
                break;
        }
        UpdateNotchBgClip();
    }

    private void ApplyDockShape(NotchDockEdge edge)
    {
        ApplyDockCorners(edge);

        NotchShell.HorizontalAlignment = edge switch
        {
            NotchDockEdge.Left or NotchDockEdge.CornerTopLeft or NotchDockEdge.CornerBottomLeft => HorizontalAlignment.Left,
            NotchDockEdge.Right or NotchDockEdge.CornerTopRight or NotchDockEdge.CornerBottomRight => HorizontalAlignment.Right,
            _ => HorizontalAlignment.Center
        };

        NotchShell.VerticalAlignment = edge switch
        {
            NotchDockEdge.Top or NotchDockEdge.CornerTopLeft or NotchDockEdge.CornerTopRight => VerticalAlignment.Top,
            NotchDockEdge.Bottom or NotchDockEdge.CornerBottomLeft or NotchDockEdge.CornerBottomRight => VerticalAlignment.Bottom,
            _ => VerticalAlignment.Center
        };

        Full.RenderTransformOrigin = edge switch
        {
            NotchDockEdge.Top => new Point(0.5, 0),
            NotchDockEdge.Bottom => new Point(0.5, 1),
            NotchDockEdge.Left => new Point(0, 0.5),
            NotchDockEdge.Right => new Point(1, 0.5),
            _ => new Point(0.5, 0.5)
        };
    }

    private void PositionWindowToAnchor(bool animate)
    {
        double screenW = SystemParameters.PrimaryScreenWidth;
        double workBottom = SystemParameters.WorkArea.Bottom;

        if (IsCornerMode && !_inSettings)
        {
            double winW = ActualWidth > 0 ? ActualWidth : Width;
            double winH = ActualHeight > 0 ? ActualHeight : Height;
            double targetLeftC = _dockEdge is NotchDockEdge.CornerTopLeft or NotchDockEdge.CornerBottomLeft ? 0 : Math.Max(0, screenW - winW);
            double targetTopC = _dockEdge is NotchDockEdge.CornerBottomLeft or NotchDockEdge.CornerBottomRight ? Math.Max(0, workBottom - winH) : 0;

            if (animate)
            {
                _winTargetLeft = targetLeftC;
                _winTargetTop = targetTopC;
                _winAnimActive = true;
            }
            else
            {
                _winAnimActive = false;
                Left = targetLeftC;
                Top = targetTopC;
            }
            return;
        }

        double w = _open ? GetExpandedWidth() : GetCollapsedWidth();
        double h = _open ? GetExpandedHeight() : GetCollapsedHeight();

        double shellX, shellY;
        switch (_dockEdge)
        {
            case NotchDockEdge.Top:
                shellX = _hasCustomAnchor
                    ? Math.Clamp(_anchorShellX, 0, Math.Max(0, screenW - w))
                    : (screenW - w) / 2.0;
                shellY = 0;
                break;

            case NotchDockEdge.Bottom:
                shellX = _hasCustomAnchor
                    ? Math.Clamp(_anchorShellX, 0, Math.Max(0, screenW - w))
                    : (screenW - w) / 2.0;
                shellY = Math.Max(0, workBottom - h);
                break;

            case NotchDockEdge.Left:
                shellX = 0;
                shellY = _hasCustomAnchor
                    ? Math.Clamp(_anchorShellY, 0, Math.Max(0, workBottom - h))
                    : (workBottom - h) / 2.0;
                break;

            case NotchDockEdge.Right:
                shellX = Math.Max(0, screenW - w);
                shellY = _hasCustomAnchor
                    ? Math.Clamp(_anchorShellY, 0, Math.Max(0, workBottom - h))
                    : (workBottom - h) / 2.0;
                break;

            case NotchDockEdge.CornerTopLeft:
                shellX = 0; shellY = 0;
                break;
            case NotchDockEdge.CornerTopRight:
                shellX = Math.Max(0, screenW - w); shellY = 0;
                break;
            case NotchDockEdge.CornerBottomLeft:
                shellX = 0; shellY = Math.Max(0, workBottom - h);
                break;
            case NotchDockEdge.CornerBottomRight:
                shellX = Math.Max(0, screenW - w); shellY = Math.Max(0, workBottom - h);
                break;

            default: // Floating
                shellX = _hasCustomAnchor
                    ? Math.Clamp(_anchorShellX, 0, Math.Max(0, screenW - w))
                    : (screenW - w) / 2.0;
                shellY = _hasCustomAnchor
                    ? Math.Clamp(_anchorShellY, 0, Math.Max(0, workBottom - h))
                    : 48;
                break;
        }

        // Keep expanded shell fully inside screen bounds
        if (_open)
        {
            if (_dockEdge is NotchDockEdge.Top or NotchDockEdge.Bottom or NotchDockEdge.Floating)
            {
                double anchorCenterX = _hasCustomAnchor ? _anchorShellX + GetCollapsedWidth() / 2.0 : screenW / 2.0;
                shellX = Math.Clamp(anchorCenterX - w / 2.0, 8, Math.Max(8, screenW - w - 8));
            }
            if (_dockEdge is NotchDockEdge.Left or NotchDockEdge.Right or NotchDockEdge.Floating)
            {
                double anchorCenterY = _hasCustomAnchor ? _anchorShellY + GetCollapsedHeight() / 2.0 : workBottom / 2.0;
                shellY = Math.Clamp(anchorCenterY - h / 2.0, 8, Math.Max(8, workBottom - h - 8));
            }
        }

        var (targetLeft, targetTop) = ComputeWindowCoordsForShell(shellX, shellY, w, h);

        if (animate)
        {
            _winTargetLeft = targetLeft;
            _winTargetTop = targetTop;
            _winAnimActive = true;
        }
        else
        {
            _winAnimActive = false;
            Left = targetLeft;
            Top = targetTop;
        }
    }

    private Point GetCursorDip()
    {
        GetCursorPos(out var c);
        if (PresentationSource.FromVisual(this) is { } src)
            return src.CompositionTarget.TransformFromDevice.Transform(new Point(c.X, c.Y));
        return new Point(c.X, c.Y);
    }

    private (double X, double Y, NotchDockEdge Edge) ComputeMagneticTarget(double rawX, double rawY, double w, double h)
    {
        double screenW = SystemParameters.PrimaryScreenWidth;
        double workBottom = SystemParameters.WorkArea.Bottom;
        const double edgeSnap = 44.0;
        const double cornerSnap = 74.0;
        const double centerSnap = 52.0;

        double clampedX = Math.Clamp(rawX, 0, Math.Max(0, screenW - w));
        double clampedY = Math.Clamp(rawY, 0, Math.Max(0, workBottom - h));

        double distTop = clampedY;
        double distBottom = Math.Max(0, workBottom - (clampedY + h));
        double distLeft = clampedX;
        double distRight = Math.Max(0, screenW - (clampedX + w));

        // Beta: Corner Wrap L-Mode detection when close to BOTH a horizontal and a vertical edge
        if (_settings.CornerWrapBetaEnabled)
        {
            bool nearTop = distTop <= cornerSnap;
            bool nearBot = distBottom <= cornerSnap;
            bool nearLeft = distLeft <= cornerSnap;
            bool nearRight = distRight <= cornerSnap;

            if (nearTop && nearRight) return (Math.Max(0, screenW - w), 0, NotchDockEdge.CornerTopRight);
            if (nearTop && nearLeft) return (0, 0, NotchDockEdge.CornerTopLeft);
            if (nearBot && nearRight) return (Math.Max(0, screenW - w), Math.Max(0, workBottom - h), NotchDockEdge.CornerBottomRight);
            if (nearBot && nearLeft) return (0, Math.Max(0, workBottom - h), NotchDockEdge.CornerBottomLeft);
        }

        double minDist = Math.Min(Math.Min(distTop, distBottom), Math.Min(distLeft, distRight));
        if (minDist <= edgeSnap)
        {
            if (minDist == distTop)
            {
                double sx = Math.Abs((clampedX + w / 2.0) - screenW / 2.0) <= centerSnap
                    ? (screenW - w) / 2.0
                    : clampedX;
                return (sx, 0, NotchDockEdge.Top);
            }
            if (minDist == distBottom)
            {
                double sx = Math.Abs((clampedX + w / 2.0) - screenW / 2.0) <= centerSnap
                    ? (screenW - w) / 2.0
                    : clampedX;
                return (sx, Math.Max(0, workBottom - h), NotchDockEdge.Bottom);
            }
            if (minDist == distLeft)
            {
                return (0, clampedY, NotchDockEdge.Left);
            }
            return (Math.Max(0, screenW - w), clampedY, NotchDockEdge.Right);
        }

        return (clampedX, clampedY, NotchDockEdge.Floating);
    }

    private static DependencyObject? GetVisualOrLogicalParent(DependencyObject dep)
    {
        if (dep is Visual or System.Windows.Media.Media3D.Visual3D)
            return VisualTreeHelper.GetParent(dep);
        if (dep is ContentElement ce)
            return ContentOperations.GetParent(ce) ?? LogicalTreeHelper.GetParent(ce);
        return LogicalTreeHelper.GetParent(dep);
    }

    private void NotchShell_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!_settings.AllowNotchDrag) return;
        if (e.ChangedButton != MouseButton.Left) return;
        // Don't start dragging when clicking interactive controls (buttons, textboxes, sliders, checkboxes)
        DependencyObject? dep = e.OriginalSource as DependencyObject;
        while (dep != null && dep != NotchShell && dep != CornerShell)
        {
            if (dep is System.Windows.Controls.Primitives.ButtonBase
                or System.Windows.Controls.Primitives.TextBoxBase
                or System.Windows.Controls.Primitives.RangeBase
                or System.Windows.Controls.Primitives.ScrollBar)
                return;
            if (dep == SeekTrack || dep == SeekTrackVert || dep == SeekTrackCorner) return;
            dep = GetVisualOrLogicalParent(dep);
        }

        var rect = GetNotchScreenRect();
        _dragStartCursor = GetCursorDip();
        _dragStartShellPos = new Point(rect.X, rect.Y);
        _dragCurrentX = _dragTargetX = rect.X;
        _dragCurrentY = _dragTargetY = rect.Y;
        _dragPreviewEdge = _dockEdge;
        _dragPending = true;
        _isDragging = false;
    }

    private void NotchShell_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragPending && !_isDragging) return;
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            FinishDrag();
            return;
        }

        var cur = GetCursorDip();
        double dx = cur.X - _dragStartCursor.X;
        double dy = cur.Y - _dragStartCursor.Y;

        if (!_isDragging)
        {
            if (Math.Sqrt(dx * dx + dy * dy) < 6.0) return;
            _isDragging = true;
            _winAnimActive = false;
            _idleSince = null;
            ActiveShellElement.CaptureMouse();
            Cursor = Cursors.SizeAll;
        }

        var (_, _, w, h) = GetNotchScreenRect();
        var (snapX, snapY, edge) = ComputeMagneticTarget(_dragStartShellPos.X + dx, _dragStartShellPos.Y + dy, w, h);
        // Gentle magnetic attraction while dragging (45% pull toward edge, full smooth snap on release)
        double rawClampedX = Math.Clamp(_dragStartShellPos.X + dx, 0, Math.Max(0, SystemParameters.PrimaryScreenWidth - w));
        double rawClampedY = Math.Clamp(_dragStartShellPos.Y + dy, 0, Math.Max(0, SystemParameters.WorkArea.Bottom - h));
        if (edge != NotchDockEdge.Floating)
        {
            _dragTargetX = rawClampedX + (snapX - rawClampedX) * 0.45;
            _dragTargetY = rawClampedY + (snapY - rawClampedY) * 0.45;
        }
        else
        {
            _dragTargetX = rawClampedX;
            _dragTargetY = rawClampedY;
        }

        if (edge != _dragPreviewEdge)
        {
            _dragPreviewEdge = edge;
            if (!IsCornerMode) ApplyDockCorners(edge);
        }
    }

    private void NotchShell_MouseLeftButtonUp(object sender, MouseButtonEventArgs e) => FinishDrag();
    private void NotchShell_LostMouseCapture(object sender, MouseEventArgs e)
    {
        if (_isDragging) FinishDrag();
    }

    private void FinishDrag()
    {
        bool wasDragging = _isDragging;
        _dragPending = false;
        _isDragging = false;
        if (NotchShell.IsMouseCaptured) NotchShell.ReleaseMouseCapture();
        if (CornerShell.IsMouseCaptured) CornerShell.ReleaseMouseCapture();
        Cursor = Cursors.Arrow;

        if (!wasDragging) return;

        var rect = GetNotchScreenRect();
        var (tx, ty, newEdge) = ComputeMagneticTarget(_dragTargetX, _dragTargetY, rect.W, rect.H);

        bool edgeChanged = newEdge != _dockEdge;
        _dockEdge = newEdge;

        // Save anchor in collapsed-friendly coordinates
        double screenW = SystemParameters.PrimaryScreenWidth;
        double workBottom = SystemParameters.WorkArea.Bottom;
        double collapsedW = GetCollapsedWidth();
        double collapsedH = GetCollapsedHeight();

        if (newEdge == NotchDockEdge.Top && Math.Abs((tx + rect.W / 2.0) - screenW / 2.0) <= 6)
        {
            _hasCustomAnchor = false;
            _settings.NotchCustomX = null;
            _settings.NotchCustomY = null;
        }
        else if (IsCornerEdge(newEdge))
        {
            _hasCustomAnchor = false;
            _settings.NotchCustomX = null;
            _settings.NotchCustomY = null;
        }
        else
        {
            _hasCustomAnchor = true;
            _anchorShellX = Math.Clamp(tx + (rect.W - collapsedW) / 2.0, 0, Math.Max(0, screenW - collapsedW));
            _anchorShellY = Math.Clamp(ty + (rect.H - collapsedH) / 2.0, 0, Math.Max(0, workBottom - collapsedH));
            if (newEdge == NotchDockEdge.Top) _anchorShellY = 0;
            else if (newEdge == NotchDockEdge.Bottom) _anchorShellY = Math.Max(0, workBottom - collapsedH);
            else if (newEdge == NotchDockEdge.Left) _anchorShellX = 0;
            else if (newEdge == NotchDockEdge.Right) _anchorShellX = Math.Max(0, screenW - collapsedW);

            _settings.NotchCustomX = _anchorShellX;
            _settings.NotchCustomY = _anchorShellY;
        }

        _settings.DockEdge = _dockEdge;
        _settings.Save();

        // Switch alignment & orientation while preserving exact current screen position (zero jump), then glide into snap!
        bool enteringCorner = IsCornerMode && edgeChanged;
        if (enteringCorner)
        {
            // Seed corner L-shape from current notch dimensions so it bends smoothly into the corner!
            _cornerCurW = Math.Clamp(rect.W, 220, 580);
            _cornerCurH = Math.Clamp(rect.H, 42, 168);
            _cornerCurArmW = _open ? 180 : 58;
            _cornerCurArmH = Math.Clamp(rect.H, 42, 164);
        }

        ApplyDockShape(_dockEdge);
        UpdateWindowDimensionsForDock();
        UpdateLayoutOrientation(snapCornerImmediate: !enteringCorner);

        if (enteringCorner)
        {
            ApplyCornerGeometry();
            var fadeVert = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(320)) { EasingFunction = SilkyEase };
            if (_open && HasAnySideBottomPanel()) CornerFullVert.BeginAnimation(OpacityProperty, fadeVert);
            else if (!_open) CornerMiniVert.BeginAnimation(OpacityProperty, fadeVert);
        }

        SetShellScreenPosition(rect.X, rect.Y);

        if (edgeChanged)
        {
            AnimateSize(_open);
            UpdateLyricsPosition(force: true);
        }

        PositionWindowToAnchor(animate: true);
    }

    private void StepDragAndWindowGlide(long nowTicks)
    {
        double dt = _lastGlideTicks == 0 ? 0.016 : Math.Min(0.05, (nowTicks - _lastGlideTicks) / (double)TimeSpan.TicksPerSecond);
        _lastGlideTicks = nowTicks;

        // Smoothly morph CornerShell L-shape dimensions at 60 FPS
        if (IsCornerMode && !_inSettings)
        {
            double m = 1.0 - Math.Exp(-dt * 10.0);
            double dw = _cornerTgtW - _cornerCurW;
            double dh = _cornerTgtH - _cornerCurH;
            double daw = _cornerTgtArmW - _cornerCurArmW;
            double dah = _cornerTgtArmH - _cornerCurArmH;
            if (Math.Abs(dw) > 0.2 || Math.Abs(dh) > 0.2 || Math.Abs(daw) > 0.2 || Math.Abs(dah) > 0.2)
            {
                _cornerCurW += dw * m;
                _cornerCurH += dh * m;
                _cornerCurArmW += daw * m;
                _cornerCurArmH += dah * m;
                ApplyCornerGeometry();
            }
        }

        if (_isDragging)
        {
            double follow = 1.0 - Math.Exp(-dt * 16.5);
            _dragCurrentX += (_dragTargetX - _dragCurrentX) * follow;
            _dragCurrentY += (_dragTargetY - _dragCurrentY) * follow;
            SetShellScreenPosition(_dragCurrentX, _dragCurrentY);
            return;
        }

        if (_winAnimActive)
        {
            double glide = 1.0 - Math.Exp(-dt * 12.5);
            double dx = _winTargetLeft - Left;
            double dy = _winTargetTop - Top;
            if (Math.Abs(dx) < 0.35 && Math.Abs(dy) < 0.35)
            {
                Left = _winTargetLeft;
                Top = _winTargetTop;
                _winAnimActive = false;
            }
            else
            {
                Left += dx * glide;
                Top += dy * glide;
            }
        }
    }

    private bool IsPointInsideCornerArms(Point p)
    {
        var rect = GetNotchScreenRect();
        double pad = 10.0;
        bool flipX = _dockEdge is NotchDockEdge.CornerTopLeft or NotchDockEdge.CornerBottomLeft;
        bool flipY = _dockEdge is NotchDockEdge.CornerBottomLeft or NotchDockEdge.CornerBottomRight;

        // Horizontal arm rect
        double hx = rect.X;
        double hy = flipY ? rect.Y + rect.H - _cornerCurArmH : rect.Y;
        bool inHoriz = p.X >= hx - pad && p.X <= hx + rect.W + pad
                    && p.Y >= hy - pad && p.Y <= hy + _cornerCurArmH + pad;

        // Vertical arm rect
        double vx = flipX ? rect.X : rect.X + rect.W - _cornerCurArmW;
        double vy = rect.Y;
        bool inVert = p.X >= vx - pad && p.X <= vx + _cornerCurArmW + pad
                   && p.Y >= vy - pad && p.Y <= vy + rect.H + pad;

        return inHoriz || inVert;
    }

    // ---------- Hover & 60 FPS Animation ----------
    private void OnRenderFrame(object? sender, EventArgs e)
    {
        long nowTicks = DateTime.UtcNow.Ticks;

        StepDragAndWindowGlide(nowTicks);

        if (!_open)
        {
            if (IsCornerMode) CornerMiniViz.StepFrame(nowTicks);
            else if (IsSideVerticalMode) MiniVizVert.StepFrame(nowTicks);
            else MiniViz.StepFrame(nowTicks);
        }

        if (IsCornerMode && !_inSettings)
            CornerShimmer.StepFrame(nowTicks, _media.IsPlaying);
        else
            NotchShimmer.StepFrame(nowTicks, _media.IsPlaying);

        // Poll hover & calendar every ~25ms to keep CPU ultra-low
        if (nowTicks - _lastHoverCheckTicks >= TimeSpan.TicksPerMillisecond * 25)
        {
            _lastHoverCheckTicks = nowTicks;

            if (DateTime.Today != _calendarDate)
                BuildCalendar(DateTime.Today);

            if (!_inSettings && !_isDragging && PresentationSource.FromVisual(this) is { } src)
            {
                GetCursorPos(out var c);
                var p = src.CompositionTarget.TransformFromDevice.Transform(new Point(c.X, c.Y));

                bool inside;
                if (IsCornerMode)
                {
                    inside = IsPointInsideCornerArms(p);
                }
                else
                {
                    var rect = GetNotchScreenRect();
                    inside = p.X >= rect.X - 8 && p.X <= rect.X + rect.W + 8
                          && p.Y >= rect.Y - 8 && p.Y <= rect.Y + rect.H + 8;
                }
                bool menuOpen = NotchShell.ContextMenu?.IsOpen == true || CornerShell.ContextMenu?.IsOpen == true;

                if (inside || menuOpen)
                {
                    _idleSince = null;
                    if (!_open) SetOpen(true);
                }
                else if (_open)
                {
                    _idleSince ??= DateTime.Now;
                    if ((DateTime.Now - _idleSince.Value).TotalMilliseconds > 360)
                        SetOpen(false);
                }
            }
        }

        UpdateTimelineAndLyrics();
    }

    private void SetOpen(bool expand)
    {
        _open = expand;
        AnimateSize(expand);
        PositionWindowToAnchor(animate: true);

        var fullOp = new DoubleAnimation(expand ? 1 : 0, TimeSpan.FromMilliseconds(expand ? 280 : 150))
        {
            BeginTime = expand ? TimeSpan.FromMilliseconds(45) : TimeSpan.Zero,
            EasingFunction = SilkyEase
        };
        var miniOp = new DoubleAnimation(expand ? 0 : 1, TimeSpan.FromMilliseconds(expand ? 130 : 240))
        {
            BeginTime = expand ? TimeSpan.Zero : TimeSpan.FromMilliseconds(60),
            EasingFunction = SilkyEase
        };
        var scale = new DoubleAnimation(expand ? 1.0 : 0.965, TimeSpan.FromMilliseconds(expand ? 320 : 220))
        {
            EasingFunction = SilkyEase
        };
        Timeline.SetDesiredFrameRate(fullOp, 60);
        Timeline.SetDesiredFrameRate(miniOp, 60);
        Timeline.SetDesiredFrameRate(scale, 60);

        if (IsCornerMode)
        {
            bool showVert = expand && HasAnySideBottomPanel();
            CornerFullHoriz.BeginAnimation(OpacityProperty, fullOp);
            CornerFullVert.BeginAnimation(OpacityProperty, new DoubleAnimation(showVert ? 1 : 0, TimeSpan.FromMilliseconds(expand ? 280 : 150)) { EasingFunction = SilkyEase });
            CornerMiniHoriz.BeginAnimation(OpacityProperty, miniOp);
            CornerMiniJunction.BeginAnimation(OpacityProperty, miniOp);
            CornerMiniVert.BeginAnimation(OpacityProperty, miniOp);
            CornerFullHoriz.IsHitTestVisible = expand;
            CornerFullVert.IsHitTestVisible = showVert;
            return;
        }

        if (IsSideVerticalMode)
        {
            FullVertical.BeginAnimation(OpacityProperty, fullOp);
            MiniVertical.BeginAnimation(OpacityProperty, miniOp);
            FullVertScale.BeginAnimation(ScaleTransform.ScaleXProperty, scale);
            FullVertScale.BeginAnimation(ScaleTransform.ScaleYProperty, scale);
            FullVertical.IsHitTestVisible = expand;
            Full.IsHitTestVisible = false;
        }
        else
        {
            Full.BeginAnimation(OpacityProperty, fullOp);
            Mini.BeginAnimation(OpacityProperty, miniOp);
            FullScale.BeginAnimation(ScaleTransform.ScaleXProperty, scale);
            FullScale.BeginAnimation(ScaleTransform.ScaleYProperty, scale);
            Full.IsHitTestVisible = expand;
            FullVertical.IsHitTestVisible = false;
        }
    }

    private void AnimateSize(bool expand)
    {
        if (IsCornerMode && !_inSettings)
        {
            UpdateCornerTargets(snapImmediate: false);
            return;
        }
        var d = TimeSpan.FromMilliseconds(expand ? 360 : 300);
        bool twoLine = IsMiniTwoLine();
        Mini.Height = twoLine ? 52 : 34;
        double artSize = twoLine ? 28 : 20;
        MiniArtBox.Width = artSize;
        MiniArtBox.Height = artSize;
        var artRadius = new CornerRadius(twoLine ? 8 : 6);
        MiniArtBox.CornerRadius = artRadius;
        MiniArtLayer.CornerRadius = artRadius;
        MiniLyricText.Visibility = twoLine ? Visibility.Visible : Visibility.Collapsed;

        double targetW = expand ? GetExpandedWidth() : GetCollapsedWidth();
        double targetH = expand ? GetExpandedHeight() : GetCollapsedHeight();

        // Animate NotchShell inside the fixed transparent window (zero Win32 WM_SIZE / SetWindowPos jitter!)
        var wAnim = new DoubleAnimation(targetW, d) { EasingFunction = SilkyEase };
        var hAnim = new DoubleAnimation(targetH, d) { EasingFunction = SilkyEase };
        Timeline.SetDesiredFrameRate(wAnim, 60);
        Timeline.SetDesiredFrameRate(hAnim, 60);

        NotchShell.BeginAnimation(WidthProperty, wAnim);
        NotchShell.BeginAnimation(HeightProperty, hAnim);
    }

    // ---------- Media Handling ----------
    private void OnTrackChanged(TrackSnapshot snap)
    {
        _currentTrack = snap;

        if (!snap.HasSession || string.IsNullOrWhiteSpace(snap.Title))
        {
            _trackKey = "";
            _hdCoverKey = "";
            _trackCts?.Cancel();
            _currentLyrics = new(new(), null, false);

            _defaultMiniText = IsEn ? "Nothing playing" : "Ничего не играет";
            TitleText.Text = _defaultMiniText;
            ArtistText.Text = "";
            TitleTextVert.Text = _defaultMiniText;
            ArtistTextVert.Text = "";
            CornerTitleText.Text = _defaultMiniText;
            CornerArtistText.Text = "";
            MiniText.Text = _defaultMiniText;
            CornerMiniText.Text = _defaultMiniText;
            MiniLyricText.Text = "";
            MiniLyricText.Visibility = Visibility.Collapsed;
            UpdateMiniIndicatorVisibility(false);
            SetLyricsColumnVisible(false, animate: true);
            if (!_open) AnimateSize(false);
            ArtBrush.ImageSource = null;
            MiniArtBrush.ImageSource = null;
            ArtVertBrush.ImageSource = null;
            MiniVertArtBrush.ImageSource = null;
            CornerArtBrush.ImageSource = null;
            CornerMiniArtBrush.ImageSource = null;
            var emptyAvg = Color.FromRgb(28, 32, 48);
            NotchShimmer.SetPalette(Array.Empty<Color>(), emptyAvg);
            CornerShimmer.SetPalette(Array.Empty<Color>(), emptyAvg);
            SourceBadge.Visibility = Visibility.Collapsed;
            SourceBadgeVert.Visibility = Visibility.Collapsed;
            CornerSourceBadge.Visibility = Visibility.Collapsed;
            var idleDotBrush = new SolidColorBrush(Color.FromRgb(75, 75, 82));
            SourceStatusDot.Background = idleDotBrush;
            SourceStatusDotVert.Background = idleDotBrush;
            SourceStatusDotCorner.Background = idleDotBrush;
            var idleTip = IsEn ? "No active media source" : "Нет активного источника медиа";
            SourceStatusBtn.ToolTip = idleTip;
            SourceStatusBtnVert.ToolTip = idleTip;
            SourceStatusBtnCorner.ToolTip = idleTip;
            ProgressFill.Width = 0;
            ProgressFillVert.Width = 0;
            ProgressFillCorner.Width = 0;
            SideProgInd.UpdateProgress(0, "0:00", "-0:00");
            CornerMiniProg.UpdateProgress(0, "0:00", "-0:00");
            ElapsedText.Text = "0:00";
            RemainingText.Text = "-0:00";
            ElapsedTextVert.Text = "0:00";
            RemainingTextVert.Text = "-0:00";
            ElapsedTextCorner.Text = "0:00";
            RemainingTextCorner.Text = "-0:00";

            NotchLyrics.SetStatus("");
            SideLyrics.SetStatus("");
            CornerLyrics.SetStatus("");
            _floatingWindow?.SetStatus("");
            if (_settings.FloatingClickThrough) _floatingWindow?.Hide();
            return;
        }

        var (cleanTitle, cleanArtist) = MetadataHelper.CleanMeta(snap.Title, snap.Artist);
        TitleText.Text = cleanTitle;
        ArtistText.Text = cleanArtist;
        TitleTextVert.Text = cleanTitle;
        ArtistTextVert.Text = cleanArtist;
        CornerTitleText.Text = cleanTitle;
        CornerArtistText.Text = cleanArtist;
        _defaultMiniText = string.IsNullOrWhiteSpace(cleanArtist) ? cleanTitle : $"{cleanArtist} — {cleanTitle}";
        CornerMiniText.Text = _defaultMiniText;
        if (!_settings.ShowLyrics || !_settings.ShowLyricsInMiniNotch || _currentLyrics.Synced.Count == 0)
            MiniText.Text = _defaultMiniText;

        var badgeBrush = new SolidColorBrush(snap.AppInfo.BadgeColor);
        SourceBadge.Background = badgeBrush;
        SourceBadgeText.Text = snap.AppInfo.BadgeText;
        SourceBadge.Visibility = Visibility.Visible;
        SourceBadgeVert.Background = badgeBrush;
        SourceBadgeTextVert.Text = snap.AppInfo.BadgeText;
        SourceBadgeVert.Visibility = Visibility.Visible;
        CornerSourceBadge.Background = badgeBrush;
        CornerSourceBadgeText.Text = snap.AppInfo.BadgeText;
        CornerSourceBadge.Visibility = Visibility.Visible;
        SourceStatusDot.Background = badgeBrush;
        SourceStatusDotVert.Background = badgeBrush;
        SourceStatusDotCorner.Background = badgeBrush;
        var srcTip = IsEn
            ? $"Source: {snap.AppInfo.DisplayName} (click for settings)"
            : $"Источник: {snap.AppInfo.DisplayName} (нажми для настроек)";
        SourceStatusBtn.ToolTip = srcTip;
        SourceStatusBtnVert.ToolTip = srcTip;
        SourceStatusBtnCorner.ToolTip = srcTip;
        MiniDot.Fill = badgeBrush;
        MiniDotVert.Fill = badgeBrush;
        CornerMiniDot.Fill = badgeBrush;

        var newKey = $"{snap.Artist}|{snap.Title}";

        // Never overwrite an already-loaded Apple Music HD cover with Windows thumbnail for the same track
        if (snap.Thumbnail != null && _hdCoverKey != newKey)
        {
            ArtBrush.ImageSource = snap.Thumbnail;
            MiniArtBrush.ImageSource = snap.Thumbnail;
            ArtVertBrush.ImageSource = snap.Thumbnail;
            MiniVertArtBrush.ImageSource = snap.Thumbnail;
            CornerArtBrush.ImageSource = snap.Thumbnail;
            CornerMiniArtBrush.ImageSource = snap.Thumbnail;
            _coverAccentColor = ArtworkService.ExtractAccentColor(snap.Thumbnail);
            var (pal, avg) = ArtworkService.ExtractCoverPalette(snap.Thumbnail);
            NotchShimmer.SetPalette(pal, avg);
            CornerShimmer.SetPalette(pal, avg);
            MiniViz.AccentColor = _coverAccentColor;
            MiniVizVert.AccentColor = _coverAccentColor;
            CornerMiniViz.AccentColor = _coverAccentColor;
            SideProgInd.AccentColor = _coverAccentColor;
            CornerMiniProg.AccentColor = _coverAccentColor;
            ApplyNotchBackground();
        }
        UpdateMiniIndicatorVisibility(_media.IsPlaying);

        bool showFloating = _settings.ShowLyrics && _settings.LyricsPlacement is LyricsPlacement.Floating or LyricsPlacement.NotchAndFloating;
        if (showFloating && _floatingWindow != null && !_floatingWindow.IsVisible)
            _floatingWindow.Show();

        if (newKey != _trackKey)
        {
            _trackKey = newKey;
            _hdCoverKey = "";
            _trackCts?.Cancel();
            _trackCts = new CancellationTokenSource();
            var ct = _trackCts.Token;

            _ = LoadLyricsAsync(snap.Artist, snap.Title, snap.Album, ct);
            if (_settings.UseAppleMusicHdCover)
                _ = LoadHdCoverAsync(newKey, snap.Title, snap.Artist, snap.Album, ct);
        }
    }

    private void OnPlaybackChanged(bool playing, TimeSpan pos, TimeSpan dur)
    {
        var icon = playing ? PauseGeometry : PlayGeometry;
        PlayPauseIcon.Data = icon;
        PlayPauseIconVert.Data = icon;
        PlayPauseIconCorner.Data = icon;
        UpdateMiniIndicatorVisibility(playing);
        UpdateProgressVisual(pos, dur);
    }

    private async Task LoadHdCoverAsync(string expectedKey, string title, string artist, string album, CancellationToken ct)
    {
        var hd = await _artwork.FetchHdCoverAsync(title, artist, album, ct);
        if (ct.IsCancellationRequested || hd == null) return;

        Dispatcher.Invoke(() =>
        {
            if (ct.IsCancellationRequested || _trackKey != expectedKey) return;
            _hdCoverKey = expectedKey;
            ArtBrush.ImageSource = hd;
            MiniArtBrush.ImageSource = hd;
            ArtVertBrush.ImageSource = hd;
            MiniVertArtBrush.ImageSource = hd;
            CornerArtBrush.ImageSource = hd;
            CornerMiniArtBrush.ImageSource = hd;
            _coverAccentColor = ArtworkService.ExtractAccentColor(hd);
            var (pal, avg) = ArtworkService.ExtractCoverPalette(hd);
            NotchShimmer.SetPalette(pal, avg);
            CornerShimmer.SetPalette(pal, avg);
            MiniViz.AccentColor = _coverAccentColor;
            MiniVizVert.AccentColor = _coverAccentColor;
            CornerMiniViz.AccentColor = _coverAccentColor;
            SideProgInd.AccentColor = _coverAccentColor;
            CornerMiniProg.AccentColor = _coverAccentColor;
            ApplyNotchBackground();
        });
    }

    private async Task LoadLyricsAsync(string artist, string title, string album, CancellationToken ct)
    {
        _currentLyrics = new(new(), null, false);
        var searching = IsEn ? "Searching lyrics…" : "Ищу текст…";
        Dispatcher.Invoke(() =>
        {
            if (_lyricsColumnVisible)
            {
                NotchLyrics.SetStatus(searching);
                SideLyrics.SetStatus(searching);
                CornerLyrics.SetStatus(searching);
            }
            _floatingWindow?.SetStatus(searching);
        });

        if (_media.Duration <= TimeSpan.Zero)
        {
            try { await Task.Delay(250, ct); } catch { return; }
        }

        var result = await _lyricsService.FetchLyricsAsync(artist, title, album, _media.Duration.TotalSeconds, ct);
        if (ct.IsCancellationRequested) return;

        _currentLyrics = result;
        Dispatcher.Invoke(() =>
        {
            if (ct.IsCancellationRequested) return;
            NotchLyrics.SetLyrics(result);
            SideLyrics.SetLyrics(result);
            CornerLyrics.SetLyrics(result);
            _floatingWindow?.SetLyrics(result);
            SetLyricsColumnVisible(WantNotchLyricsColumn(), animate: true);
            if (IsCornerMode && !_inSettings)
            {
                UpdateCornerTargets(snapImmediate: false);
                bool showVert = _open && HasAnySideBottomPanel();
                CornerFullVert.Opacity = showVert ? 1 : 0;
                CornerFullVert.IsHitTestVisible = showVert;
            }
            if (!_open && _settings.ShowLyrics && _settings.ShowLyricsInMiniNotch)
                AnimateSize(false);
            UpdateLyricsPosition(force: true);
        });
    }

    private void UpdateTimelineAndLyrics()
    {
        var dur = _media.Duration;
        if (dur <= TimeSpan.Zero) return;

        var pos = _media.GetCurrentPosition();
        if ((_open && !_inSettings) || (!_open && (IsSideVerticalMode || IsCornerMode)))
            UpdateProgressVisual(pos, dur);

        UpdateLyricsPosition(force: false);
    }

    private void UpdateProgressVisual(TimeSpan pos, TimeSpan dur)
    {
        if (dur <= TimeSpan.Zero)
        {
            ProgressFill.Width = 0;
            ProgressFillVert.Width = 0;
            ProgressFillCorner.Width = 0;
            SideProgInd.UpdateProgress(0, "0:00", "-0:00");
            CornerMiniProg.UpdateProgress(0, "0:00", "-0:00");
            ElapsedText.Text = "0:00";
            RemainingText.Text = "-0:00";
            ElapsedTextVert.Text = "0:00";
            RemainingTextVert.Text = "-0:00";
            ElapsedTextCorner.Text = "0:00";
            RemainingTextCorner.Text = "-0:00";
            return;
        }

        double ratio = Math.Clamp(pos.TotalSeconds / dur.TotalSeconds, 0, 1);
        double trackW = SeekTrack.ActualWidth;
        if (trackW > 0) ProgressFill.Width = trackW * ratio;
        double trackVertW = SeekTrackVert.ActualWidth;
        if (trackVertW > 0) ProgressFillVert.Width = trackVertW * ratio;
        double trackCornerW = SeekTrackCorner.ActualWidth;
        if (trackCornerW > 0) ProgressFillCorner.Width = trackCornerW * ratio;

        var elapsed = FormatTime(pos);
        var rem = dur - pos;
        if (rem < TimeSpan.Zero) rem = TimeSpan.Zero;
        var remaining = "-" + FormatTime(rem);

        SideProgInd.UpdateProgress(ratio, elapsed, remaining);
        CornerMiniProg.UpdateProgress(ratio, elapsed, remaining);

        ElapsedText.Text = elapsed;
        RemainingText.Text = remaining;
        ElapsedTextVert.Text = elapsed;
        RemainingTextVert.Text = remaining;
        ElapsedTextCorner.Text = elapsed;
        RemainingTextCorner.Text = remaining;
    }

    private void UpdateLyricsPosition(bool force)
    {
        if (!_settings.ShowLyrics || _currentLyrics.Synced.Count == 0)
        {
            if (!_open)
            {
                MiniText.Text = _defaultMiniText;
                MiniLyricText.Text = "";
                MiniLyricText.Visibility = Visibility.Collapsed;
            }
            return;
        }

        double sec = _media.GetCurrentPosition().TotalSeconds + _settings.LyricsOffsetSeconds;

        if (_open && !_inSettings && (_settings.LyricsPlacement is LyricsPlacement.Notch or LyricsPlacement.NotchAndFloating))
        {
            if (IsCornerMode)
                CornerLyrics.UpdatePosition(sec, force);
            else if (IsSideVerticalMode && _sideTab == 0)
                SideLyrics.UpdatePosition(sec, force);
            else if (!IsSideVerticalMode)
                NotchLyrics.UpdatePosition(sec, force);
        }

        if (_floatingWindow is { IsVisible: true })
            _floatingWindow.UpdatePosition(sec, force);

        if (!_open && _settings.ShowLyricsInMiniNotch)
        {
            var line = NotchLyrics.GetCurrentLineText(sec);
            if (_settings.MiniLyricsStyle == MiniLyricsStyle.BelowTitle)
            {
                MiniText.Text = _defaultMiniText;
                MiniLyricText.Text = string.IsNullOrWhiteSpace(line) ? "…" : line;
                MiniLyricText.Visibility = Visibility.Visible;
            }
            else
            {
                MiniLyricText.Visibility = Visibility.Collapsed;
                MiniText.Text = string.IsNullOrWhiteSpace(line) ? _defaultMiniText : line;
            }
        }
        else if (!_open)
        {
            MiniLyricText.Visibility = Visibility.Collapsed;
            MiniText.Text = _defaultMiniText;
        }
    }

    private static string FormatTime(TimeSpan t)
    {
        int totalSec = Math.Max(0, (int)t.TotalSeconds);
        return $"{totalSec / 60}:{totalSec % 60:00}";
    }

    // ---------- Controls ----------
    private void Prev(object sender, RoutedEventArgs e) => _media.Previous();
    private void PlayPause(object sender, RoutedEventArgs e) => _media.PlayPause();
    private void Next(object sender, RoutedEventArgs e) => _media.Next();

    private void SeekAt(FrameworkElement track, MouseEventArgs e)
    {
        double w = track.ActualWidth;
        if (w <= 0) return;
        double ratio = Math.Clamp(e.GetPosition(track).X / w, 0, 1);
        _media.SeekRatio(ratio);
        UpdateProgressVisual(TimeSpan.FromSeconds(ratio * _media.Duration.TotalSeconds), _media.Duration);
    }

    private void SeekDown(object sender, MouseButtonEventArgs e) => SeekAt(SeekTrack, e);
    private void SeekMove(object sender, MouseEventArgs e) { if (e.LeftButton == MouseButtonState.Pressed) SeekAt(SeekTrack, e); }
    private void SeekVertDown(object sender, MouseButtonEventArgs e) => SeekAt(SeekTrackVert, e);
    private void SeekVertMove(object sender, MouseEventArgs e) { if (e.LeftButton == MouseButtonState.Pressed) SeekAt(SeekTrackVert, e); }
    private void SeekCornerDown(object sender, MouseButtonEventArgs e) => SeekAt(SeekTrackCorner, e);
    private void SeekCornerMove(object sender, MouseEventArgs e) { if (e.LeftButton == MouseButtonState.Pressed) SeekAt(SeekTrackCorner, e); }

    private void CycleLyricsMode(object sender, RoutedEventArgs e)
    {
        var values = Enum.GetValues<LyricsMode>();
        int next = ((int)_settings.LyricsMode + 1) % values.Length;
        _settings.LyricsMode = values[next];
        _settings.Save();
        ApplySettings();
    }

    private void Quit(object sender, RoutedEventArgs e) => Application.Current.Shutdown();

    // ---------- Calendar ----------
    private void BuildCalendar(DateTime today)
    {
        _calendarDate = today;
        PopulateCalendar(today, CalMonthTitle, CalWeekdaysGrid, CalDaysGrid);
        PopulateCalendar(today, SideCalMonthTitle, SideCalWeekdaysGrid, SideCalDaysGrid);
        PopulateCalendar(today, CornerCalMonthTitle, CornerCalWeekdaysGrid, CornerCalDaysGrid);
    }

    private void PopulateCalendar(DateTime today, TextBlock monthTitle, System.Windows.Controls.Primitives.UniformGrid weekdaysGrid, System.Windows.Controls.Primitives.UniformGrid daysGrid)
    {
        bool en = IsEn;
        var culture = CultureInfo.GetCultureInfo(en ? "en-US" : "ru-RU");
        monthTitle.Text = today.ToString("MMMM yyyy", culture).ToUpperInvariant();

        weekdaysGrid.Children.Clear();
        var days = en
            ? new[] { "Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat" }
            : new[] { "Пн", "Вт", "Ср", "Чт", "Пт", "Сб", "Вс" };

        foreach (var d in days)
        {
            weekdaysGrid.Children.Add(new TextBlock
            {
                Text = d,
                Foreground = new SolidColorBrush(Color.FromRgb(99, 99, 102)),
                FontSize = 10,
                HorizontalAlignment = HorizontalAlignment.Center
            });
        }

        daysGrid.Children.Clear();
        var first = new DateTime(today.Year, today.Month, 1);
        int offset = en ? (int)first.DayOfWeek : ((int)first.DayOfWeek + 6) % 7;
        int daysInMonth = DateTime.DaysInMonth(today.Year, today.Month);

        for (int i = 0; i < offset; i++)
            daysGrid.Children.Add(new TextBlock());

        for (int day = 1; day <= daysInMonth; day++)
        {
            bool isToday = day == today.Day;
            if (isToday)
            {
                var badge = new Border
                {
                    Width = 21,
                    Height = 21,
                    CornerRadius = new CornerRadius(10.5),
                    Background = Brushes.White,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 1, 0, 1),
                    Child = new TextBlock
                    {
                        Text = day.ToString(),
                        Foreground = Brushes.Black,
                        FontSize = 11,
                        FontWeight = FontWeights.Bold,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center
                    }
                };
                daysGrid.Children.Add(badge);
            }
            else
            {
                daysGrid.Children.Add(new TextBlock
                {
                    Text = day.ToString(),
                    Foreground = new SolidColorBrush(Color.FromRgb(209, 209, 214)),
                    FontSize = 11,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 3, 0, 3)
                });
            }
        }
    }

    // ---------- Clipboard History ----------
    private async void OnClip()
    {
        for (int n = 0; n < 5; n++)
        {
            try
            {
                var d = Clipboard.GetDataObject();
                if (d == null || d.GetDataPresent("ExcludeClipboardContentFromMonitorProcessing")
                              || !d.GetDataPresent(DataFormats.UnicodeText))
                    return;

                var s = d.GetData(DataFormats.UnicodeText) as string;
                if (string.IsNullOrWhiteSpace(s)) return;

                _clips.Remove(s);
                _clips.Insert(0, s);
                if (_clips.Count > 15) _clips.RemoveRange(15, _clips.Count - 15);
                RebuildClips();
                return;
            }
            catch { await Task.Delay(50); }
        }
    }

    private void RebuildClips()
    {
        PopulateClipList(ClipList);
        PopulateClipList(SideClipList);
        PopulateClipList(CornerClipList);
    }

    private void PopulateClipList(StackPanel targetList)
    {
        targetList.Children.Clear();
        var visible = _clips.Take(Math.Clamp(_settings.ClipboardMaxEntries, 1, 15)).ToList();
        if (visible.Count == 0)
        {
            targetList.Children.Add(new TextBlock
            {
                Text = IsEn ? "Empty" : "Пусто",
                Foreground = new SolidColorBrush(Color.FromRgb(85, 85, 92)),
                FontSize = 12
            });
            return;
        }

        foreach (var item in visible)
        {
            var text = item;
            var grid = new Grid { Margin = new Thickness(0, 0, 0, 2) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var label = new TextBlock
            {
                Text = text.Replace('\r', ' ').Replace('\n', ' ').Trim(),
                Foreground = new SolidColorBrush(Color.FromRgb(215, 215, 222)),
                FontSize = 12.5,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center
            };

            var copyIcon = new Path
            {
                Data = CopyGeometry,
                Fill = new SolidColorBrush(Color.FromRgb(100, 100, 108)),
                Stretch = Stretch.Uniform,
                Width = 12,
                Height = 12,
                Margin = new Thickness(8, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center
            };

            Grid.SetColumn(label, 0);
            Grid.SetColumn(copyIcon, 1);
            grid.Children.Add(label);
            grid.Children.Add(copyIcon);

            var btn = new Button
            {
                Content = grid,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(2, 5, 2, 5),
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Cursor = Cursors.Hand,
                Style = (Style)FindResource("IconBtn")
            };

            btn.Click += async (_, _) =>
            {
                try
                {
                    Clipboard.SetText(text);
                    copyIcon.Fill = new SolidColorBrush(Color.FromRgb(52, 199, 89));
                    await Task.Delay(700);
                    copyIcon.Fill = new SolidColorBrush(Color.FromRgb(100, 100, 108));
                }
                catch { }
            };

            targetList.Children.Add(btn);
        }
    }
}
