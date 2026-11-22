using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NotchturneWin.Models;
using Windows.Media.Control;

namespace NotchturneWin.Services;

public record MediaAppInfo(string AppId, string DisplayName, string BadgeText, Color BadgeColor, bool IsBrowser);

public record TrackSnapshot(
    string Title,
    string Artist,
    string Album,
    string AppId,
    MediaAppInfo AppInfo,
    BitmapImage? Thumbnail,
    bool HasSession);

public class MediaService
{
    private static readonly string[] BrowserTokens =
    {
        "chrome", "msedge", "edge", "firefox", "opera", "brave", "vivaldi",
        "browser", "arc", "waterfox", "librewolf", "zen", "chromium", "tor"
    };

    private readonly AppSettings _settings;
    private GlobalSystemMediaTransportControlsSessionManager? _mgr;
    private GlobalSystemMediaTransportControlsSession? _activeSession;
    private readonly List<GlobalSystemMediaTransportControlsSession> _watchedSessions = new();
    private readonly HashSet<string> _seenAppIds = new(StringComparer.OrdinalIgnoreCase);

    private TimeSpan _pos;
    private TimeSpan _dur;
    private DateTimeOffset _posAt;
    private bool _playing;

    public event Action<TrackSnapshot>? TrackChanged;
    public event Action<bool, TimeSpan, TimeSpan>? PlaybackChanged;
    public event Action? SeenAppsChanged;

    public bool IsPlaying => _playing;
    public TimeSpan Duration => _dur;
    public string ActiveAppId => _activeSession?.SourceAppUserModelId ?? "";

    public MediaService(AppSettings settings)
    {
        _settings = settings;
    }

