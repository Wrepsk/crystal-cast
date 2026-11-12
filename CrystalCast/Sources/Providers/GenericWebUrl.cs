namespace CrystalCast.Video;

public static class GenericWebUrl
{
    public static bool TryParseSource(string input, out GenericWebSourceReference source)
    {
        source = default;
        if (string.IsNullOrWhiteSpace(input))
            return false;

        if (!BrowserUriPolicy.TryCreateHttpUri(input, out var uri))
            return false;

        source = new GenericWebSourceReference(uri.AbsoluteUri);
        return true;
    }
}

public readonly record struct GenericWebSourceReference(string Url) : IBrowserSourceReference
{
    public string DisplayName
    {
        get
        {
            if (Uri.TryCreate(Url, UriKind.Absolute, out var uri) && !string.IsNullOrWhiteSpace(uri.Host))
                return $"Generic Web: {uri.Host}";

            return "Generic Web page";
        }
    }

    public string VideoId => string.Empty;
}
