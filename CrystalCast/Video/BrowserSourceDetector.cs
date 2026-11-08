namespace CrystalCast.Video;

/// <summary>A link or ID resolved to the provider that will play it.</summary>
internal readonly record struct DetectedBrowserSource(
    BrowserSourceProviderKind ProviderKind,
    string Url,
    IBrowserSourceReference Source)
{
    /// <summary>True when no video site matched and the link opens as a Generic Web page.</summary>
    public bool IsGenericWebFallback => ProviderKind == BrowserSourceProviderKind.GenericWeb;
}

/// <summary>
/// Picks the provider for what the user pastes. Full links are matched by their site; anything that is a
/// valid web address but no known video site becomes a Generic Web page. Bare IDs and channel names are
/// ambiguous across sites (a YouTube video ID is also a valid Twitch channel name), so they stay with the
/// screen's current provider.
/// </summary>
internal static class BrowserSourceDetector
{
    private static readonly BrowserSourceDescriptor[] VideoSites =
    [
        BrowserSourceDescriptors.YouTube,
        BrowserSourceDescriptors.Twitch,
        BrowserSourceDescriptors.Dailymotion,
        BrowserSourceDescriptors.Vimeo,
    ];

    public static bool TryDetect(string? input, BrowserSourceProviderKind currentProvider, out DetectedBrowserSource detected)
    {
        detected = default;
        if (string.IsNullOrWhiteSpace(input))
            return false;

        var trimmed = input.Trim();
        var link = NormalizeLink(trimmed);
        if (link != null)
        {
            foreach (var site in VideoSites)
            {
                if (site.TryParse(link, out var siteSource))
                {
                    detected = new DetectedBrowserSource(site.ProviderKind, link, siteSource);
                    return true;
                }
            }

            if (BrowserSourceDescriptors.GenericWeb.TryParse(link, out var page))
            {
                detected = new DetectedBrowserSource(BrowserSourceProviderKind.GenericWeb, link, page);
                return true;
            }

            return false;
        }

        foreach (var site in VideoSites)
        {
            if (site.ProviderKind == currentProvider && site.TryParse(trimmed, out var idSource))
            {
                detected = new DetectedBrowserSource(site.ProviderKind, trimmed, idSource);
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Returns the input as an http(s) link, adding https:// to scheme-less addresses such as
    /// "youtube.com/watch?v=…", or null when the input is not a link.
    /// </summary>
    internal static string? NormalizeLink(string input)
    {
        if (BrowserUriPolicy.TryCreateHttpUri(input, out _))
            return input;

        if (input.Contains("://", StringComparison.Ordinal) || input.Any(char.IsWhiteSpace))
            return null;

        var hostEnd = input.IndexOfAny(['/', '?', '#']);
        var host = hostEnd < 0 ? input : input[..hostEnd];
        var dot = host.LastIndexOf('.');
        if (dot <= 0 || dot == host.Length - 1)
            return null;

        var withScheme = "https://" + input;
        return BrowserUriPolicy.TryCreateHttpUri(withScheme, out _) ? withScheme : null;
    }
}
