namespace CrystalCast.Video;

/// <summary>Splits a provider link into the parts the provider ID parsers look at.</summary>
internal static class ProviderUrlParsing
{
    /// <summary>The path's segments, unescaped, without empty ones.</summary>
    public static string[] GetPathSegments(Uri uri)
    {
        return uri.AbsolutePath
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(Uri.UnescapeDataString)
            .ToArray();
    }

    /// <summary>The query's values by name, ignoring case; a repeated name keeps its last value.</summary>
    public static Dictionary<string, string> ParseQuery(string query)
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
}
