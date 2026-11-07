using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using CrystalCast.Video;

namespace CrystalCast.Tests;

public sealed class WebKitGtkBackendTests
{
    [Theory]
    [InlineData(BrowserSourceProviderKind.YouTube, true)]
    [InlineData(BrowserSourceProviderKind.Twitch, true)]
    [InlineData(BrowserSourceProviderKind.Dailymotion, true)]
    [InlineData(BrowserSourceProviderKind.Vimeo, true)]
    [InlineData(BrowserSourceProviderKind.GenericWeb, false)]
    public void WebKitGtkIsUsedOnlyForProviderPlayersUnderWine(BrowserSourceProviderKind provider, bool expected)
    {
        Assert.Equal(expected, BrowserPlatformPolicy.UsesWebKitGtk(BrowserMediaEngine.WebKitGtk, isWine: true, provider));
        Assert.False(BrowserPlatformPolicy.UsesWebKitGtk(BrowserMediaEngine.WebKitGtk, isWine: false, provider));
        Assert.False(BrowserPlatformPolicy.UsesWebKitGtk(BrowserMediaEngine.Auto, isWine: true, provider));
    }

    [Fact]
    public void WebKitGtkSelectionSurvivesNormalizationAndFallsBackToWebView2Capture()
    {
        Assert.Equal(BrowserMediaEngine.WebKitGtk, BrowserMediaEnginePolicy.Normalize(BrowserMediaEngine.WebKitGtk));
        Assert.Equal(WebView2CaptureMode.PreviewJpeg, BrowserPlatformPolicy.ResolveCaptureMode(BrowserMediaEngine.WebKitGtk, isWine: true));
        Assert.Equal(WebView2CaptureMode.WindowGraphicsCapture, BrowserPlatformPolicy.ResolveCaptureMode(BrowserMediaEngine.WebKitGtk, isWine: false));
    }

    [Fact]
    public void ConfigurationKeepsWebKitGtkSelection()
    {
        var configuration = new Configuration { YouTubeBrowserEngine = BrowserMediaEngine.WebKitGtk };

        configuration.Normalize();

        Assert.Equal(BrowserMediaEngine.WebKitGtk, configuration.YouTubeBrowserEngine);
    }

    [Fact]
    public void NativeCommandLineQuotesPathsWithSpaces()
    {
        var commandLine = WineHostInterop.BuildCommandLine(
            "sh",
            ["/home/user/My Plugins/launch.sh", "/opt/helper", "--width", "1280", "C:\\with space\\"]);

        Assert.Equal("sh \"/home/user/My Plugins/launch.sh\" /opt/helper --width 1280 \"C:\\with space\\\\\"", commandLine);
    }

    [Fact]
    public void NativeCommandLineRejectsEmbeddedQuotes()
    {
        Assert.Throws<ArgumentException>(() => WineHostInterop.BuildCommandLine("sh", ["bad\"path"]));
    }

    [Fact]
    public void EnvironmentBlockAddsSessionVariablesAndTerminates()
    {
        var block = WineHostInterop.BuildEnvironmentBlock(new Dictionary<string, string>
        {
            ["CRYSTALCAST_HOST_PORT"] = "41234",
        });

        Assert.Contains("CRYSTALCAST_HOST_PORT=41234\0", block, StringComparison.Ordinal);
        Assert.EndsWith("\0\0", block, StringComparison.Ordinal);
    }

    [Fact]
    public void HostMessagesUseLittleEndianLengthPrefix()
    {
        var framed = WebKitGtkHostSession.Frame(new { type = "ack", seq = 7 });

        var length = BinaryPrimitives.ReadUInt32LittleEndian(framed);
        Assert.Equal(framed.Length - 4, (int)length);
        using var document = JsonDocument.Parse(framed.AsMemory(4));
        Assert.Equal("ack", document.RootElement.GetProperty("type").GetString());
        Assert.Equal(7, document.RootElement.GetProperty("seq").GetInt32());
    }

    [Fact]
    public void PlayerStateReportsReadyOnlyWithMatchingNonce()
    {
        var descriptor = BrowserSourceDescriptors.YouTube;
        Assert.True(descriptor.TryParse("dQw4w9WgXcQ", out var source));
        var state = new BrowserPlayerState(descriptor, source, 1.0f);

        using var forged = JsonDocument.Parse("""{"type":"ready","nonce":"wrong"}""");
        using var ready = JsonDocument.Parse("""{"type":"ready","nonce":"right"}""");

        Assert.Equal(BrowserPlayerMessageResult.Ignored, state.HandleMessage(forged.RootElement, "right", 1.0f));
        Assert.Equal(BrowserPlayerMessageResult.Ready, state.HandleMessage(ready.RootElement, "right", 1.0f));
        Assert.StartsWith("player ready", state.PlayerStatus, StringComparison.Ordinal);
    }

    [Fact]
    public void PlayerStateTracksStatusTelemetry()
    {
        var descriptor = BrowserSourceDescriptors.YouTube;
        Assert.True(descriptor.TryParse("dQw4w9WgXcQ", out var source));
        var state = new BrowserPlayerState(descriptor, source, 1.0f);
        using var status = JsonDocument.Parse(
            """{"type":"status","nonce":"n","state":1,"positionSeconds":12.5,"durationSeconds":60,"rate":1.5,"title":"Clip"}""");

        Assert.Equal(BrowserPlayerMessageResult.Handled, state.HandleMessage(status.RootElement, "n", 1.0f));

        var telemetry = state.Telemetry;
        Assert.Equal(ScreenPlaybackState.Playing, telemetry.State);
        Assert.Equal(12_500, telemetry.PositionMs);
        Assert.Equal(60_000, telemetry.DurationMs);
        Assert.Equal(1.5f, telemetry.Rate);
        Assert.Equal("Clip", telemetry.Title);

        state.SetState(ScreenPlaybackState.Paused, 1.0f);
        Assert.Equal(ScreenPlaybackState.Paused, state.Telemetry.State);
        Assert.Equal(12_500, state.Telemetry.PositionMs);
    }

    [Fact]
    public void NativeHelperIsUnavailableOutsideWine()
    {
        Assert.False(WebKitGtkAvailability.TryGetHelper(out _, out _, out var reason));
        Assert.Contains("Wine", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void PlayerPageCommandBridgeUsesWebViewShimContract()
    {
        // The native helper installs window.chrome.webview with postMessage/addEventListener.
        var descriptor = BrowserSourceDescriptors.YouTube;
        Assert.True(descriptor.TryParse("dQw4w9WgXcQ", out var source));
        var page = BrowserPlayerPageResource.Create(descriptor, source, new BrowserPlaybackSettings(true, false, true, false, 0.7f, 1.0f));

        Assert.Contains("window.chrome.webview.postMessage", page.Html, StringComparison.Ordinal);
        Assert.Contains("window.chrome.webview.addEventListener", page.Html, StringComparison.Ordinal);
        Assert.StartsWith("https://", page.Url, StringComparison.Ordinal);
        Assert.True(Encoding.UTF8.GetByteCount(page.Html) < 1024 * 1024);
    }
}