    public IReadOnlyList<MediaAppInfo> GetSeenApps()
    {
        lock (_seenAppIds)
        {
            return _seenAppIds
                .Select(DescribeApp)
                .OrderBy(a => a.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }

    public async Task InitializeAsync()
    {
        _mgr = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
        _mgr.CurrentSessionChanged += (_, _) => SelectBestSession();
        _mgr.SessionsChanged += (_, _) => SelectBestSession();
        SelectBestSession();
    }

    public void RefreshSessionFilter()
    {
        SelectBestSession(forceReattach: true);
    }

    public bool IsAppBlocked(string appId)
    {
        if (string.IsNullOrWhiteSpace(appId)) return false;
        var info = DescribeApp(appId);
        if (_settings.IgnoreBrowsers && info.IsBrowser)
            return true;

        return _settings.BlockedApps.Any(b =>
            !string.IsNullOrWhiteSpace(b) &&
            (appId.Equals(b, StringComparison.OrdinalIgnoreCase) ||
             appId.Contains(b, StringComparison.OrdinalIgnoreCase)));
    }

    private void SelectBestSession(bool forceReattach = false)
    {
        if (_mgr == null) return;

        IReadOnlyList<GlobalSystemMediaTransportControlsSession> sessions;
        try { sessions = _mgr.GetSessions(); }
        catch { return; }

        bool addedNew = false;
        lock (_seenAppIds)
        {
            foreach (var s in sessions)
            {
                if (!string.IsNullOrWhiteSpace(s.SourceAppUserModelId) && _seenAppIds.Add(s.SourceAppUserModelId))
                    addedNew = true;
            }
        }
        if (addedNew) SeenAppsChanged?.Invoke();

        // Watch all sessions' playback state so if Spotify starts playing while browser is open, we switch immediately
        foreach (var old in _watchedSessions)
            old.PlaybackInfoChanged -= OnAnySessionPlaybackChanged;
        _watchedSessions.Clear();
        foreach (var s in sessions)
        {
            s.PlaybackInfoChanged += OnAnySessionPlaybackChanged;
            _watchedSessions.Add(s);
        }

        var allowed = sessions.Where(s => !IsAppBlocked(s.SourceAppUserModelId)).ToList();
        var sysCurrent = _mgr.GetCurrentSession();

        GlobalSystemMediaTransportControlsSession? pick = null;
        if (sysCurrent != null && !IsAppBlocked(sysCurrent.SourceAppUserModelId) &&
            sysCurrent.GetPlaybackInfo().PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
        {
            pick = sysCurrent;
        }
        else
        {
            pick = allowed.FirstOrDefault(s =>
                s.GetPlaybackInfo().PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing);
        }

        if (pick == null && sysCurrent != null && !IsAppBlocked(sysCurrent.SourceAppUserModelId))
            pick = sysCurrent;

        pick ??= allowed.FirstOrDefault();

        if (!forceReattach && ReferenceEquals(pick, _activeSession))
            return;

        if (_activeSession != null)
        {
            _activeSession.MediaPropertiesChanged -= OnProps;
            _activeSession.PlaybackInfoChanged -= OnInfo;
            _activeSession.TimelinePropertiesChanged -= OnInfo;
        }

        _activeSession = pick;

        if (_activeSession == null)
        {
            _playing = false;
            _pos = TimeSpan.Zero;
            _dur = TimeSpan.Zero;
            TrackChanged?.Invoke(new TrackSnapshot("", "", "", "", DescribeApp(""), null, false));
            PlaybackChanged?.Invoke(false, TimeSpan.Zero, TimeSpan.Zero);
            return;
        }

        _activeSession.MediaPropertiesChanged += OnProps;
        _activeSession.PlaybackInfoChanged += OnInfo;
        _activeSession.TimelinePropertiesChanged += OnInfo;

        OnProps(_activeSession, null);
        OnInfo(_activeSession, null);
    }

    private void OnAnySessionPlaybackChanged(GlobalSystemMediaTransportControlsSession sender, PlaybackInfoChangedEventArgs args)
    {
        if (!ReferenceEquals(sender, _activeSession))
            SelectBestSession();
    }

    private async void OnProps(GlobalSystemMediaTransportControlsSession sender, MediaPropertiesChangedEventArgs? e)
    {
        if (!ReferenceEquals(sender, _activeSession)) return;
        try
        {
            var props = await sender.TryGetMediaPropertiesAsync();
            if (!ReferenceEquals(sender, _activeSession)) return;

            BitmapImage? thumb = null;
            if (props.Thumbnail != null)
            {
                try
                {
                    using var st = (await props.Thumbnail.OpenReadAsync()).AsStreamForRead();
                    using var ms = new MemoryStream();
                    await st.CopyToAsync(ms);
                    var bytes = ms.ToArray();
                    if (bytes.Length > 0) thumb = ArtworkService.CreateFrozenBitmap(bytes);
                }
                catch { }
            }

            var appId = sender.SourceAppUserModelId ?? "";
            TrackChanged?.Invoke(new TrackSnapshot(
                props.Title ?? "",
                props.Artist ?? "",
                props.AlbumTitle ?? "",
                appId,
                DescribeApp(appId),
                thumb,
                true));
        }
        catch { }
    }

    private void OnInfo(GlobalSystemMediaTransportControlsSession sender, object? e)
    {
        if (!ReferenceEquals(sender, _activeSession)) return;
        try
        {
            var tl = sender.GetTimelineProperties();
            var pb = sender.GetPlaybackInfo();

            var newDur = tl.EndTime - tl.StartTime;
            var newPos = tl.Position - tl.StartTime;
            if (newPos < TimeSpan.Zero) newPos = TimeSpan.Zero;
            if (newDur < TimeSpan.Zero) newDur = TimeSpan.Zero;

            var pred = GetCurrentPosition();
            var isNowPlaying = pb.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;

            // Smooth minor SMTC jitter (<0.8s) while playing; apply real seeks immediately
            if (_playing && isNowPlaying && Math.Abs((newPos - pred).TotalSeconds) < 0.8 && (DateTimeOffset.Now - _posAt).TotalSeconds < 5)
            {
                _pos = pred + TimeSpan.FromSeconds((newPos - pred).TotalSeconds * 0.25);
                _posAt = DateTimeOffset.Now;
            }
            else
            {
                _pos = newPos;
                _posAt = tl.LastUpdatedTime > DateTimeOffset.MinValue.AddYears(1) ? tl.LastUpdatedTime : DateTimeOffset.Now;
            }

            _dur = newDur;
            _playing = isNowPlaying;

            PlaybackChanged?.Invoke(_playing, GetCurrentPosition(), _dur);
        }
        catch { }
    }

    public TimeSpan GetCurrentPosition()
    {
        if (_dur <= TimeSpan.Zero) return TimeSpan.Zero;
        var t = _pos + (_playing ? DateTimeOffset.Now - _posAt : TimeSpan.Zero);
        if (t < TimeSpan.Zero) return TimeSpan.Zero;
        if (t > _dur) return _dur;
        return t;
    }

    public void Previous() => _ = _activeSession?.TrySkipPreviousAsync();
    public void PlayPause() => _ = _activeSession?.TryTogglePlayPauseAsync();
    public void Next() => _ = _activeSession?.TrySkipNextAsync();

    public void SeekRatio(double ratio)
    {
        if (_activeSession == null || _dur <= TimeSpan.Zero) return;
        ratio = Math.Clamp(ratio, 0, 1);
        SeekTo(TimeSpan.FromTicks((long)(ratio * _dur.Ticks)));
    }

    public void SeekTo(TimeSpan target)
    {
        if (_activeSession == null || _dur <= TimeSpan.Zero) return;
        if (target < TimeSpan.Zero) target = TimeSpan.Zero;
        if (target > _dur) target = _dur;
        _pos = target;
        _posAt = DateTimeOffset.Now;
        _ = _activeSession.TryChangePlaybackPositionAsync(target.Ticks);
    }

    public static MediaAppInfo DescribeApp(string appId)
    {
        var lower = (appId ?? "").ToLowerInvariant();
        if (lower.Contains("spotify"))
            return new MediaAppInfo(appId ?? "", "Spotify", "S", Color.FromRgb(29, 185, 84), false);
        if (lower.Contains("applemusic") || lower.Contains("itunes") || lower.Contains("appleinc.applemusic"))
            return new MediaAppInfo(appId ?? "", "Apple Music", "♪", Color.FromRgb(250, 45, 72), false);
        if (lower.Contains("yandex") && lower.Contains("music"))
            return new MediaAppInfo(appId ?? "", "Яндекс Музыка", "Я", Color.FromRgb(255, 204, 0), false);
        if (lower.Contains("tidal"))
            return new MediaAppInfo(appId ?? "", "TIDAL", "T", Color.FromRgb(20, 20, 20), false);
        if (lower.Contains("foobar"))
            return new MediaAppInfo(appId ?? "", "foobar2000", "F", Color.FromRgb(100, 100, 100), false);
        if (lower.Contains("aimp"))
            return new MediaAppInfo(appId ?? "", "AIMP", "A", Color.FromRgb(255, 140, 0), false);
        if (lower.Contains("vlc"))
            return new MediaAppInfo(appId ?? "", "VLC", "V", Color.FromRgb(255, 136, 0), false);
        if (lower.Contains("telegram"))
            return new MediaAppInfo(appId ?? "", "Telegram", "TG", Color.FromRgb(42, 171, 238), false);
        if (lower.Contains("discord"))
            return new MediaAppInfo(appId ?? "", "Discord", "D", Color.FromRgb(88, 101, 242), false);

        bool isBrowser = BrowserTokens.Any(tok => lower.Contains(tok));
        if (lower.Contains("chrome"))
            return new MediaAppInfo(appId ?? "", "Google Chrome", "C", Color.FromRgb(66, 133, 244), true);
        if (lower.Contains("msedge") || lower.Contains("edge"))
            return new MediaAppInfo(appId ?? "", "Microsoft Edge", "E", Color.FromRgb(0, 120, 215), true);
        if (lower.Contains("firefox"))
            return new MediaAppInfo(appId ?? "", "Firefox", "F", Color.FromRgb(255, 113, 57), true);
        if (lower.Contains("opera"))
            return new MediaAppInfo(appId ?? "", "Opera", "O", Color.FromRgb(255, 27, 45), true);
        if (lower.Contains("brave"))
            return new MediaAppInfo(appId ?? "", "Brave", "B", Color.FromRgb(251, 84, 43), true);
        if (lower.Contains("yandex"))
            return new MediaAppInfo(appId ?? "", "Яндекс Браузер", "Я", Color.FromRgb(252, 63, 29), true);

        // Clean fallback name
        var clean = appId ?? "Media";
        var bang = clean.IndexOf('!');
        if (bang > 0) clean = clean[..bang];
        if (clean.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) clean = clean[..^4];
        var dot = clean.LastIndexOf('.');
        if (dot >= 0 && dot < clean.Length - 1) clean = clean[(dot + 1)..];
        if (string.IsNullOrWhiteSpace(clean)) clean = "Media";

        return new MediaAppInfo(appId ?? "", clean, "♫", Color.FromRgb(90, 90, 100), isBrowser);
    }
}
