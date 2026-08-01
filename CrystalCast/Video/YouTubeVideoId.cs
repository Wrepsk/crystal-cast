using System.Text.RegularExpressions;

namespace CrystalCast.Video;

public static partial class YouTubeVideoId
{
    public static bool TryParseSource(string input, out YouTubeSourceReference source)
    {
        source = default;
        if (string.IsNullOrWhiteSpace(input))
            return false;

        input = input.Trim();
        if (IsValidVideoId(input))
        {
            source = new YouTubeSourceReference(YouTubeSourceKind.Video, input, string.Empty, string.Empty);
            return true;
        }

        if (IsValidChannelId(input))
        {
            source = new YouTubeSourceReference(YouTubeSourceKind.LiveChannel, string.Empty, string.Empty, input);
            return true;
        }

        if (IsValidListId(input))
        {
            source = new YouTubeSourceReference(YouTubeSourceKind.Playlist, string.Empty, input, string.Empty);
            return true;
        }

        if (!BrowserUriPolicy.TryCreateHttpUri(input, out var uri))
            return false;

        var host = uri.IdnHost.ToLowerInvariant();
        if (!BrowserUriPolicy.IsHostOrSubdomain(uri, "youtube.com", "youtube-nocookie.com")
            && !BrowserUriPolicy.IsExactHost(uri, "youtu.be", "www.youtu.be"))
        {
            return false;
        }

        var query = ParseQuery(uri.Query);
        var segments = GetPathSegments(uri);
        var videoId = GetVideoId(host, query, segments);
        var playlistId = GetPlaylistId(query);
        var liveChannelId = GetLiveChannelId(query, segments);
        if (!string.IsNullOrWhiteSpace(liveChannelId))
        {
            source = new YouTubeSourceReference(YouTubeSourceKind.LiveChannel, string.Empty, string.Empty, liveChannelId);
            return true;
        }

        if (!string.IsNullOrWhiteSpace(playlistId))
        {
            source = new YouTubeSourceReference(YouTubeSourceKind.Playlist, videoId, playlistId, string.Empty);
            return true;
        }

        if (!string.IsNullOrWhiteSpace(videoId))
        {
            source = new YouTubeSourceReference(YouTubeSourceKind.Video, videoId, string.Empty, string.Empty);
            return true;
        }

        return false;
    }

    public static bool TryParse(string input, out string videoId)
    {
        videoId = string.Empty;
        if (!TryParseSource(input, out var source) || string.IsNullOrWhiteSpace(source.VideoId))
            return false;

        videoId = source.VideoId;
        return true;
    }

    public static string BuildCanonicalWatchUrl(string videoId)
    {
        return IsValidVideoId(videoId) ? $"https://www.youtube.com/watch?v={videoId}" : string.Empty;
    }

    public static string BuildCanonicalSourceUrl(YouTubeSourceReference source, string currentVideoId = "")
    {
        return source.Kind switch
        {
            YouTubeSourceKind.Video => BuildCanonicalWatchUrl(source.VideoId),
            YouTubeSourceKind.Playlist => BuildCanonicalPlaylistUrl(source.PlaylistId, FirstValidVideoId(currentVideoId, source.VideoId)),
            YouTubeSourceKind.LiveChannel => IsValidChannelId(source.LiveChannelId)
                ? $"https://www.youtube.com/embed/live_stream?channel={source.LiveChannelId}"
                : string.Empty,
            _ => string.Empty,
        };
    }

    public static bool IsValidVideoId(string videoId)
    {
        return !string.IsNullOrWhiteSpace(videoId) && VideoIdRegex().IsMatch(videoId);
    }

    public static bool IsValidListId(string listId)
    {
        return !string.IsNullOrWhiteSpace(listId) && ListIdRegex().IsMatch(listId);
    }

    public static bool IsValidChannelId(string channelId)
    {
        return !string.IsNullOrWhiteSpace(channelId) && ChannelIdRegex().IsMatch(channelId);
    }

    private static string BuildCanonicalPlaylistUrl(string playlistId, string videoId)
    {
        if (!IsValidListId(playlistId))
            return string.Empty;

        return IsValidVideoId(videoId)
            ? $"https://www.youtube.com/watch?v={videoId}&list={playlistId}"
            : $"https://www.youtube.com/playlist?list={playlistId}";
    }

    private static string FirstValidVideoId(params string[] videoIds)
    {
        foreach (var videoId in videoIds)
        {
            if (IsValidVideoId(videoId))
                return videoId;
        }

        return string.Empty;
    }

    private static string GetVideoId(string host, Dictionary<string, string> query, string[] segments)
    {
        if (host is "youtu.be" or "www.youtu.be")
            return TryUsePathSegment(segments, 0, out var shortVideoId) ? shortVideoId : string.Empty;

        if (query.TryGetValue("v", out var queryVideoId) && IsValidVideoId(queryVideoId))
            return queryVideoId;

        if (segments.Length >= 2 && segments[0] is "embed" or "shorts" or "live" && IsValidVideoId(segments[1]))
            return segments[1];

        return string.Empty;
    }

    private static string GetPlaylistId(Dictionary<string, string> query)
    {
        return query.TryGetValue("list", out var listId) && IsValidListId(listId)
            ? listId
            : string.Empty;
    }

    private static string GetLiveChannelId(Dictionary<string, string> query, string[] segments)
    {
        if (query.TryGetValue("channel", out var queryChannelId) && IsValidChannelId(queryChannelId))
            return queryChannelId;

        if (segments is ["channel", var channelId, "live", ..] && IsValidChannelId(channelId))
            return channelId;

        return string.Empty;
    }

    private static bool TryUsePathSegment(string[] segments, int index, out string videoId)
    {
        videoId = string.Empty;
        if (segments.Length <= index || !IsValidVideoId(segments[index]))
            return false;

        videoId = segments[index];
        return true;
    }

    private static string[] GetPathSegments(Uri uri)
    {
        return uri.AbsolutePath
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(Uri.UnescapeDataString)
            .ToArray();
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

    [GeneratedRegex("^[A-Za-z0-9_-]{11}$", RegexOptions.CultureInvariant)]
    private static partial Regex VideoIdRegex();

    [GeneratedRegex("^[A-Za-z0-9_-]{10,128}$", RegexOptions.CultureInvariant)]
    private static partial Regex ListIdRegex();

    [GeneratedRegex("^UC[A-Za-z0-9_-]{22}$", RegexOptions.CultureInvariant)]
    private static partial Regex ChannelIdRegex();
}

public readonly record struct YouTubeSourceReference(
    YouTubeSourceKind Kind,
    string VideoId,
    string PlaylistId,
    string LiveChannelId) : IBrowserSourceReference
{
    public string DisplayName => Kind switch
    {
        YouTubeSourceKind.Playlist when !string.IsNullOrWhiteSpace(VideoId) => $"Playlist: {PlaylistId} (starting at {VideoId})",
        YouTubeSourceKind.Playlist => $"Playlist: {PlaylistId}",
        YouTubeSourceKind.LiveChannel => $"Live channel: {LiveChannelId}",
        _ => $"Video ID: {VideoId}",
    };
}

public enum YouTubeSourceKind
{
    Video = 0,
    Playlist = 1,
    LiveChannel = 2,
}
