using System.Reflection;

namespace CrystalCast.Video;

internal static class BrowserAssetLoader
{
    public static string LoadText(string resourceName)
    {
        using var stream = typeof(BrowserAssetLoader).Assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded browser asset '{resourceName}' was not found.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
