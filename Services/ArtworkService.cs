using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace NotchturneWin.Services;

public class ArtworkService
{
    private readonly HttpClient _http;
    private static readonly Regex ItunesSizeRegex = new(@"\d+x\d+(bb)?\.(jpg|png)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public ArtworkService(HttpClient http)
    {
        _http = http;
    }

    private record CoverCandidate(string Url, bool ExactTitle, bool AlbumMatch, int SourcePriority);

    public async Task<BitmapImage?> FetchHdCoverAsync(string rawTitle, string rawArtist, string? album, CancellationToken ct)
    {
        var (title, artist) = MetadataHelper.CleanMeta(rawTitle, rawArtist);
        if (string.IsNullOrWhiteSpace(title)) return null;

        var baseTitle = MetadataHelper.StripSubtitle(title);
        var normTitle = MetadataHelper.Normalize(baseTitle);
        var normAlbum = MetadataHelper.Normalize(album);
        var artistParts = MetadataHelper.GetArtistParts(artist);
        var primaryArtist = MetadataHelper.GetPrimaryArtist(artist);

        if (string.IsNullOrEmpty(normTitle)) return null;

        var candidates = new List<CoverCandidate>();
        var seenUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void TryAdd(string? cTitle, string? cArtist, string? url, string? cAlbum, int priority)
        {
            if (string.IsNullOrWhiteSpace(url) || !seenUrls.Add(url)) return;
            var rt = MetadataHelper.Normalize(cTitle);
            if (string.IsNullOrEmpty(rt)) return;

            bool titleHit = rt.Contains(normTitle, StringComparison.Ordinal) || normTitle.Contains(rt, StringComparison.Ordinal);
            bool artistHit = artistParts.Count == 0 || MetadataHelper.ArtistMatches(cArtist, artistParts);
            if (!titleHit || !artistHit) return;

            bool exact = rt == normTitle;
            bool albumHit = !string.IsNullOrEmpty(normAlbum) && !string.IsNullOrEmpty(cAlbum) &&
                            (MetadataHelper.Normalize(cAlbum).Contains(normAlbum, StringComparison.Ordinal) ||
                             normAlbum.Contains(MetadataHelper.Normalize(cAlbum), StringComparison.Ordinal));
            candidates.Add(new CoverCandidate(url, exact, albumHit, priority));
        }

        var term = Uri.EscapeDataString($"{primaryArtist} {baseTitle}".Trim());
        var tasks = new List<Task>
        {
            QueryItunesSongsAsync($"https://itunes.apple.com/search?media=music&entity=song&limit=20&term={term}", 2, TryAdd, ct),
            QueryItunesSongsAsync($"https://itunes.apple.com/search?media=music&entity=song&limit=20&country=us&term={term}", 2, TryAdd, ct),
            QueryDeezerAsync($"https://api.deezer.com/search?limit=15&q={term}", 1, TryAdd, ct)
        };

        if (!string.IsNullOrEmpty(normAlbum) && !string.IsNullOrEmpty(primaryArtist))
        {
            var albumTerm = Uri.EscapeDataString($"{primaryArtist} {album}".Trim());
            tasks.Add(QueryItunesAlbumsAsync($"https://itunes.apple.com/search?media=music&entity=album&limit=12&term={albumTerm}", normAlbum, artistParts, seenUrls, candidates, ct));
        }

        try { await Task.WhenAll(tasks); } catch { }
        if (ct.IsCancellationRequested || candidates.Count == 0) return null;

        // Also try primary artist alone if artist had collaborations and nothing matched well
        var best = candidates
            .OrderByDescending(c => (c.AlbumMatch ? 4 : 0) + (c.ExactTitle ? 2 : 0) + c.SourcePriority)
            .FirstOrDefault();

        if (best == null) return null;

        try
        {
            using var resp = await _http.GetAsync(best.Url, ct);
            if (!resp.IsSuccessStatusCode) return null;
            var bytes = await resp.Content.ReadAsByteArrayAsync(ct);
            if (ct.IsCancellationRequested || bytes.Length == 0) return null;

            return CreateFrozenBitmap(bytes);
        }
        catch
        {
            return null;
        }
    }

    private async Task QueryItunesSongsAsync(string url, int priority, Action<string?, string?, string?, string?, int> add, CancellationToken ct)
    {
        try
        {
            using var resp = await _http.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode) return;
            using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            if (!doc.RootElement.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array) return;

            foreach (var item in results.EnumerateArray())
            {
                var art = GetStr(item, "artworkUrl100");
                if (string.IsNullOrEmpty(art)) continue;
                var hd = ItunesSizeRegex.Replace(art, "600x600bb.$2");
                lock (add)
                {
                    add(GetStr(item, "trackName"), GetStr(item, "artistName"), hd, GetStr(item, "collectionName"), priority);
                }
            }
        }
        catch { }
    }

