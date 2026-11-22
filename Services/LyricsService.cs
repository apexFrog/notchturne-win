using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace NotchturneWin.Services;

public record LyricLine(double TimeSeconds, string Text);

public record LyricsResult(List<LyricLine> Synced, string? Plain, bool Found);

public class LyricsService
{
    private readonly HttpClient _http;
    private static readonly Regex TimestampRegex = new(@"\[(\d+):(\d+(?:\.\d+)?)\]", RegexOptions.Compiled);
    private static readonly Regex AnyBracketTagRegex = new(@"\[[^\]]*\]", RegexOptions.Compiled);

    public LyricsService(HttpClient http)
    {
        _http = http;
    }

    private record LrcEntry(double Duration, string? ArtistName, string? TrackName, string? SyncedLyrics, string? PlainLyrics);

    public async Task<LyricsResult> FetchLyricsAsync(string rawArtist, string rawTitle, string? album, double durationSeconds, CancellationToken ct)
    {
        var (title, artist) = MetadataHelper.CleanMeta(rawTitle, rawArtist);
        if (string.IsNullOrWhiteSpace(title))
            return new LyricsResult(new(), null, false);

        var baseTitle = MetadataHelper.StripSubtitle(title);
        var primaryArtist = MetadataHelper.GetPrimaryArtist(artist);
        string? bestPlainFallback = null;

        // Stage 1: Exact /api/get
        if (!string.IsNullOrWhiteSpace(artist))
        {
            var q = $"track_name={Uri.EscapeDataString(title)}&artist_name={Uri.EscapeDataString(artist)}";
            if (!string.IsNullOrWhiteSpace(album)) q += $"&album_name={Uri.EscapeDataString(album)}";
            if (durationSeconds > 0) q += $"&duration={(int)Math.Round(durationSeconds)}";

            var exact = await QueryGetAsync("https://lrclib.net/api/get?" + q, ct);
            if (ct.IsCancellationRequested) return new LyricsResult(new(), null, false);
            if (exact != null)
            {
                if (!string.IsNullOrWhiteSpace(exact.SyncedLyrics))
                {
                    var parsed = ParseLrc(exact.SyncedLyrics);
                    if (parsed.Count > 0) return new LyricsResult(parsed, exact.PlainLyrics, true);
                }
                bestPlainFallback ??= exact.PlainLyrics;
            }
        }

        // Stages 2-6: Progressive search queries
        var queries = new List<(string Query, bool LooseArtist)>();
        var seenQ = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddQuery(string q, bool loose)
        {
            if (seenQ.Add(q)) queries.Add((q, loose));
        }

        if (!string.IsNullOrWhiteSpace(artist))
        {
            AddQuery($"track_name={Uri.EscapeDataString(title)}&artist_name={Uri.EscapeDataString(artist)}", false);
            if (baseTitle != title)
                AddQuery($"track_name={Uri.EscapeDataString(baseTitle)}&artist_name={Uri.EscapeDataString(artist)}", false);

            // General / primary artist name fallback (e.g., stripping feat., collaborations, secondary artists)
            if (!string.IsNullOrWhiteSpace(primaryArtist) && !primaryArtist.Equals(artist, StringComparison.OrdinalIgnoreCase))
            {
                AddQuery($"track_name={Uri.EscapeDataString(baseTitle)}&artist_name={Uri.EscapeDataString(primaryArtist)}", false);
                AddQuery($"q={Uri.EscapeDataString($"{primaryArtist} {baseTitle}")}", true);
            }

            AddQuery($"q={Uri.EscapeDataString($"{artist} {baseTitle}")}", true);
        }

        // Title-only search with loose artist verification (and general artist fallback)
        AddQuery($"track_name={Uri.EscapeDataString(baseTitle)}", true);
        if (!string.IsNullOrWhiteSpace(primaryArtist))
            AddQuery($"q={Uri.EscapeDataString(primaryArtist)}", true);

        foreach (var (query, loose) in queries)
        {
            if (ct.IsCancellationRequested) return new LyricsResult(new(), null, false);
            var list = await QuerySearchAsync("https://lrclib.net/api/search?" + query, ct);
            if (list.Count == 0) continue;

            // If this is the general artist-only query, require title match too
            bool isArtistOnly = query.StartsWith("q=", StringComparison.Ordinal) && !query.Contains(Uri.EscapeDataString(baseTitle), StringComparison.OrdinalIgnoreCase);
            var normBaseTitle = MetadataHelper.Normalize(baseTitle);

            var filtered = list.Where(item =>
            {
                if (loose && !string.IsNullOrWhiteSpace(artist) && !MetadataHelper.ArtistMatchesLoose(item.ArtistName, artist))
                    return false;
                if (isArtistOnly)
                {
                    var rt = MetadataHelper.Normalize(item.TrackName);
                    if (string.IsNullOrEmpty(rt) || (!rt.Contains(normBaseTitle, StringComparison.Ordinal) && !normBaseTitle.Contains(rt, StringComparison.Ordinal)))
                        return false;
                }
                return true;
            }).ToList();

            var syncedMatches = filtered.Where(x => !string.IsNullOrWhiteSpace(x.SyncedLyrics)).ToList();
            if (syncedMatches.Count > 0)
            {
                var durOk = durationSeconds > 0
                    ? syncedMatches.Where(x => x.Duration <= 0 || Math.Abs(x.Duration - durationSeconds) <= 12).ToList()
                    : syncedMatches;

                var pool = durOk.Count > 0 ? durOk : (loose ? new List<LrcEntry>() : syncedMatches);
                if (pool.Count > 0)
                {
                    var best = durationSeconds > 0
                        ? pool.OrderBy(x => x.Duration > 0 ? Math.Abs(x.Duration - durationSeconds) : 999).First()
                        : pool[0];

                    var parsed = ParseLrc(best.SyncedLyrics!);
                    if (parsed.Count > 0)
                        return new LyricsResult(parsed, best.PlainLyrics, true);
                }
            }

            if (bestPlainFallback == null)
            {
                var plainEntry = filtered.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x.PlainLyrics));
                if (plainEntry != null) bestPlainFallback = plainEntry.PlainLyrics;
            }
        }

        if (!string.IsNullOrWhiteSpace(bestPlainFallback))
            return new LyricsResult(new(), bestPlainFallback, true);

        return new LyricsResult(new(), null, false);
    }

    private async Task<LrcEntry?> QueryGetAsync(string url, CancellationToken ct)
    {
        try
        {
            using var resp = await _http.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode) return null;
            using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            return ParseEntry(doc.RootElement);
        }
        catch { return null; }
    }

    private async Task<List<LrcEntry>> QuerySearchAsync(string url, CancellationToken ct)
    {
        var result = new List<LrcEntry>();
        try
        {
            using var resp = await _http.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode) return result;
            using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return result;

            foreach (var el in doc.RootElement.EnumerateArray())
            {
                var entry = ParseEntry(el);
                if (entry != null) result.Add(entry);
            }
        }
        catch { }
        return result;
    }

    private static LrcEntry? ParseEntry(JsonElement el)
    {
        if (el.ValueKind != JsonValueKind.Object) return null;
        double dur = el.TryGetProperty("duration", out var d) && d.ValueKind == JsonValueKind.Number ? d.GetDouble() : 0;
        return new LrcEntry(
            dur,
            GetStr(el, "artistName"),
            GetStr(el, "trackName"),
            GetStr(el, "syncedLyrics"),
            GetStr(el, "plainLyrics"));
    }

    private static string? GetStr(JsonElement el, string prop)
        => el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    public static List<LyricLine> ParseLrc(string syncedLyrics)
    {
        var list = new List<LyricLine>();
        foreach (var raw in syncedLyrics.Split('\n'))
        {
            var matches = TimestampRegex.Matches(raw);
            if (matches.Count == 0) continue;

            var text = AnyBracketTagRegex.Replace(raw, "").Trim();
            foreach (Match m in matches)
            {
                if (int.TryParse(m.Groups[1].Value, out var min) &&
                    double.TryParse(m.Groups[2].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var sec))
                {
                    list.Add(new LyricLine(min * 60.0 + sec, string.IsNullOrEmpty(text) ? "♪" : text));
                }
            }
        }
        list.Sort((a, b) => a.TimeSeconds.CompareTo(b.TimeSeconds));
        return list;
    }
}
