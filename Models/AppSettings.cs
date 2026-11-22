using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NotchturneWin.Models;

public enum LyricsPlacement
{
    Notch,
    Floating,
    NotchAndFloating,
    Hidden
}

public enum LyricsMode
{
    Classic,
    Karaoke,
    Bounce,
    Typing,
    SingleLine
}

public enum FloatingPosition
{
    BottomCenter,
    TopBelowNotch,
    Custom
}

public enum MiniLyricsStyle
{
    BelowTitle,
    ReplaceTitle
}

public enum NotchBackgroundMode
{
    Black,
    Glass,
    CoverGlass,
    CoverBlack
}

public class AppSettings
{
    public bool ShowLyrics { get; set; } = true;
    public LyricsPlacement LyricsPlacement { get; set; } = LyricsPlacement.Notch;
    public LyricsMode LyricsMode { get; set; } = LyricsMode.Karaoke;
    public bool LockActiveLine { get; set; } = true;
    public bool ShowLyricsInMiniNotch { get; set; } = false;
    public MiniLyricsStyle MiniLyricsStyle { get; set; } = MiniLyricsStyle.BelowTitle;
    public double LyricsOffsetSeconds { get; set; } = 0.2;

    public NotchBackgroundMode NotchBackground { get; set; } = NotchBackgroundMode.Black;

    public bool UseAppleMusicHdCover { get; set; } = true;
    public bool IgnoreBrowsers { get; set; } = true;
    public List<string> BlockedApps { get; set; } = new();

    public bool ShowCalendar { get; set; } = true;
    public bool ShowClipboard { get; set; } = true;
    public int ClipboardMaxEntries { get; set; } = 5;
    public string Language { get; set; } = "ru";

    public FloatingPosition FloatingPosition { get; set; } = FloatingPosition.BottomCenter;
    public double? FloatingCustomLeft { get; set; }
    public double? FloatingCustomTop { get; set; }
    public double FloatingFontSize { get; set; } = 22;
    public double FloatingOpacity { get; set; } = 0.92;
    public bool FloatingClickThrough { get; set; } = true;

    private static string SettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "NotchturneWin",
        "settings.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                var loaded = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
                if (loaded != null)
                {
                    loaded.BlockedApps ??= new();
                    return loaded;
                }
            }
        }
        catch { }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(SettingsPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this, JsonOptions));
        }
        catch { }
    }
}
