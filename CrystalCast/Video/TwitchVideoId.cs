using System.Text.RegularExpressions;

namespace CrystalCast.Video;

public static partial class TwitchVideoId
{
    public static bool TryParseSource(string input, out TwitchSourceReference source)
    {
        source = default;
        if (string.IsNullOrWhiteSpace(input))
            return false;

        input = input.Trim();
        if (IsValidVideoId(input))
        {
            source = new TwitchSourceReference(TwitchSourceKind.Video, NormalizeVideoId(input), string.Empty);
            return true;
        }

        if (IsValidChannelName(input))
        {
            source = new TwitchSourceReference(TwitchSourceKind.Channel, string.Empty, input.ToLowerInvariant());
            return true;
        }

        if (!Uri.TryCreate(input, UriKind.Absolute, out var uri))
            return false;

        var host = uri.Host.ToLowerInvariant();
        if (host is "clips.twitch.tv" || host.EndsWith(".clips.twitch.tv", StringComparison.OrdinalIgnoreCase))
            return false;

        if (host is not "twitch.tv" and not "www.twitch.tv" && !host.EndsWith(".twitch.tv", StringComparison.OrdinalIgnoreCase))
            return false;

        var segments = GetPathSegments(uri);
        if (segments.Length == 0)
            return false;

        if (segments[0].Equals("videos", StringComparison.OrdinalIgnoreCase)
            && segments.Length >= 2
            && IsValidNumericVideoId(segments[1]))
        {
            source = new TwitchSourceReference(TwitchSourceKind.Video, NormalizeVideoId(segments[1]), string.Empty);
            return true;
        }

        if (segments[0].Equals("clip", StringComparison.OrdinalIgnoreCase)
            || segments.Any(segment => segment.Equals("clip", StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        if (IsValidChannelName(segments[0]))
        {
            source = new TwitchSourceReference(TwitchSourceKind.Channel, string.Empty, segments[0].ToLowerInvariant());
            return true;
        }

        return false;
    }

    public static string BuildCanonicalSourceUrl(TwitchSourceReference source, string currentVideoId = "")
    {
        return source.Kind switch
        {
            TwitchSourceKind.Video => BuildCanonicalVideoUrl(FirstValidVideoId(currentVideoId, source.VideoId)),
            TwitchSourceKind.Channel => IsValidChannelName(source.ChannelName) ? $"https://www.twitch.tv/{source.ChannelName}" : string.Empty,
            _ => string.Empty,
        };
    }

    public static bool IsValidVideoId(string videoId)
    {
        return !string.IsNullOrWhiteSpace(videoId)
            && (VideoIdRegex().IsMatch(videoId) || IsValidNumericVideoId(videoId));
    }

    public static bool IsValidChannelName(string channelName)
    {
        return !string.IsNullOrWhiteSpace(channelName) && ChannelNameRegex().IsMatch(channelName);
    }

    private static string BuildCanonicalVideoUrl(string videoId)
    {
        if (!IsValidVideoId(videoId))
            return string.Empty;

        return $"https://www.twitch.tv/videos/{NormalizeVideoId(videoId)[1..]}";
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
        videoId = videoId.Trim();
        return videoId.StartsWith("v", StringComparison.OrdinalIgnoreCase)
            ? "v" + videoId[1..]
            : $"v{videoId}";
    }

    private static bool IsValidNumericVideoId(string videoId)
    {
        return !string.IsNullOrWhiteSpace(videoId) && NumericVideoIdRegex().IsMatch(videoId);
    }

    private static string[] GetPathSegments(Uri uri)
    {
        return uri.AbsolutePath
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(Uri.UnescapeDataString)
            .ToArray();
    }

    [GeneratedRegex("^v[0-9]{1,24}$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex VideoIdRegex();

    [GeneratedRegex("^[0-9]{1,24}$", RegexOptions.CultureInvariant)]
    private static partial Regex NumericVideoIdRegex();

    [GeneratedRegex("^[A-Za-z0-9_]{3,25}$", RegexOptions.CultureInvariant)]
    private static partial Regex ChannelNameRegex();
}

public readonly record struct TwitchSourceReference(
    TwitchSourceKind Kind,
    string VideoId,
    string ChannelName) : IBrowserSourceReference
{
    public string DisplayName => Kind switch
    {
        TwitchSourceKind.Video => $"Twitch VOD: {VideoId}",
        _ => $"Twitch channel: {ChannelName}",
    };
}

public enum TwitchSourceKind
{
    Channel = 0,
    Video = 1,
}
