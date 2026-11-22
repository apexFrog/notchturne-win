using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using NotchturneWin.Models;
using NotchturneWin.Services;

namespace NotchturneWin;

public partial class FloatingLyricsWindow : Window
{
    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr h, int i);
    [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr h, int i, int v);

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_NOACTIVATE = 0x08000000;

    private readonly AppSettings _settings;
    private bool _handleReady;

    public FloatingLyricsWindow(AppSettings settings)
    {
        _settings = settings;
        InitializeComponent();
        Lyrics.CompactMode = false;
        Lyrics.UseTextShadow = true;
        ApplySettings();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _handleReady = true;
        ApplyWindowStyles();
    }

    public void ApplySettings()
    {
        Lyrics.Mode = _settings.LyricsMode;
        Lyrics.BaseFontSize = _settings.FloatingFontSize;
        Opacity = Math.Clamp(_settings.FloatingOpacity, 0.2, 1.0);

        // When click-through is off, show subtle translucent pill so user can easily drag it
        PillBorder.Background = _settings.FloatingClickThrough
            ? Brushes.Transparent
            : new SolidColorBrush(Color.FromArgb(120, 12, 12, 16));
        PillBorder.BorderBrush = _settings.FloatingClickThrough
            ? Brushes.Transparent
            : new SolidColorBrush(Color.FromArgb(80, 255, 255, 255));
        PillBorder.BorderThickness = _settings.FloatingClickThrough ? new Thickness(0) : new Thickness(1);
        PillBorder.Cursor = _settings.FloatingClickThrough ? Cursors.Arrow : Cursors.SizeAll;

        ApplyPosition();
        if (_handleReady) ApplyWindowStyles();
    }

    private void ApplyWindowStyles()
    {
        var h = new WindowInteropHelper(this).Handle;
        if (h == IntPtr.Zero) return;
        int style = GetWindowLong(h, GWL_EXSTYLE) | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
        if (_settings.FloatingClickThrough)
            style |= WS_EX_TRANSPARENT;
        else
            style &= ~WS_EX_TRANSPARENT;
        SetWindowLong(h, GWL_EXSTYLE, style);
    }

    private void ApplyPosition()
    {
        var work = SystemParameters.WorkArea;
        if (_settings.FloatingPosition == FloatingPosition.Custom &&
            _settings.FloatingCustomLeft.HasValue && _settings.FloatingCustomTop.HasValue)
        {
            Left = _settings.FloatingCustomLeft.Value;
            Top = _settings.FloatingCustomTop.Value;
        }
        else if (_settings.FloatingPosition == FloatingPosition.TopBelowNotch)
        {
            Left = (SystemParameters.PrimaryScreenWidth - Width) / 2;
            Top = 50;
        }
        else
        {
            Left = (SystemParameters.PrimaryScreenWidth - Width) / 2;
            Top = work.Bottom - Height - 28;
        }
    }

    public void SetLyrics(LyricsResult result) => Lyrics.SetLyrics(result);
    public void SetStatus(string msg) => Lyrics.SetStatus(msg);
    public void UpdatePosition(double seconds, bool force = false) => Lyrics.UpdatePosition(seconds, force);

    private void OnDragStart(object sender, MouseButtonEventArgs e)
    {
        if (_settings.FloatingClickThrough) return;
        try
        {
            DragMove();
            _settings.FloatingPosition = FloatingPosition.Custom;
            _settings.FloatingCustomLeft = Left;
            _settings.FloatingCustomTop = Top;
            _settings.Save();
        }
        catch { }
    }
}
