using System.Text;
using System.Text.Json;

namespace CrystalCast.Video;

internal static class BrowserUriPolicy
{
    public const int MaximumUrlLength = 2048;

    public static bool TryCreateHttpUri(string? value, out Uri uri)
    {
        uri = null!;
        if (string.IsNullOrWhiteSpace(value) || value.Length > MaximumUrlLength)
            return false;

        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var parsed)
            || (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps)
            || string.IsNullOrWhiteSpace(parsed.IdnHost)
            || !string.IsNullOrEmpty(parsed.UserInfo))
        {
            return false;
        }

        uri = parsed;
        return true;
    }

    public static bool IsHostOrSubdomain(Uri uri, params string[] providerDomains)
    {
        var host = uri.IdnHost.TrimEnd('.');
        foreach (var domain in providerDomains)
        {
            if (host.Equals(domain, StringComparison.OrdinalIgnoreCase)
                || host.EndsWith('.' + domain, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    public static bool IsExactHost(Uri uri, params string[] hosts)
    {
        var host = uri.IdnHost.TrimEnd('.');
        return hosts.Any(allowed => host.Equals(allowed, StringComparison.OrdinalIgnoreCase));
    }
}

internal static class BrowserNavigationPolicy
{
    public const string GenericWebTrustWarning =
        "Generic Web runs scripts from the page you enter. Pages can track you and control their own playback telemetry; only load sites you trust.";

    public static bool IsAllowedProviderDocument(string? candidate, string expectedDocument)
    {
        return BrowserUriPolicy.TryCreateHttpUri(candidate, out var candidateUri)
            && BrowserUriPolicy.TryCreateHttpUri(expectedDocument, out var expectedUri)
            && Uri.Compare(
                candidateUri,
                expectedUri,
                UriComponents.SchemeAndServer | UriComponents.PathAndQuery,
                UriFormat.SafeUnescaped,
                StringComparison.OrdinalIgnoreCase) == 0;
    }

    public static bool IsAllowedGenericDocument(string? candidate)
    {
        return BrowserUriPolicy.TryCreateHttpUri(candidate, out _);
    }

    public static bool IsExpectedMessageSource(string? actualSource, string? expectedSource)
    {
        return !string.IsNullOrWhiteSpace(expectedSource)
            && IsAllowedProviderDocument(actualSource, expectedSource);
    }
}

internal static class BrowserPermissionPolicy
{
    public static bool IsAllowed(string? permissionKind)
    {
        return string.Equals(permissionKind, "Autoplay", StringComparison.Ordinal);
    }
}

internal static class BrowserMessageValidator
{
    public const int MaximumMessageBytes = 16 * 1024;
    public const int MaximumTextLength = 512;
    public const double MaximumMediaTimeSeconds = 7 * 24 * 60 * 60;
    private const int MaximumMessageCharacters = MaximumMessageBytes;

    public static bool TryParseAuthenticated(
        string? json,
        string expectedNonce,
        out JsonDocument? document,
        out string error)
    {
        document = null;
        if (string.IsNullOrWhiteSpace(json))
        {
            error = "browser message is empty";
            return false;
        }

        if (json.Length > MaximumMessageCharacters || Encoding.UTF8.GetByteCount(json) > MaximumMessageBytes)
        {
            error = "browser message exceeds the size limit";
            return false;
        }

        try
        {
            var parsed = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                MaxDepth = 16,
                CommentHandling = JsonCommentHandling.Disallow,
                AllowTrailingCommas = false,
            });
            var root = parsed.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !BrowserPageMessaging.HasNonce(root, expectedNonce)
                || !root.TryGetProperty("type", out var type)
                || type.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(type.GetString())
                || type.GetString()!.Length > 64)
            {
                parsed.Dispose();
                error = "browser message authentication or shape is invalid";
                return false;
            }

            document = parsed;
            error = string.Empty;
            return true;
        }
        catch (JsonException ex)
        {
            error = $"browser message JSON is invalid: {ex.Message}";
            return false;
        }
    }

    public static string BoundText(string? value, string fallback, int maximumLength = MaximumTextLength)
    {
        if (value == null)
            return fallback;

        return value.Length <= maximumLength ? value : value[..maximumLength];
    }

    public static long ToBoundedMilliseconds(double seconds)
    {
        if (!double.IsFinite(seconds))
            return 0;

        return (long)(Math.Clamp(seconds, 0.0, MaximumMediaTimeSeconds) * 1000.0);
    }
}
