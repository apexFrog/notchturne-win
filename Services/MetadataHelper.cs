using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace NotchturneWin.Services;

public static class MetadataHelper
{
    private static readonly Regex StripTopic = new(@"\s*-\s*Topic\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex StripVevo = new(@"\s*VEVO\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex StripBracketsTag = new(@"\s*[\(\[][^\)\]]*\b(official|video|audio|lyrics?|remaster(ed)?|visuali[sz]er|hd|hq|4k|mv|live|explicit|clean|version|edit|radio)\b[^\)\]]*[\)\]]", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex StripFeatBrackets = new(@"\s*[\(\[]\s*(feat|ft|with)\.?\s*[^\)\]]*[\)\]]", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex StripFeatInline = new(@"\s+\b(feat|ft)\.?\s+.*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ArtistSplitter = new(@",|&|;|\bfeat\.?\b|\bft\.?\b|\bx\b|\bwith\b|\/", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex NonAlphaNum = new(@"[^\p{L}\p{N}]+", RegexOptions.Compiled);
    private static readonly Regex DashSuffix = new(@"\s+[-–—]\s+.*$", RegexOptions.Compiled);

    public static string Normalize(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        var formD = s.ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(formD.Length);
        foreach (var ch in formD)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark)
                sb.Append(ch);
        }
        return NonAlphaNum.Replace(sb.ToString(), " ").Trim();
    }

    public static (string Title, string Artist) CleanMeta(string? title, string? artist)
    {
        var a = StripVevo.Replace(StripTopic.Replace(artist ?? "", ""), "").Trim();
        var t = (title ?? "").Trim();

        if (!string.IsNullOrEmpty(a))
        {
            var pre = a + " - ";
            if (t.StartsWith(pre, StringComparison.OrdinalIgnoreCase))
                t = t[pre.Length..].Trim();
        }

        t = StripBracketsTag.Replace(t, "");
        t = StripFeatBrackets.Replace(t, "");
        t = StripFeatInline.Replace(t, "").Trim();

        return (string.IsNullOrEmpty(t) ? (title ?? "").Trim() : t, a);
    }

    public static string StripSubtitle(string title)
    {
        var stripped = DashSuffix.Replace(title, "").Trim();
        return string.IsNullOrEmpty(stripped) ? title : stripped;
    }

    public static string GetPrimaryArtist(string? artist)
    {
        if (string.IsNullOrWhiteSpace(artist)) return "";
        var parts = ArtistSplitter.Split(artist)
            .Select(p => p.Trim())
            .Where(p => p.Length > 0)
            .ToList();
        return parts.Count > 0 ? parts[0] : artist.Trim();
    }

    public static List<string> GetArtistParts(string? artist)
    {
        if (string.IsNullOrWhiteSpace(artist)) return new();
        return ArtistSplitter.Split(artist)
            .Select(Normalize)
            .Where(p => p.Length >= 2)
            .Distinct()
            .ToList();
    }

    public static bool ArtistMatches(string? candidateArtist, List<string> targetParts)
    {
        var ra = Normalize(candidateArtist);
        if (string.IsNullOrEmpty(ra) || targetParts.Count == 0) return false;
        return targetParts.Any(p => ra.Contains(p, StringComparison.Ordinal) || p.Contains(ra, StringComparison.Ordinal));
    }

    public static bool ArtistMatchesLoose(string? candidateArtist, string? targetArtist)
    {
        var parts = GetArtistParts(targetArtist);
        if (parts.Count == 0) return true;
        if (ArtistMatches(candidateArtist, parts)) return true;
        var firstWord = Normalize(targetArtist).Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
        var ra = Normalize(candidateArtist);
        return !string.IsNullOrEmpty(firstWord) && (ra.Contains(firstWord, StringComparison.Ordinal) || firstWord.Contains(ra, StringComparison.Ordinal));
    }
}
