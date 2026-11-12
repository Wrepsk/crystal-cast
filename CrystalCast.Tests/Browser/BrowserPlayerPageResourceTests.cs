using System.Text.Json;
using CrystalCast.Video;

namespace CrystalCast.Tests;

public sealed class BrowserPlayerPageResourceTests
{
    [Fact]
    public void MultipleInstancesReceiveUniqueInMemoryPageUrls()
    {
        var descriptor = BrowserSourceDescriptors.YouTube;
        Assert.True(descriptor.TryParse("https://www.youtube.com/watch?v=dQw4w9WgXcQ", out var source));
        var settings = new BrowserPlaybackSettings(true, false, true, false, 0.7f, 1.0f);
        var urls = new string[64];

        Parallel.For(0, urls.Length, index =>
        {
            urls[index] = BrowserPlayerPageResource.Create(descriptor, source, settings).Url;
        });

        Assert.Equal(urls.Length, urls.Distinct(StringComparer.Ordinal).Count());
        Assert.All(urls, url => Assert.StartsWith("https://crystalcast.local/player/", url, StringComparison.Ordinal));
    }

    [Fact]
    public void InstanceContentKeepsItsOwnSourceConfiguration()
    {
        var descriptor = BrowserSourceDescriptors.YouTube;
        Assert.True(descriptor.TryParse("https://www.youtube.com/watch?v=dQw4w9WgXcQ", out var firstSource));
        Assert.True(descriptor.TryParse("https://www.youtube.com/watch?v=M7lc1UVf-VE", out var secondSource));
        var settings = new BrowserPlaybackSettings(true, false, true, false, 0.7f, 1.0f);

        var first = BrowserPlayerPageResource.Create(descriptor, firstSource, settings);
        var second = BrowserPlayerPageResource.Create(descriptor, secondSource, settings);

        Assert.Contains("dQw4w9WgXcQ", first.Html, StringComparison.Ordinal);
        Assert.DoesNotContain("M7lc1UVf-VE", first.Html, StringComparison.Ordinal);
        Assert.Contains("M7lc1UVf-VE", second.Html, StringComparison.Ordinal);
        Assert.Contains("window.chrome.webview.addEventListener", first.Html, StringComparison.Ordinal);
        Assert.Contains(first.Nonce, first.Html, StringComparison.Ordinal);
        Assert.NotEqual(first.Nonce, second.Nonce);
        Assert.True(first.Utf8Content.Length > 0);
    }

    [Fact]
    public void YouTubePageExposesNativePlayerSettingsInTheInteractionWindow()
    {
        var source = new YouTubeSourceReference(
            YouTubeSourceKind.Video,
            "dQw4w9WgXcQ",
            string.Empty,
            string.Empty);

        var html = YouTubePlayerPage.BuildHtml(source, true, false, true, false, 0.7f, 1.0f);

        Assert.Contains("controls: 1", html, StringComparison.Ordinal);
        Assert.Contains("disablekb: 0", html, StringComparison.Ordinal);
        Assert.Contains("&controls=1&disablekb=0", html, StringComparison.Ordinal);
        Assert.DoesNotContain("controls: 0", html, StringComparison.Ordinal);
        Assert.DoesNotContain("controls=0", html, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("play")]
    [InlineData("pause")]
    [InlineData("restart")]
    public void SimpleCommandsAreSerializedAsWebMessages(string command)
    {
        var json = command switch
        {
            "play" => BrowserPageMessaging.Play("screen-nonce"),
            "pause" => BrowserPageMessaging.Pause("screen-nonce"),
            _ => BrowserPageMessaging.Restart("screen-nonce"),
        };

        using var document = JsonDocument.Parse(json);
        Assert.Equal(command, document.RootElement.GetProperty("type").GetString());
        Assert.Equal("screen-nonce", document.RootElement.GetProperty("nonce").GetString());
    }

    [Fact]
    public void SettingsCommandCarriesPerInstanceValues()
    {
        var json = BrowserPageMessaging.Settings("screen-nonce", true, 0.35f, 1.25f, true, false);
        using var document = JsonDocument.Parse(json);
        var settings = document.RootElement.GetProperty("settings");

        Assert.Equal("settings", document.RootElement.GetProperty("type").GetString());
        Assert.True(BrowserPageMessaging.HasNonce(document.RootElement, "screen-nonce"));
        Assert.False(BrowserPageMessaging.HasNonce(document.RootElement, "another-screen"));
        Assert.True(settings.GetProperty("audioEnabled").GetBoolean());
        Assert.Equal(0.35f, settings.GetProperty("volume").GetSingle());
        Assert.Equal(1.25f, settings.GetProperty("playbackRate").GetSingle());
        Assert.True(settings.GetProperty("loop").GetBoolean());
        Assert.False(settings.GetProperty("playlistAutoplayNext").GetBoolean());
    }

    [Fact]
    public void CommandBridgeIsLoadedFromPackagedAssetAndBindsNonce()
    {
        var bridge = BrowserPageMessaging.BuildCommandBridge("asset-test-nonce");

        Assert.Contains("window.chrome.webview.addEventListener", bridge, StringComparison.Ordinal);
        Assert.Contains("asset-test-nonce", bridge, StringComparison.Ordinal);
        Assert.DoesNotContain("__CRYSTALCAST_NONCE__", bridge, StringComparison.Ordinal);
    }
}