    private async Task QueryItunesAlbumsAsync(string url, string normAlbum, List<string> artistParts, HashSet<string> seenUrls, List<CoverCandidate> candidates, CancellationToken ct)
    {
        try
        {
            using var resp = await _http.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode) return;
            using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            if (!doc.RootElement.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array) return;

            foreach (var item in results.EnumerateArray())
            {
                var art = GetStr(item, "artworkUrl100");
                var col = GetStr(item, "collectionName");
                var cArt = GetStr(item, "artistName");
                if (string.IsNullOrEmpty(art) || string.IsNullOrEmpty(col)) continue;

                var rCol = MetadataHelper.Normalize(col);
                bool albumHit = rCol.Contains(normAlbum, StringComparison.Ordinal) || normAlbum.Contains(rCol, StringComparison.Ordinal);
                bool artistHit = artistParts.Count == 0 || MetadataHelper.ArtistMatches(cArt, artistParts);
                if (!albumHit || !artistHit) continue;

                var hd = ItunesSizeRegex.Replace(art, "600x600bb.$2");
                lock (candidates)
                {
                    if (seenUrls.Add(hd))
                        candidates.Add(new CoverCandidate(hd, false, true, 2));
                }
            }
        }
        catch { }
    }

    private async Task QueryDeezerAsync(string url, int priority, Action<string?, string?, string?, string?, int> add, CancellationToken ct)
    {
        try
        {
            using var resp = await _http.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode) return;
            using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) return;

            foreach (var item in data.EnumerateArray())
            {
                if (!item.TryGetProperty("album", out var alb) || alb.ValueKind != JsonValueKind.Object) continue;
                var cover = GetStr(alb, "cover_xl") ?? GetStr(alb, "cover_big");
                if (string.IsNullOrEmpty(cover)) continue;
                string? cArtist = item.TryGetProperty("artist", out var artObj) ? GetStr(artObj, "name") : null;
                lock (add)
                {
                    add(GetStr(item, "title"), cArtist, cover, GetStr(alb, "title"), priority);
                }
            }
        }
        catch { }
    }

    private static string? GetStr(JsonElement el, string prop)
        => el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    public static BitmapImage? CreateFrozenBitmap(byte[] bytes)
    {
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.StreamSource = new MemoryStream(bytes);
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Extracts a rich, vibrant dominant accent color from album art (inspired by spotify-lyrics).
    /// </summary>
    public static Color ExtractAccentColor(BitmapSource? source)
    {
        var fallback = Color.FromRgb(65, 75, 110);
        if (source == null || source.PixelWidth <= 0 || source.PixelHeight <= 0)
            return fallback;

        try
        {
            BitmapSource bmp = source.Format == PixelFormats.Bgra32
                ? source
                : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);

            int w = bmp.PixelWidth;
            int h = bmp.PixelHeight;
            int stride = w * 4;
            var pixels = new byte[h * stride];
            bmp.CopyPixels(pixels, stride, 0);

            const int hueBins = 24;
            var binR = new double[hueBins];
            var binG = new double[hueBins];
            var binB = new double[hueBins];
            var binW = new double[hueBins];

            double avgR = 0, avgG = 0, avgB = 0;
            int count = 0;

            int stepX = Math.Max(1, w / 28);
            int stepY = Math.Max(1, h / 28);

            for (int y = 0; y < h; y += stepY)
            {
                int row = y * stride;
                for (int x = 0; x < w; x += stepX)
                {
                    int idx = row + x * 4;
                    if (pixels[idx + 3] < 40) continue;

                    double b = pixels[idx];
                    double g = pixels[idx + 1];
                    double r = pixels[idx + 2];

                    avgR += r; avgG += g; avgB += b;
                    count++;

                    double mx = Math.Max(r, Math.Max(g, b));
                    double mn = Math.Min(r, Math.Min(g, b));
                    double d = mx - mn;
                    double v = mx / 255.0;
                    double s = mx > 0 ? d / mx : 0;

                    if (s >= 0.25 && v >= 0.18)
                    {
                        double hue = 0;
                        if (d > 0)
                        {
                            if (mx == r) hue = ((g - b) / d) % 6;
                            else if (mx == g) hue = (b - r) / d + 2;
                            else hue = (r - g) / d + 4;
                            hue *= 60;
                            if (hue < 0) hue += 360;
                        }
                        int bin = Math.Clamp((int)(hue / (360.0 / hueBins)), 0, hueBins - 1);
                        double weight = s * s * v;
                        binR[bin] += r * weight;
                        binG[bin] += g * weight;
                        binB[bin] += b * weight;
                        binW[bin] += weight;
                    }
                }
            }

            int bestBin = -1;
            double bestWeight = 0;
            for (int i = 0; i < hueBins; i++)
            {
                if (binW[i] > bestWeight)
                {
                    bestWeight = binW[i];
                    bestBin = i;
                }
            }

            double fr, fg, fb;
            if (bestBin >= 0 && bestWeight > 0.2)
            {
                fr = binR[bestBin] / binW[bestBin];
                fg = binG[bestBin] / binW[bestBin];
                fb = binB[bestBin] / binW[bestBin];
            }
            else if (count > 0)
            {
                fr = avgR / count;
                fg = avgG / count;
                fb = avgB / count;
            }
            else
            {
                return fallback;
            }

            // Ensure color isn't too dark so the ambient glow is visible
            double lum = (0.299 * fr + 0.587 * fg + 0.114 * fb) / 255.0;
            if (lum < 0.22)
            {
                double lift = (0.22 - lum) * 160;
                fr = Math.Min(255, fr + lift);
                fg = Math.Min(255, fg + lift);
                fb = Math.Min(255, fb + lift);
            }

            return Color.FromRgb((byte)Math.Clamp(fr, 0, 255), (byte)Math.Clamp(fg, 0, 255), (byte)Math.Clamp(fb, 0, 255));
        }
        catch
        {
            return fallback;
        }
    }

    /// <summary>
    /// Extracts a multi-color vivid palette (up to 6 distinct colors) and average color from album art.
    /// Uses 36 hue bins with 18° merging and vivid boost (inspired by spotify-lyrics).
    /// </summary>
    public static (List<Color> Palette, Color Average) ExtractCoverPalette(BitmapSource? source)
    {
        var fallback = new List<Color>
        {
            Color.FromRgb(68, 86, 150),
            Color.FromRgb(120, 62, 138),
            Color.FromRgb(42, 118, 146)
        };
        var fallbackAvg = Color.FromRgb(32, 36, 54);

        if (source == null || source.PixelWidth <= 0 || source.PixelHeight <= 0)
            return (fallback, fallbackAvg);

        try
        {
            BitmapSource bmp = source.Format == PixelFormats.Bgra32
                ? source
                : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);

            int w = bmp.PixelWidth;
            int h = bmp.PixelHeight;
            int stride = w * 4;
            var pixels = new byte[h * stride];
            bmp.CopyPixels(pixels, stride, 0);

            // Sample center square (avoids black bars on non-square video thumbnails)
            int sq = Math.Min(w, h);
            int x0 = (w - sq) / 2;
            int y0 = (h - sq) / 2;
            int step = Math.Max(1, sq / 48);

            const int hueBins = 36;
            var hn = new double[hueBins];
            var hr = new double[hueBins];
            var hg = new double[hueBins];
            var hb = new double[hueBins];
            var hw = new double[hueBins];

            double avgR = 0, avgG = 0, avgB = 0;
            int total = 0;

            for (int y = y0; y < y0 + sq; y += step)
            {
                int row = y * stride;
                for (int x = x0; x < x0 + sq; x += step)
                {
                    int idx = row + x * 4;
                    if (pixels[idx + 3] < 40) continue;

                    double b = pixels[idx];
                    double g = pixels[idx + 1];
                    double r = pixels[idx + 2];

                    avgR += r; avgG += g; avgB += b;
                    total++;

                    var (hue, sat, val) = RgbToHsv(r, g, b);
                    if (sat >= 0.28 && val >= 0.20)
                    {
                        int bin = Math.Clamp((int)(hue / (360.0 / hueBins)), 0, hueBins - 1);
                        double weight = Math.Pow(sat * val, 2);
                        hn[bin]++;
                        hr[bin] += r * weight;
                        hg[bin] += g * weight;
                        hb[bin] += b * weight;
                        hw[bin] += weight;
                    }
                }
            }

            if (total == 0) return (fallback, fallbackAvg);

            var avgColor = Color.FromRgb(
                (byte)Math.Clamp(avgR / total, 0, 255),
                (byte)Math.Clamp(avgG / total, 0, 255),
                (byte)Math.Clamp(avgB / total, 0, 255));

            // Group hue bins within 18 degrees
            var order = Enumerable.Range(0, hueBins)
                .Where(q => hn[q] > 0)
                .OrderByDescending(q => hn[q])
                .ToList();

            double minSeed = Math.Max(3, total * 0.001);
            var groups = new List<(double Hue, double N, double R, double G, double B, double W)>();

            foreach (int q in order)
            {
                double hue = (q + 0.5) * (360.0 / hueBins);
                int bestIdx = -1;
                double bestDist = double.MaxValue;
                for (int i = 0; i < groups.Count; i++)
                {
                    double d = Math.Abs(groups[i].Hue - hue) % 360.0;
                    if (d > 180.0) d = 360.0 - d;
                    if (d < bestDist) { bestDist = d; bestIdx = i; }
                }

                if (bestIdx >= 0 && bestDist < 18.0)
                {
                    var g = groups[bestIdx];
                    groups[bestIdx] = (g.Hue, g.N + hn[q], g.R + hr[q], g.G + hg[q], g.B + hb[q], g.W + hw[q]);
                }
                else if (hn[q] >= minSeed)
                {
                    groups.Add((hue, hn[q], hr[q], hg[q], hb[q], hw[q]));
                }
            }

            var palette = groups
                .Where(g => g.W > 0.001)
                .OrderByDescending(g => Math.Sqrt(g.N / total) * (0.6 + 0.4 * (g.W / g.N)))
                .Take(6)
                .Select(g => MakeVivid(g.R / g.W, g.G / g.W, g.B / g.W))
                .ToList();

            if (palette.Count == 0)
            {
                // Monochrome/neutral cover fallback: build 3 tasteful tinted tones from average
                var (ah, asat, aval) = RgbToHsv(avgColor.R, avgColor.G, avgColor.B);
                double baseV = Math.Clamp(aval, 0.32, 0.72);
                palette.Add(HsvToColor(ah, Math.Max(asat, 0.14), baseV));
                palette.Add(HsvToColor((ah + 28) % 360, Math.Max(asat * 0.9, 0.12), Math.Min(0.82, baseV + 0.14)));
                palette.Add(HsvToColor((ah + 332) % 360, Math.Max(asat * 0.8, 0.10), Math.Max(0.24, baseV - 0.10)));
            }
            else if (palette.Count == 1)
            {
                var (h1, s1, v1) = RgbToHsv(palette[0].R, palette[0].G, palette[0].B);
                palette.Add(HsvToColor((h1 + 32) % 360, Math.Min(1.0, s1 * 0.92), Math.Clamp(v1 * 0.88, 0.36, 0.92)));
                palette.Add(HsvToColor((h1 + 328) % 360, Math.Min(1.0, s1 * 0.85), Math.Clamp(v1 * 0.78, 0.30, 0.86)));
            }
            else if (palette.Count == 2)
            {
                // Add a harmonic bridge color between the first two
                palette.Add(Color.FromRgb(
                    (byte)((palette[0].R + palette[1].R) / 2),
                    (byte)((palette[0].G + palette[1].G) / 2),
                    (byte)((palette[0].B + palette[1].B) / 2)));
            }

            return (palette, avgColor);
        }
        catch
        {
            return (fallback, fallbackAvg);
        }
    }

    private static (double H, double S, double V) RgbToHsv(double r, double g, double b)
    {
        double mx = Math.Max(r, Math.Max(g, b));
        double mn = Math.Min(r, Math.Min(g, b));
        double d = mx - mn;
        double h = 0;
        if (d > 0)
        {
            if (mx == r) h = ((g - b) / d) % 6;
            else if (mx == g) h = (b - r) / d + 2;
            else h = (r - g) / d + 4;
            h *= 60;
            if (h < 0) h += 360;
        }
        double s = mx > 0 ? d / mx : 0;
        double v = mx / 255.0;
        return (h, s, v);
    }

    private static Color HsvToColor(double h, double s, double v)
    {
        h = ((h % 360) + 360) % 360;
        double c = v * s;
        double x = c * (1 - Math.Abs((h / 60.0) % 2 - 1));
        double m = v - c;
        double r = 0, g = 0, b = 0;
        if (h < 60) { r = c; g = x; }
        else if (h < 120) { r = x; g = c; }
        else if (h < 180) { g = c; b = x; }
        else if (h < 240) { g = x; b = c; }
        else if (h < 300) { r = x; b = c; }
        else { r = c; b = x; }
        return Color.FromRgb(
            (byte)Math.Clamp((r + m) * 255, 0, 255),
            (byte)Math.Clamp((g + m) * 255, 0, 255),
            (byte)Math.Clamp((b + m) * 255, 0, 255));
    }

    private static Color MakeVivid(double r, double g, double b)
    {
        var (h, s, v) = RgbToHsv(r, g, b);
        return HsvToColor(h, Math.Min(1.0, s * 1.18), Math.Max(v, 0.42));
    }
}
