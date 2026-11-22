using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using NotchturneWin.Models;
using NotchturneWin.Services;

namespace NotchturneWin;

public partial class SettingsWindow : Window
{
    private readonly AppSettings _settings;
    private readonly MediaService _media;
    private readonly Action _onChanged;
    private bool _loading = true;

    public SettingsWindow(AppSettings settings, MediaService media, Action onChanged)
    {
        _settings = settings;
        _media = media;
        _onChanged = onChanged;
        InitializeComponent();
        LoadFromSettings();
        RebuildAppsList();
        RebuildBlockedTags();
        _media.SeenAppsChanged += OnSeenAppsChanged;
        Closed += (_, _) => _media.SeenAppsChanged -= OnSeenAppsChanged;
    }

    private void OnSeenAppsChanged() => Dispatcher.Invoke(RebuildAppsList);

    private void LoadFromSettings()
    {
        _loading = true;
        SelectByTag(PlacementBox, _settings.LyricsPlacement.ToString());
        SelectByTag(ModeBox, _settings.LyricsMode.ToString());
        MiniLyricsCheck.IsChecked = _settings.ShowLyricsInMiniNotch;

        OffsetSlider.Value = Math.Clamp(_settings.LyricsOffsetSeconds, -3, 3);
        OffsetVal.Text = $"{(_settings.LyricsOffsetSeconds >= 0 ? "+" : "")}{_settings.LyricsOffsetSeconds:0.0}s";

        SelectByTag(FloatPosBox, _settings.FloatingPosition.ToString());
        FloatSizeSlider.Value = Math.Clamp(_settings.FloatingFontSize, 14, 36);
        FloatSizeVal.Text = $"{(int)_settings.FloatingFontSize}";
        FloatOpSlider.Value = Math.Clamp(_settings.FloatingOpacity * 100, 30, 100);
        FloatOpVal.Text = $"{(int)Math.Round(_settings.FloatingOpacity * 100)}%";
        ClickThroughCheck.IsChecked = _settings.FloatingClickThrough;

        HdCoverCheck.IsChecked = _settings.UseAppleMusicHdCover;
        CalendarCheck.IsChecked = _settings.ShowCalendar;
        ClipboardCheck.IsChecked = _settings.ShowClipboard;
        IgnoreBrowsersCheck.IsChecked = _settings.IgnoreBrowsers;
        _loading = false;
    }

    private static void SelectByTag(ComboBox box, string tag)
    {
        foreach (ComboBoxItem item in box.Items)
        {
            if (string.Equals(item.Tag as string, tag, StringComparison.OrdinalIgnoreCase))
            {
                box.SelectedItem = item;
                return;
            }
        }
        if (box.Items.Count > 0) box.SelectedIndex = 0;
    }

    private void OnSettingChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;

        if (PlacementBox.SelectedItem is ComboBoxItem pItem && Enum.TryParse<LyricsPlacement>(pItem.Tag as string, out var placement))
            _settings.LyricsPlacement = placement;

        if (ModeBox.SelectedItem is ComboBoxItem mItem && Enum.TryParse<LyricsMode>(mItem.Tag as string, out var mode))
            _settings.LyricsMode = mode;

        _settings.ShowLyricsInMiniNotch = MiniLyricsCheck.IsChecked == true;
        _settings.LyricsOffsetSeconds = Math.Round(OffsetSlider.Value, 1);
        OffsetVal.Text = $"{(_settings.LyricsOffsetSeconds >= 0 ? "+" : "")}{_settings.LyricsOffsetSeconds:0.0}s";

        if (FloatPosBox.SelectedItem is ComboBoxItem fpItem && Enum.TryParse<FloatingPosition>(fpItem.Tag as string, out var fp))
            _settings.FloatingPosition = fp;

        _settings.FloatingFontSize = Math.Round(FloatSizeSlider.Value);
        FloatSizeVal.Text = $"{(int)_settings.FloatingFontSize}";
        _settings.FloatingOpacity = Math.Round(FloatOpSlider.Value) / 100.0;
        FloatOpVal.Text = $"{(int)Math.Round(FloatOpSlider.Value)}%";
        _settings.FloatingClickThrough = ClickThroughCheck.IsChecked == true;

