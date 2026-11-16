using System.Text;
using System.Text.Json;
using CrystalCast.Sources;
using CrystalCast.Sources.Providers;

namespace CrystalCast.YouTube;

/// <summary>A video from the user's YouTube home page.</summary>
internal sealed record YouTubeVideo(
    string VideoId,
    string Title,
    string Channel,
    string Views,
    string Age,
    string Duration,
    bool IsLive)
{
    public string Url => $"https://www.youtube.com/watch?v={VideoId}";

    /// <summary>YouTube's medium thumbnail, 320×180, built from the ID so it never depends on the page.</summary>
    public string ThumbnailUrl => $"https://i.ytimg.com/vi/{VideoId}/mqdefault.jpg";
}

/// <summary>
/// Reads the videos on YouTube's home page out of its page data (<c>ytInitialData</c>). YouTube's page isn't a
/// public API and changes shape now and then, so this looks for video entries anywhere in the data instead of
/// following one fixed path, and knows both the older <c>videoRenderer</c> and the newer
/// <c>lockupViewModel</c> entries. Shorts, mixes, playlists and ads are left out.
/// </summary>
internal static class YouTubeFeed
{
    public const int MaxVideos = 60;
    private const int MaxDepth = 80;

    public static IReadOnlyList<YouTubeVideo> Parse(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 256 });
            var videos = new List<YouTubeVideo>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            Visit(document.RootElement, videos, seen, 0);
            return videos;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static void Visit(JsonElement element, List<YouTubeVideo> videos, HashSet<string> seen, int depth)
    {
        if (videos.Count >= MaxVideos || depth > MaxDepth)
            return;

        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    var video = property.Name switch
                    {
                        "videoRenderer" => FromVideoRenderer(property.Value),
                        "lockupViewModel" => FromLockup(property.Value),
                        _ => null,
                    };

                    if (video != null)
                    {
                        if (seen.Add(video.VideoId))
                            videos.Add(video);
                        continue;
                    }

                    // Shorts and ads have their own entries; their insides hold no regular videos.
                    if (property.Name is "reelItemRenderer" or "shortsLockupViewModel" or "adSlotRenderer")
                        continue;

                    Visit(property.Value, videos, seen, depth + 1);
                }

                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                    Visit(item, videos, seen, depth + 1);
                break;
        }
    }

    /// <summary>The older entry: <c>{ videoId, title, ownerText, viewCountText, publishedTimeText, lengthText }</c>.</summary>
    internal static YouTubeVideo? FromVideoRenderer(JsonElement renderer)
    {
        var id = Str(renderer, "videoId");
        if (!YouTubeVideoId.IsValidVideoId(id))
            return null;

        var isLive = HasLiveBadge(renderer);
        return new YouTubeVideo(
            id,
            Text(Child(renderer, "title")),
            FirstText(renderer, "ownerText", "longBylineText", "shortBylineText"),
            WithViewsWord(FirstText(renderer, "shortViewCountText", "viewCountText")),
            Text(Child(renderer, "publishedTimeText")),
            isLive ? string.Empty : Text(Child(renderer, "lengthText")),
            isLive);
    }

    /// <summary>
    /// The newer entry: <c>{ contentId, contentType, metadata.lockupMetadataViewModel { title, metadata rows },
    /// contentImage { badges } }</c>. The rows are the channel, then views and age.
    /// </summary>
    internal static YouTubeVideo? FromLockup(JsonElement lockup)
    {
        if (Str(lockup, "contentType") != "LOCKUP_CONTENT_TYPE_VIDEO")
            return null;

        var id = Str(lockup, "contentId");
        if (!YouTubeVideoId.IsValidVideoId(id))
            return null;

        var metadata = Child(Child(lockup, "metadata"), "lockupMetadataViewModel");
        var rows = Child(Child(Child(metadata, "metadata"), "contentMetadataViewModel"), "metadataRows");
        var parts = new List<List<string>>();
        if (rows.ValueKind == JsonValueKind.Array)
        {
            foreach (var row in rows.EnumerateArray())
            {
                var texts = new List<string>();
                var rowParts = Child(row, "metadataParts");
                if (rowParts.ValueKind == JsonValueKind.Array)
                {
                    foreach (var part in rowParts.EnumerateArray())
                    {
                        if (Text(Child(part, "text")) is { Length: > 0 } text)
                            texts.Add(text);
                    }
                }

                parts.Add(texts);
            }
        }

        var badges = new List<string>();
        CollectBadges(Child(lockup, "contentImage"), badges, 0);
        var isLive = badges.Any(badge => badge.Equals("LIVE", StringComparison.OrdinalIgnoreCase));
        var duration = isLive ? string.Empty : badges.FirstOrDefault(LooksLikeDuration) ?? string.Empty;
        var second = parts.Count > 1 ? parts[1] : [];
        return new YouTubeVideo(
            id,
            Text(Child(metadata, "title")),
            parts.Count > 0 && parts[0].Count > 0 ? parts[0][0] : string.Empty,
            second.Count > 0 ? WithViewsWord(second[0]) : string.Empty,
            second.Count > 1 ? second[1] : string.Empty,
            duration,
            isLive);
    }

    /// <summary>Texts of the thumbnail's badges (the length, or LIVE), wherever they sit in the thumbnail.</summary>
    private static void CollectBadges(JsonElement element, List<string> badges, int depth)
    {
        if (depth > 16)
            return;

        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (property.Name == "thumbnailBadgeViewModel" && Text(Child(property.Value, "text")) is { Length: > 0 } text)
                    badges.Add(text);
                else
                    CollectBadges(property.Value, badges, depth + 1);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
                CollectBadges(item, badges, depth + 1);
        }
    }

    /// <summary>The newer entries give views as a bare count ("11K"); the word makes it read like YouTube.</summary>
    internal static string WithViewsWord(string views) =>
        views.Length > 0 && views.All(c => char.IsAsciiDigit(c) || c is '.' or ',' or 'K' or 'M' or 'B')
            ? $"{views} views"
            : views;

    internal static bool LooksLikeDuration(string text) =>
        text.Length is > 2 and < 12 && text.Contains(':') && text.All(c => char.IsAsciiDigit(c) || c == ':');

    private static bool HasLiveBadge(JsonElement renderer)
    {
        var badges = Child(renderer, "badges");
        if (badges.ValueKind == JsonValueKind.Array
            && badges.EnumerateArray().Any(badge => Str(Child(badge, "metadataBadgeRenderer"), "style") == "BADGE_STYLE_TYPE_LIVE_NOW"))
        {
            return true;
        }

        var overlays = Child(renderer, "thumbnailOverlays");
        return overlays.ValueKind == JsonValueKind.Array
            && overlays.EnumerateArray().Any(overlay => Str(Child(overlay, "thumbnailOverlayTimeStatusRenderer"), "style") == "LIVE");
    }

    private static string FirstText(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (Text(Child(element, name)) is { Length: > 0 } text)
                return text;
        }

        return string.Empty;
    }

    /// <summary>YouTube's text in any of its shapes: <c>simpleText</c>, <c>runs[].text</c> or <c>content</c>.</summary>
    internal static string Text(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                return Clean(element.GetString());
            case JsonValueKind.Object:
                if (Str(element, "simpleText") is { Length: > 0 } simple)
                    return Clean(simple);
                if (Str(element, "content") is { Length: > 0 } content)
                    return Clean(content);
                var runs = Child(element, "runs");
                if (runs.ValueKind == JsonValueKind.Array)
                {
                    var builder = new StringBuilder();
                    foreach (var run in runs.EnumerateArray())
                        builder.Append(Str(run, "text"));
                    return Clean(builder.ToString());
                }

                return string.Empty;
            default:
                return string.Empty;
        }
    }

    private static string Clean(string? text) => (text ?? string.Empty).Replace('\n', ' ').Trim();

    private static JsonElement Child(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var child) ? child : default;

    private static string Str(JsonElement element, string name) =>
        Child(element, name) is { ValueKind: JsonValueKind.String } value ? value.GetString() ?? string.Empty : string.Empty;
}
