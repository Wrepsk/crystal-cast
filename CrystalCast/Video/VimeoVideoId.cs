using System.Text.RegularExpressions;

namespace CrystalCast.Video;

public static partial class VimeoVideoId
{
    public static bool TryParseSource(string input, out VimeoSourceReference source)
    {
        source = default;
        if (string.IsNullOrWhiteSpace(input))
            return false;

        input = input.Trim();
        if (IsValidVideoId(input))
        {
            source = new VimeoSourceReference(input, string.Empty);
            return true;
        }

        if (!BrowserUriPolicy.TryCreateHttpUri(input, out var uri))
            return false;

        if (!BrowserUriPolicy.IsHostOrSubdomain(uri, "vimeo.com"))
        {
            return false;
        }

        var query = ParseQuery(uri.Query);
        var segments = GetPathSegments(uri);
        var hash = TryGetQueryValue(query, "h", out var queryHash) && IsValidHash(queryHash)
            ? queryHash
            : string.Empty;

        if (TryExtractVideoFromPath(segments, out var videoId, out var pathHash))
        {
            source = new VimeoSourceReference(videoId, string.IsNullOrWhiteSpace(hash) ? pathHash : hash);
            return true;
        }

        return false;
    }

    public static string BuildCanonicalSourceUrl(VimeoSourceReference source, string currentVideoId = "")
    {
        var videoId = FirstValidVideoId(currentVideoId, source.VideoId);
        if (string.IsNullOrWhiteSpace(videoId))
            return string.Empty;

        return IsValidHash(source.Hash)
            ? $"https://vimeo.com/{videoId}?h={source.Hash}"
            : $"https://vimeo.com/{videoId}";
    }

    public static bool IsValidVideoId(string videoId)
    {
        return !string.IsNullOrWhiteSpace(videoId) && VideoIdRegex().IsMatch(videoId.Trim());
    }

    public static bool IsValidHash(string hash)
    {
        return !string.IsNullOrWhiteSpace(hash) && HashRegex().IsMatch(hash.Trim());
    }

    private static string FirstValidVideoId(params string[] videoIds)
    {
        foreach (var videoId in videoIds)
        {
            if (IsValidVideoId(videoId))
                return videoId.Trim();
        }

        return string.Empty;
    }

    private static bool TryExtractVideoFromPath(string[] segments, out string videoId, out string hash)
    {
        videoId = string.Empty;
        hash = string.Empty;
        for (var i = 0; i < segments.Length; i++)
        {
            if (!IsValidVideoId(segments[i]))
                continue;

            videoId = segments[i];
            if (i + 1 < segments.Length && IsValidHash(segments[i + 1]))
                hash = segments[i + 1];
            return true;
        }

        return false;
    }

    private static string[] GetPathSegments(Uri uri)
    {
        return uri.AbsolutePath
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(Uri.UnescapeDataString)
            .ToArray();
    }

    private static bool TryGetQueryValue(Dictionary<string, string> query, string key, out string value)
    {
        return query.TryGetValue(key, out value!);
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(query))
            return result;

        foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split('=', 2);
            var key = Uri.UnescapeDataString(parts[0].Replace("+", " ", StringComparison.Ordinal));
            var value = parts.Length == 2 ? Uri.UnescapeDataString(parts[1].Replace("+", " ", StringComparison.Ordinal)) : string.Empty;
            result[key] = value;
        }

        return result;
    }

    [GeneratedRegex("^[0-9]{1,18}$", RegexOptions.CultureInvariant)]
    private static partial Regex VideoIdRegex();

    [GeneratedRegex("^[A-Za-z0-9]{6,128}$", RegexOptions.CultureInvariant)]
    private static partial Regex HashRegex();
}

public readonly record struct VimeoSourceReference(
    string VideoId,
    string Hash) : IBrowserSourceReference
{
    public string DisplayName => string.IsNullOrWhiteSpace(Hash)
        ? $"Vimeo video: {VideoId}"
        : $"Vimeo video: {VideoId} (unlisted)";
}