        _settings.UseAppleMusicHdCover = HdCoverCheck.IsChecked == true;
        _settings.ShowCalendar = CalendarCheck.IsChecked == true;
        _settings.ShowClipboard = ClipboardCheck.IsChecked == true;
        _settings.IgnoreBrowsers = IgnoreBrowsersCheck.IsChecked == true;

        _settings.Save();
        _onChanged();
        RebuildAppsList();
    }

    private void RebuildAppsList()
    {
        AppsPanel.Children.Clear();
        var apps = _media.GetSeenApps();
        if (apps.Count == 0)
        {
            AppsPanel.Children.Add(new TextBlock
            {
                Text = "Пока нет активных плееров — включи музыку, чтобы увидеть источники",
                Foreground = new SolidColorBrush(Color.FromRgb(115, 115, 125)),
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 2, 0, 4)
            });
            return;
        }

        foreach (var app in apps)
        {
            bool blockedManually = _settings.BlockedApps.Any(b => b.Equals(app.AppId, StringComparison.OrdinalIgnoreCase));
            bool blockedByBrowserRule = _settings.IgnoreBrowsers && app.IsBrowser;

            var cb = new CheckBox
            {
                IsChecked = !blockedManually && !blockedByBrowserRule,
                IsEnabled = !blockedByBrowserRule,
                Content = app.DisplayName + (app.IsBrowser ? " (браузер)" : "") +
                          (app.AppId.Equals(_media.ActiveAppId, StringComparison.OrdinalIgnoreCase) ? " • активен" : ""),
                ToolTip = app.AppId
            };

            var id = app.AppId;
            cb.Checked += (_, _) =>
            {
                _settings.BlockedApps.RemoveAll(b => b.Equals(id, StringComparison.OrdinalIgnoreCase));
                _settings.Save();
                _onChanged();
                RebuildBlockedTags();
            };
            cb.Unchecked += (_, _) =>
            {
                if (!_settings.BlockedApps.Any(b => b.Equals(id, StringComparison.OrdinalIgnoreCase)))
                    _settings.BlockedApps.Add(id);
                _settings.Save();
                _onChanged();
                RebuildBlockedTags();
            };

            AppsPanel.Children.Add(cb);
        }
    }

    private void RebuildBlockedTags()
    {
        BlockedTagsPanel.Children.Clear();
        foreach (var token in _settings.BlockedApps.ToList())
        {
            var tag = token;
            var border = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(42, 42, 52)),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(8, 3, 6, 3),
                Margin = new Thickness(0, 0, 6, 6)
            };
            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            sp.Children.Add(new TextBlock
            {
                Text = MediaService.DescribeApp(tag).DisplayName,
                FontSize = 11.5,
                Foreground = new SolidColorBrush(Color.FromRgb(220, 220, 230)),
                VerticalAlignment = VerticalAlignment.Center
            });
            var rm = new Button
            {
                Content = "✕",
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Foreground = new SolidColorBrush(Color.FromRgb(160, 160, 175)),
                FontSize = 10,
                Margin = new Thickness(6, 0, 0, 0),
                Cursor = Cursors.Hand
            };
            rm.Click += (_, _) =>
            {
                _settings.BlockedApps.Remove(tag);
                _settings.Save();
                _onChanged();
                RebuildAppsList();
                RebuildBlockedTags();
            };
            sp.Children.Add(rm);
            border.Child = sp;
            BlockedTagsPanel.Children.Add(border);
        }
    }

    private void OnAddBlocked(object sender, RoutedEventArgs e)
    {
        var text = BlockInput.Text.Trim();
        if (string.IsNullOrWhiteSpace(text)) return;
        if (!_settings.BlockedApps.Any(b => b.Equals(text, StringComparison.OrdinalIgnoreCase)))
        {
            _settings.BlockedApps.Add(text);
            _settings.Save();
            _onChanged();
            RebuildAppsList();
            RebuildBlockedTags();
        }
        BlockInput.Clear();
    }

    private void OnTitleDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed) DragMove();
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
