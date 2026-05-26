using System.Text.RegularExpressions;

namespace CrystalCast.Video;

public static partial class DailymotionVideoId
{
    public static bool TryParseSource(string input, out DailymotionSourceReference source)
    {
        source = default;
        if (string.IsNullOrWhiteSpace(input))
            return false;

        input = input.Trim();
        if (IsValidVideoId(input))
        {
            source = new DailymotionSourceReference(DailymotionSourceKind.Video, NormalizeVideoId(input), string.Empty);
            return true;
        }

        if (IsValidPlaylistId(input))
        {
            source = new DailymotionSourceReference(DailymotionSourceKind.Playlist, string.Empty, NormalizePlaylistId(input));
            return true;
        }

        if (!Uri.TryCreate(input, UriKind.Absolute, out var uri))
            return false;

        var host = uri.Host.ToLowerInvariant();
        if (host is "dai.ly" or "www.dai.ly")
        {
            var shortSegments = GetPathSegments(uri);
            if (TryExtractVideoId(shortSegments.FirstOrDefault(), out var shortVideoId))
            {
                source = new DailymotionSourceReference(DailymotionSourceKind.Video, shortVideoId, string.Empty);
                return true;
            }

            return false;
        }

        if (host is not "dailymotion.com" and not "www.dailymotion.com" and not "geo.dailymotion.com"
            && !host.EndsWith(".dailymotion.com", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var query = ParseQuery(uri.Query);
        var segments = GetPathSegments(uri);
        if (TryGetQueryValue(query, "video", out var queryVideoId) && TryExtractVideoId(queryVideoId, out var videoId))
        {
            source = new DailymotionSourceReference(DailymotionSourceKind.Video, videoId, string.Empty);
            return true;
        }

        if (TryGetQueryValue(query, "playlist", out var queryPlaylistId) && TryExtractPlaylistId(queryPlaylistId, out var playlistId))
        {
            source = new DailymotionSourceReference(DailymotionSourceKind.Playlist, string.Empty, playlistId);
            return true;
        }

        if (TryExtractVideoFromPath(segments, out var pathVideoId))
        {
            source = new DailymotionSourceReference(DailymotionSourceKind.Video, pathVideoId, string.Empty);
            return true;
        }

        if (TryExtractPlaylistFromPath(segments, out var pathPlaylistId))
        {
            source = new DailymotionSourceReference(DailymotionSourceKind.Playlist, string.Empty, pathPlaylistId);
            return true;
        }

        return false;
    }

    public static string BuildCanonicalSourceUrl(DailymotionSourceReference source, string currentVideoId = "")
    {
        return source.Kind switch
        {
            DailymotionSourceKind.Video => BuildCanonicalVideoUrl(FirstValidVideoId(currentVideoId, source.VideoId)),
            DailymotionSourceKind.Playlist => IsValidPlaylistId(source.PlaylistId)
                ? $"https://www.dailymotion.com/playlist/{source.PlaylistId}"
                : string.Empty,
            _ => string.Empty,
        };
    }

    public static bool IsValidVideoId(string videoId)
    {
        return !string.IsNullOrWhiteSpace(videoId) && VideoIdRegex().IsMatch(videoId.Trim());
    }

    public static bool IsValidPlaylistId(string playlistId)
    {
        return !string.IsNullOrWhiteSpace(playlistId) && PlaylistIdRegex().IsMatch(playlistId.Trim());
    }

    private static string BuildCanonicalVideoUrl(string videoId)
    {
        return IsValidVideoId(videoId)
            ? $"https://www.dailymotion.com/video/{NormalizeVideoId(videoId)}"
            : string.Empty;
    }

    private static string FirstValidVideoId(params string[] videoIds)
    {
        foreach (var videoId in videoIds)
        {
            if (IsValidVideoId(videoId))
                return NormalizeVideoId(videoId);
        }

        return string.Empty;
    }

    private static string NormalizeVideoId(string videoId)
    {
        return TrimSlug(videoId.Trim());
    }

    private static string NormalizePlaylistId(string playlistId)
    {
        return TrimSlug(playlistId.Trim());
    }

    private static bool TryExtractVideoFromPath(string[] segments, out string videoId)
    {
        videoId = string.Empty;
        for (var i = 0; i < segments.Length; i++)
        {
            if (!segments[i].Equals("video", StringComparison.OrdinalIgnoreCase)
                && !segments[i].Equals("embed", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var nextIndex = i + 1;
            if (nextIndex < segments.Length && TryExtractVideoId(segments[nextIndex], out videoId))
                return true;

            if (segments[i].Equals("embed", StringComparison.OrdinalIgnoreCase)
                && nextIndex < segments.Length
                && segments[nextIndex].Equals("video", StringComparison.OrdinalIgnoreCase)
                && nextIndex + 1 < segments.Length
                && TryExtractVideoId(segments[nextIndex + 1], out videoId))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryExtractPlaylistFromPath(string[] segments, out string playlistId)
    {
        playlistId = string.Empty;
        for (var i = 0; i < segments.Length - 1; i++)
        {
            if (segments[i].Equals("playlist", StringComparison.OrdinalIgnoreCase)
                && TryExtractPlaylistId(segments[i + 1], out playlistId))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryExtractVideoId(string? value, out string videoId)
    {
        videoId = string.Empty;
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var candidate = NormalizeVideoId(value);
        if (!IsValidVideoId(candidate))
            return false;

        videoId = candidate;
        return true;
    }

    private static bool TryExtractPlaylistId(string? value, out string playlistId)
    {
        playlistId = string.Empty;
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var candidate = NormalizePlaylistId(value);
        if (!IsValidPlaylistId(candidate))
            return false;

        playlistId = candidate;
        return true;
    }

    private static string TrimSlug(string value)
    {
        var underscoreIndex = value.IndexOf('_', StringComparison.Ordinal);
        if (underscoreIndex > 0)
            return value[..underscoreIndex];

        return value;
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

    [GeneratedRegex("^x[A-Za-z0-9]{2,31}$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex VideoIdRegex();

    [GeneratedRegex("^x[A-Za-z0-9]{2,31}$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PlaylistIdRegex();
}

public readonly record struct DailymotionSourceReference(
    DailymotionSourceKind Kind,
    string VideoId,
    string PlaylistId)
{
    public string DisplayName => Kind switch
    {
        DailymotionSourceKind.Playlist => $"Dailymotion playlist: {PlaylistId}",
        _ => $"Dailymotion video: {VideoId}",
    };
}

public enum DailymotionSourceKind
{
    Video = 0,
    Playlist = 1,
}
