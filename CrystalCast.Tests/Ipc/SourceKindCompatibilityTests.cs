using CrystalCast.Sync;

namespace CrystalCast.Tests;

public sealed class SourceKindCompatibilityTests
{
    [Fact]
    public void BrowserRetainsSerializedNumericValue()
    {
        Assert.Equal(3, (int)ScreenSourceKind.Browser);
    }

    [Theory]
    [InlineData("Browser")]
    [InlineData("YouTubeBrowser")]
    public void BrowserAndLegacyBrowserNamesDeserializeAsBrowser(string name)
    {
        var source = IpcJsonService.Deserialize<ScreenSourceState>($"{{\"kind\":\"{name}\"}}");

        Assert.NotNull(source);
        Assert.Equal(ScreenSourceKind.Browser, source.Kind);
    }

    [Fact]
    public void BrowserSerializesWithNeutralName()
    {
        var json = IpcJsonService.Serialize(new ScreenSourceState { Kind = ScreenSourceKind.Browser });

        Assert.Contains("\"kind\":\"Browser\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("YouTubeBrowser", json, StringComparison.Ordinal);
    }

    [Fact]
    public void LegacyLocalVideoNameCanBeReadForConfigurationMigration()
    {
        var source = IpcJsonService.Deserialize<ScreenSourceState>("{\"kind\":\"LocalVideo\"}");

        Assert.NotNull(source);
        Assert.Equal(2, (int)source.Kind);
    }
}
