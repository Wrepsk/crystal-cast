using System.Text.RegularExpressions;

namespace CrystalCast.Video;

public static partial class YouTubeVideoId
{
    public static bool TryParse(string input, out string videoId)
    {
        videoId = string.Empty;
        if (string.IsNullOrWhiteSpace(input))
            return false;

        input = input.Trim();
        if (IsValidVideoId(input))
        {
            videoId = input;
            return true;
        }

        if (!Uri.TryCreate(input, UriKind.Absolute, out var uri))
            return false;

        var host = uri.Host.ToLowerInvariant();
        if (host is "youtu.be" or "www.youtu.be")
            return TryUsePathSegment(uri, 0, out videoId);

        if (!host.EndsWith("youtube.com", StringComparison.OrdinalIgnoreCase) && !host.EndsWith("youtube-nocookie.com", StringComparison.OrdinalIgnoreCase))
            return false;

        var query = ParseQuery(uri.Query);
        if (query.TryGetValue("v", out var queryVideoId) && IsValidVideoId(queryVideoId))
        {
            videoId = queryVideoId;
            return true;
        }

        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length >= 2 && segments[0] is "embed" or "shorts" or "live" && IsValidVideoId(segments[1]))
        {
            videoId = segments[1];
            return true;
        }

        return false;
    }

    public static string BuildCanonicalWatchUrl(string videoId)
    {
        return IsValidVideoId(videoId) ? $"https://www.youtube.com/watch?v={videoId}" : string.Empty;
    }

    public static bool IsValidVideoId(string videoId)
    {
        return VideoIdRegex().IsMatch(videoId);
    }

    private static bool TryUsePathSegment(Uri uri, int index, out string videoId)
    {
        videoId = string.Empty;
        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length <= index || !IsValidVideoId(segments[index]))
            return false;

        videoId = segments[index];
        return true;
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(query))
            return result;

        foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split('=', 2);
            var key = Uri.UnescapeDataString(parts[0]);
            var value = parts.Length == 2 ? Uri.UnescapeDataString(parts[1]) : string.Empty;
            result[key] = value;
        }

        return result;
    }

    [GeneratedRegex("^[A-Za-z0-9_-]{11}$", RegexOptions.CultureInvariant)]
    private static partial Regex VideoIdRegex();
}
