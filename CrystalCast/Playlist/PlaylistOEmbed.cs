using System.Text.Json;
using CrystalCast.Video;

namespace CrystalCast.Playlist;

/// <summary>A video's title, channel, thumbnail and length as the site describes it.</summary>
internal sealed record VideoMetadata(string Title, string Author, string ThumbnailUrl, int DurationSeconds);

/// <summary>
/// The sites' public oEmbed endpoints, which describe a video from its link without an API key. YouTube,
/// Vimeo and Dailymotion have one; Twitch and web pages don't, so their titles come from playing them.
/// </summary>
internal static class PlaylistOEmbed
{
    public const int MaxResponseBytes = 64 * 1024;

    public static bool IsSupported(BrowserSourceProviderKind provider)
    {
        return provider is BrowserSourceProviderKind.YouTube
            or BrowserSourceProviderKind.Vimeo
            or BrowserSourceProviderKind.Dailymotion;
    }

    public static bool TryBuildRequestUri(PlaylistEntry entry, out Uri requestUri)
    {
        requestUri = null!;
        if (!IsSupported(entry.ProviderKind) || !TryGetLink(entry, out var link))
            return false;

        var encoded = Uri.EscapeDataString(link);
        var address = entry.ProviderKind switch
        {
            BrowserSourceProviderKind.YouTube => $"https://www.youtube.com/oembed?format=json&url={encoded}",
            BrowserSourceProviderKind.Vimeo => $"https://vimeo.com/api/oembed.json?url={encoded}",
            _ => $"https://www.dailymotion.com/services/oembed?format=json&url={encoded}",
        };
        return Uri.TryCreate(address, UriKind.Absolute, out requestUri!);
    }

    /// <summary>Reads an oEmbed answer. Thumbnails on any host but the site's own image host are dropped.</summary>
    public static VideoMetadata? Parse(BrowserSourceProviderKind provider, string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return null;

            var title = ScreenPlaylistPolicy.CleanTitle(GetString(root, "title"));
            if (title.Length == 0)
                return null;

            var thumbnail = GetString(root, "thumbnail_url");
            return new VideoMetadata(
                title,
                ScreenPlaylistPolicy.CleanTitle(GetString(root, "author_name")),
                IsAllowedThumbnail(provider, thumbnail) ? thumbnail : string.Empty,
                root.TryGetProperty("duration", out var duration) && duration.ValueKind == JsonValueKind.Number && duration.TryGetInt32(out var seconds)
                    ? Math.Clamp(seconds, 0, 7 * 24 * 3600)
                    : 0);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static bool IsAllowedThumbnail(BrowserSourceProviderKind provider, string? url)
    {
        if (string.IsNullOrWhiteSpace(url)
            || url.Length > BrowserUriPolicy.MaximumUrlLength
            || !Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || !uri.IsDefaultPort)
            return false;

        var host = uri.IdnHost.TrimEnd('.').ToLowerInvariant();
        var imageHost = provider switch
        {
            BrowserSourceProviderKind.YouTube => "ytimg.com",
            BrowserSourceProviderKind.Vimeo => "vimeocdn.com",
            BrowserSourceProviderKind.Dailymotion => "dmcdn.net",
            _ => null,
        };
        return imageHost != null && (host == imageHost || host.EndsWith("." + imageHost, StringComparison.Ordinal));
    }

    /// <summary>Whether the link is one video, so its length describes the entry (not a playlist's first video).</summary>
    public static bool IsSingleVideo(PlaylistEntry entry)
    {
        return entry.ProviderKind switch
        {
            BrowserSourceProviderKind.YouTube => BrowserSourceDescriptors.YouTube.TryParse(entry.Url, out var youTube)
                && youTube is YouTubeSourceReference { Kind: YouTubeSourceKind.Video },
            BrowserSourceProviderKind.Dailymotion => BrowserSourceDescriptors.Dailymotion.TryParse(entry.Url, out var dailymotion)
                && dailymotion is DailymotionSourceReference { Kind: DailymotionSourceKind.Video },
            _ => true,
        };
    }

    /// <summary>
    /// The entry as a full link. YouTube gets its plain watch or playlist link, since its endpoint may not
    /// know Shorts or youtu.be forms; entries added by bare ID are rebuilt into one.
    /// </summary>
    private static bool TryGetLink(PlaylistEntry entry, out string link)
    {
        if (entry.ProviderKind != BrowserSourceProviderKind.YouTube && BrowserUriPolicy.TryCreateHttpUri(entry.Url, out _))
        {
            link = entry.Url;
            return true;
        }

        var descriptor = entry.ProviderKind switch
        {
            BrowserSourceProviderKind.YouTube => BrowserSourceDescriptors.YouTube,
            BrowserSourceProviderKind.Vimeo => BrowserSourceDescriptors.Vimeo,
            BrowserSourceProviderKind.Dailymotion => BrowserSourceDescriptors.Dailymotion,
            _ => null,
        };
        if (descriptor == null || !descriptor.TryParse(entry.Url, out var source))
        {
            link = string.Empty;
            return false;
        }

        link = descriptor.BuildCanonicalSourceUrl(source, source.VideoId);
        return BrowserUriPolicy.TryCreateHttpUri(link, out _);
    }

    private static string GetString(JsonElement root, string name)
    {
        return root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;
    }
}
