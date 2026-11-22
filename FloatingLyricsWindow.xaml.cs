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
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr hAfter, int x, int y, int cx, int cy, uint flags);

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_FRAMECHANGED = 0x0020;

    private readonly AppSettings _settings;
    private readonly Action? _onPositionDragged;
    private bool _handleReady;

    public FloatingLyricsWindow(AppSettings settings, Action? onPositionDragged = null)
    {
        _settings = settings;
        _onPositionDragged = onPositionDragged;
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
        ApplyPosition();
    }

    public void ApplySettings()
    {
        Lyrics.Mode = _settings.LyricsMode;
        Lyrics.BaseFontSize = _settings.FloatingFontSize;
        Height = Math.Max(150, _settings.FloatingFontSize * 6.4);
        Opacity = Math.Clamp(_settings.FloatingOpacity, 0.2, 1.0);

        bool en = string.Equals(_settings.Language, "en", StringComparison.OrdinalIgnoreCase);
        DragHint.Text = en
            ? "Drag to move · enable Click-through to lock"
            : "Перетащи мышкой · включи «Пропускать клики» чтобы закрепить";

        bool locked = _settings.FloatingClickThrough;
        PillBorder.Background = locked
            ? Brushes.Transparent
            : new SolidColorBrush(Color.FromArgb(145, 14, 14, 20));
        PillBorder.BorderBrush = locked
            ? Brushes.Transparent
            : new SolidColorBrush(Color.FromArgb(100, 255, 255, 255));
        PillBorder.BorderThickness = locked ? new Thickness(0) : new Thickness(1);
        PillBorder.Cursor = locked ? Cursors.Arrow : Cursors.SizeAll;
        DragHint.Visibility = locked ? Visibility.Collapsed : Visibility.Visible;

        ApplyPosition();
        if (_handleReady) ApplyWindowStyles();
    }

    private void ApplyWindowStyles()
    {
        var h = new WindowInteropHelper(this).Handle;
        if (h == IntPtr.Zero) return;

        int style = GetWindowLong(h, GWL_EXSTYLE) | WS_EX_TOOLWINDOW;
        if (_settings.FloatingClickThrough)
        {
            style |= WS_EX_TRANSPARENT | WS_EX_NOACTIVATE;
        }
        else
        {
            style &= ~WS_EX_TRANSPARENT;
            style &= ~WS_EX_NOACTIVATE;
        }

        SetWindowLong(h, GWL_EXSTYLE, style);
        SetWindowPos(h, IntPtr.Zero, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
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
            Top = 64;
        }
        else
        {
            Left = (SystemParameters.PrimaryScreenWidth - Width) / 2;
            Top = work.Bottom - Height - 24;
        }
    }

    public void SetLyrics(LyricsResult result) => Lyrics.SetLyrics(result);
    public void SetStatus(string msg) => Lyrics.SetStatus(msg);
    public void UpdatePosition(double seconds, bool force = false) => Lyrics.UpdatePosition(seconds, force);

    private void OnDragStart(object sender, MouseButtonEventArgs e)
    {
        if (_settings.FloatingClickThrough || e.LeftButton != MouseButtonState.Pressed) return;
        try
        {
            DragMove();
            _settings.FloatingPosition = FloatingPosition.Custom;
            _settings.FloatingCustomLeft = Left;
            _settings.FloatingCustomTop = Top;
            _settings.Save();
            _onPositionDragged?.Invoke();
        }
        catch { }
    }
}
