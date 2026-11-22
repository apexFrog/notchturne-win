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
    private DateTime? _idleSince;
    private DateTime _calendarDate = DateTime.MinValue;
    private Color _coverAccentColor = Color.FromRgb(65, 75, 110);

    private bool IsEn => string.Equals(_settings.Language, "en", StringComparison.OrdinalIgnoreCase);

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

        ApplySettings(initial: true);
        BuildCalendar(DateTime.Today);
        RebuildClips();

        Center();
        SizeChanged += (_, _) => Center();

        Loaded += async (_, _) => await _media.InitializeAsync();
        Closed += (_, _) => _floatingWindow?.Close();

        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        timer.Tick += Tick;
        timer.Start();
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

    private double GetCollapsedWidth() => IsMiniLyricsActive() ? 360 : 250;

    private double GetCollapsedHeight() => IsMiniTwoLine() ? 52 : 34;

    private double GetExpandedWidth()
    {
        if (_inSettings) return 540;

        bool showLyrics = _settings.ShowLyrics && _settings.LyricsPlacement is LyricsPlacement.Notch or LyricsPlacement.NotchAndFloating;
        bool showCal = _settings.ShowCalendar;
        bool showClip = _settings.ShowClipboard;

        double w = 340 + 40;
        if (showLyrics) w += 230 + 31;
        if (showCal) w += 188 + 31;
        if (showClip) w += 178 + 31;

        return Math.Min(w, SystemParameters.PrimaryScreenWidth - 32);
    }

    private double GetExpandedHeight() => _inSettings ? 390 : 212;

    private void ApplySettings(bool initial = false)
    {
        bool showNotchLyrics = _settings.ShowLyrics && _settings.LyricsPlacement is LyricsPlacement.Notch or LyricsPlacement.NotchAndFloating;
        bool showFloating = _settings.ShowLyrics && _settings.LyricsPlacement is LyricsPlacement.Floating or LyricsPlacement.NotchAndFloating;

        ColLyrics.Width = showNotchLyrics ? new GridLength(230) : new GridLength(0);
        ColLyricsDiv.Width = showNotchLyrics ? GridLength.Auto : new GridLength(0);
        DivLyrics.Visibility = showNotchLyrics ? Visibility.Visible : Visibility.Collapsed;
        NotchLyrics.Visibility = showNotchLyrics ? Visibility.Visible : Visibility.Collapsed;

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
        ModeQuickBtn.ToolTip = IsEn
            ? $"Lyrics mode: {ModeDisplayName(_settings.LyricsMode)} (click to switch)"
            : $"Режим текста: {ModeDisplayName(_settings.LyricsMode)} (нажми для смены)";

        ApplyNotchBackground();
        ApplyLocalization();
        SyncInlineSettingsControls();

        if (showFloating)
        {
            if (_floatingWindow == null)
            {
                _floatingWindow = new FloatingLyricsWindow(_settings);
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
            UpdateLyricsPosition(force: true);
        }
        else
        {
            Width = GetCollapsedWidth();
        }
    }

    private void ApplyLocalization()
    {
        bool en = IsEn;
        CtxSettings.Header = en ? "Settings…" : "Настройки…";
        CtxCycleMode.Header = en ? "Switch lyrics mode" : "Сменить режим текста";
        CtxQuit.Header = en ? "Quit" : "Выход";
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
        SwLockLine.IsChecked = _settings.LockActiveLine;
        SwMiniLyrics.IsChecked = _settings.ShowLyricsInMiniNotch;
        SwClickThrough.IsChecked = _settings.FloatingClickThrough;
        SwIgnoreBrowsers.IsChecked = _settings.IgnoreBrowsers;

        TxtClipCount.Text = _settings.ClipboardMaxEntries.ToString();
        TxtOffset.Text = $"{(_settings.LyricsOffsetSeconds >= 0 ? "+" : "")}{_settings.LyricsOffsetSeconds:0.0}s";
        TxtFontSize.Text = ((int)_settings.FloatingFontSize).ToString();

        SetPill(BtnLangRu, !IsEn);
        SetPill(BtnLangEn, IsEn);

        SetPill(BtnBgBlack, _settings.NotchBackground == NotchBackgroundMode.Black);
        SetPill(BtnBgGlass, _settings.NotchBackground == NotchBackgroundMode.Glass);
        SetPill(BtnBgCoverGlass, _settings.NotchBackground == NotchBackgroundMode.CoverGlass);
        SetPill(BtnBgCoverBlack, _settings.NotchBackground == NotchBackgroundMode.CoverBlack);

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

        SettingsView.Visibility = Visibility.Visible;
        SettingsView.IsHitTestVisible = true;
        Full.IsHitTestVisible = false;

        var d = TimeSpan.FromMilliseconds(220);
        Full.BeginAnimation(OpacityProperty, new DoubleAnimation(0, d));
        Mini.BeginAnimation(OpacityProperty, new DoubleAnimation(0, d));
        SettingsView.BeginAnimation(OpacityProperty, new DoubleAnimation(1, d));
        AnimateSize(true);
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

        Full.IsHitTestVisible = true;
        Full.BeginAnimation(OpacityProperty, new DoubleAnimation(1, d));
        AnimateSize(true);
    }

    private void OnInlineToggle(object sender, RoutedEventArgs e)
    {
        if (_syncingUi) return;
        bool wasHd = _settings.UseAppleMusicHdCover;

        _settings.ShowLyrics = SwLyrics.IsChecked == true;
        _settings.ShowCalendar = SwCalendar.IsChecked == true;
        _settings.ShowClipboard = SwClipboard.IsChecked == true;
        _settings.UseAppleMusicHdCover = SwHdArt.IsChecked == true;
        _settings.LockActiveLine = SwLockLine.IsChecked == true;
        _settings.ShowLyricsInMiniNotch = SwMiniLyrics.IsChecked == true;
        _settings.FloatingClickThrough = SwClickThrough.IsChecked == true;
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

    private void ApplyNotchBackground()
    {
        var c = _coverAccentColor;
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
                break;

            case NotchBackgroundMode.CoverGlass:
                NotchShell.Background = new LinearGradientBrush(
                    Color.FromArgb(196, 14, 14, 20),
                    Color.FromArgb(174, 6, 6, 10),
                    90);
                NotchShell.BorderBrush = new SolidColorBrush(Color.FromArgb(54, 255, 255, 255));
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
                break;

            case NotchBackgroundMode.CoverBlack:
                NotchShell.Background = new SolidColorBrush(Color.FromRgb(0, 0, 0));
                NotchShell.BorderBrush = new SolidColorBrush(Color.FromArgb(40, 255, 255, 255));
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
                break;

            default: // Black
                NotchShell.Background = new SolidColorBrush(Color.FromRgb(0, 0, 0));
                NotchShell.BorderBrush = new SolidColorBrush(Color.FromArgb(26, 255, 255, 255));
                NotchGlowLayer.Visibility = Visibility.Collapsed;
                break;
        }
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

    private void OnFloatBottom(object s, RoutedEventArgs e) { _settings.FloatingPosition = FloatingPosition.BottomCenter; SaveAndApply(); }
    private void OnFloatTop(object s, RoutedEventArgs e) { _settings.FloatingPosition = FloatingPosition.TopBelowNotch; SaveAndApply(); }
    private void OnFloatCustom(object s, RoutedEventArgs e) { _settings.FloatingPosition = FloatingPosition.Custom; SaveAndApply(); }

    private void OnFontMinus(object s, RoutedEventArgs e) { _settings.FloatingFontSize = Math.Clamp(_settings.FloatingFontSize - 2, 14, 36); SaveAndApply(); }
    private void OnFontPlus(object s, RoutedEventArgs e) { _settings.FloatingFontSize = Math.Clamp(_settings.FloatingFontSize + 2, 14, 36); SaveAndApply(); }

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

    // ---------- Hover & Animation ----------
    private void Tick(object? sender, EventArgs e)
    {
        if (DateTime.Today != _calendarDate)
            BuildCalendar(DateTime.Today);

        if (!_inSettings && PresentationSource.FromVisual(this) is { } src)
        {
            GetCursorPos(out var c);
            var p = src.CompositionTarget.TransformFromDevice.Transform(new Point(c.X, c.Y));

            bool inside = p.X >= Left - 8 && p.X <= Left + Width + 8 && p.Y >= 0 && p.Y <= Top + Height + 8;
            bool menuOpen = NotchShell.ContextMenu?.IsOpen == true;

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

        UpdateTimelineAndLyrics();
    }

    private void SetOpen(bool expand)
    {
        _open = expand;
        AnimateSize(expand);

        var d = TimeSpan.FromMilliseconds(250);
        Full.BeginAnimation(OpacityProperty, new DoubleAnimation(expand ? 1 : 0, d));
        Mini.BeginAnimation(OpacityProperty, new DoubleAnimation(expand ? 0 : 1, d));
        Full.IsHitTestVisible = expand;
    }

    private void AnimateSize(bool expand)
    {
        var d = TimeSpan.FromMilliseconds(260);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        bool twoLine = IsMiniTwoLine();
        Mini.Height = twoLine ? 52 : 34;
        MiniArtBox.Width = twoLine ? 28 : 20;
        MiniArtBox.Height = twoLine ? 28 : 20;
        MiniArtBox.CornerRadius = new CornerRadius(twoLine ? 7 : 5);
        MiniLyricText.Visibility = twoLine ? Visibility.Visible : Visibility.Collapsed;

        double targetW = expand ? GetExpandedWidth() : GetCollapsedWidth();
        double targetH = expand ? GetExpandedHeight() : GetCollapsedHeight();

        BeginAnimation(WidthProperty, new DoubleAnimation(targetW, d) { EasingFunction = ease });
        BeginAnimation(HeightProperty, new DoubleAnimation(targetH, d) { EasingFunction = ease });
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
            MiniText.Text = _defaultMiniText;
            MiniLyricText.Text = "";
            MiniLyricText.Visibility = Visibility.Collapsed;
            MiniDot.Visibility = Visibility.Collapsed;
            if (!_open) AnimateSize(false);
            ArtImg.Source = null;
            MiniArt.Source = null;
            SourceBadge.Visibility = Visibility.Collapsed;
            SourceStatusDot.Background = new SolidColorBrush(Color.FromRgb(75, 75, 82));
            SourceStatusBtn.ToolTip = IsEn ? "No active media source" : "Нет активного источника медиа";
            ProgressFill.Width = 0;
            ElapsedText.Text = "0:00";
            RemainingText.Text = "-0:00";

            NotchLyrics.SetStatus("");
            _floatingWindow?.SetStatus("");
            if (_settings.FloatingClickThrough) _floatingWindow?.Hide();
            return;
        }

        var (cleanTitle, cleanArtist) = MetadataHelper.CleanMeta(snap.Title, snap.Artist);
        TitleText.Text = cleanTitle;
        ArtistText.Text = cleanArtist;
        _defaultMiniText = string.IsNullOrWhiteSpace(cleanArtist) ? cleanTitle : $"{cleanArtist} — {cleanTitle}";
        if (!_settings.ShowLyrics || !_settings.ShowLyricsInMiniNotch || _currentLyrics.Synced.Count == 0)
            MiniText.Text = _defaultMiniText;

        var badgeBrush = new SolidColorBrush(snap.AppInfo.BadgeColor);
        SourceBadge.Background = badgeBrush;
        SourceBadgeText.Text = snap.AppInfo.BadgeText;
        SourceBadge.Visibility = Visibility.Visible;
        SourceStatusDot.Background = badgeBrush;
        SourceStatusBtn.ToolTip = IsEn
            ? $"Source: {snap.AppInfo.DisplayName} (click for settings)"
            : $"Источник: {snap.AppInfo.DisplayName} (нажми для настроек)";
        MiniDot.Fill = badgeBrush;

        var newKey = $"{snap.Artist}|{snap.Title}";

        // Never overwrite an already-loaded Apple Music HD cover with Windows thumbnail for the same track
        if (snap.Thumbnail != null && _hdCoverKey != newKey)
        {
            ArtImg.Source = snap.Thumbnail;
            MiniArt.Source = snap.Thumbnail;
            _coverAccentColor = ArtworkService.ExtractAccentColor(snap.Thumbnail);
            ApplyNotchBackground();
        }

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
        PlayPauseIcon.Data = playing ? PauseGeometry : PlayGeometry;
        MiniDot.Visibility = playing ? Visibility.Visible : Visibility.Collapsed;
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
            ArtImg.Source = hd;
            MiniArt.Source = hd;
            _coverAccentColor = ArtworkService.ExtractAccentColor(hd);
            ApplyNotchBackground();
        });
    }

    private async Task LoadLyricsAsync(string artist, string title, string album, CancellationToken ct)
    {
        _currentLyrics = new(new(), null, false);
        var searching = IsEn ? "Searching lyrics…" : "Ищу текст…";
        Dispatcher.Invoke(() =>
        {
            NotchLyrics.SetStatus(searching);
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
            _floatingWindow?.SetLyrics(result);
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
        if (_open && !_inSettings)
            UpdateProgressVisual(pos, dur);

        UpdateLyricsPosition(force: false);
    }

    private void UpdateProgressVisual(TimeSpan pos, TimeSpan dur)
    {
        if (dur <= TimeSpan.Zero)
        {
            ProgressFill.Width = 0;
            ElapsedText.Text = "0:00";
            RemainingText.Text = "-0:00";
            return;
        }

        double ratio = Math.Clamp(pos.TotalSeconds / dur.TotalSeconds, 0, 1);
        double trackW = SeekTrack.ActualWidth;
        if (trackW > 0) ProgressFill.Width = trackW * ratio;

        ElapsedText.Text = FormatTime(pos);
        var rem = dur - pos;
        if (rem < TimeSpan.Zero) rem = TimeSpan.Zero;
        RemainingText.Text = "-" + FormatTime(rem);
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
            NotchLyrics.UpdatePosition(sec, force);

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

    private void SeekAt(MouseEventArgs e)
    {
        double w = SeekTrack.ActualWidth;
        if (w <= 0) return;
        double ratio = Math.Clamp(e.GetPosition(SeekTrack).X / w, 0, 1);
        _media.SeekRatio(ratio);
        UpdateProgressVisual(TimeSpan.FromSeconds(ratio * _media.Duration.TotalSeconds), _media.Duration);
    }

    private void SeekDown(object sender, MouseButtonEventArgs e) => SeekAt(e);
    private void SeekMove(object sender, MouseEventArgs e) { if (e.LeftButton == MouseButtonState.Pressed) SeekAt(e); }

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
        bool en = IsEn;
        var culture = CultureInfo.GetCultureInfo(en ? "en-US" : "ru-RU");
        CalMonthTitle.Text = today.ToString("MMMM yyyy", culture).ToUpperInvariant();

        CalWeekdaysGrid.Children.Clear();
        var days = en
            ? new[] { "Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat" }
            : new[] { "Пн", "Вт", "Ср", "Чт", "Пт", "Сб", "Вс" };

        foreach (var d in days)
        {
            CalWeekdaysGrid.Children.Add(new TextBlock
            {
                Text = d,
                Foreground = new SolidColorBrush(Color.FromRgb(99, 99, 102)),
                FontSize = 10,
                HorizontalAlignment = HorizontalAlignment.Center
            });
        }

        CalDaysGrid.Children.Clear();
        var first = new DateTime(today.Year, today.Month, 1);
        int offset = en ? (int)first.DayOfWeek : ((int)first.DayOfWeek + 6) % 7;
        int daysInMonth = DateTime.DaysInMonth(today.Year, today.Month);

        for (int i = 0; i < offset; i++)
            CalDaysGrid.Children.Add(new TextBlock());

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
                CalDaysGrid.Children.Add(badge);
            }
            else
            {
                CalDaysGrid.Children.Add(new TextBlock
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
        ClipList.Children.Clear();
        var visible = _clips.Take(Math.Clamp(_settings.ClipboardMaxEntries, 1, 15)).ToList();
        if (visible.Count == 0)
        {
            ClipList.Children.Add(new TextBlock
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

            ClipList.Children.Add(btn);
        }
    }
}
