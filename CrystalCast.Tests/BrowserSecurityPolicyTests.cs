using System.Text.Json;
using CrystalCast.Video;

namespace CrystalCast.Tests;

public sealed class BrowserSecurityPolicyTests
{
    [Theory]
    [InlineData("https://crystalcast.local/player/a.html", "https://crystalcast.local/player/a.html", true)]
    [InlineData("https://crystalcast.local/player/b.html", "https://crystalcast.local/player/a.html", false)]
    [InlineData("https://example.com/player/a.html", "https://crystalcast.local/player/a.html", false)]
    [InlineData("javascript:alert(1)", "https://crystalcast.local/player/a.html", false)]
    public void ProviderNavigationAllowsOnlyItsInMemoryDocument(string candidate, string expected, bool allowed)
    {
        Assert.Equal(allowed, BrowserNavigationPolicy.IsAllowedProviderDocument(candidate, expected));
    }

    [Theory]
    [InlineData("https://example.com/page", true)]
    [InlineData("http://localhost:8080/page", true)]
    [InlineData("https://user:password@example.com/page", false)]
    [InlineData("file:///C:/page.html", false)]
    [InlineData("data:text/html,test", false)]
    [InlineData("javascript:alert(1)", false)]
    public void GenericNavigationAllowsOnlyCredentialFreeHttpAndHttps(string candidate, bool allowed)
    {
        Assert.Equal(allowed, BrowserNavigationPolicy.IsAllowedGenericDocument(candidate));
    }

    [Fact]
    public void AuthenticatedTelemetryAcceptsExpectedNonce()
    {
        const string json = "{\"type\":\"status\",\"nonce\":\"expected\",\"title\":\"ok\"}";

        Assert.True(BrowserMessageValidator.TryParseAuthenticated(json, "expected", out var document, out _));
        using (document)
            Assert.Equal("status", document!.RootElement.GetProperty("type").GetString());
    }

    [Fact]
    public void AuthenticatedTelemetryRejectsWrongNonceAndOversizedPayloads()
    {
        Assert.False(BrowserMessageValidator.TryParseAuthenticated(
            "{\"type\":\"status\",\"nonce\":\"wrong\"}",
            "expected",
            out _,
            out _));

        var oversized = JsonSerializer.Serialize(new
        {
            type = "status",
            nonce = "expected",
            title = new string('x', BrowserMessageValidator.MaximumMessageBytes),
        });
        Assert.False(BrowserMessageValidator.TryParseAuthenticated(oversized, "expected", out _, out _));
    }

    [Fact]
    public void AuthenticatedTelemetryRejectsExcessiveJsonDepth()
    {
        var json = "{\"type\":\"status\",\"nonce\":\"expected\",\"value\":" + new string('[', 20) + "0" + new string(']', 20) + "}";

        Assert.False(BrowserMessageValidator.TryParseAuthenticated(json, "expected", out _, out _));
    }

    [Fact]
    public void ProviderProfilesAreIsolatedAndRemainInsideCrystalCastStorage()
    {
        var folders = Enum.GetValues<BrowserSourceProviderKind>()
            .Select(BrowserProfileManager.GetWebView2UserDataFolder)
            .ToArray();

        Assert.Equal(folders.Length, folders.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(folders, folder => Assert.StartsWith(
            Path.GetFullPath(BrowserProfileManager.ProfileRoot),
            Path.GetFullPath(folder),
            StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData(double.NaN, 0L)]
    [InlineData(double.PositiveInfinity, 0L)]
    [InlineData(-1.0, 0L)]
    [InlineData(1.25, 1250L)]
    [InlineData(999999999.0, 604800000L)]
    public void TelemetryTimesAreFiniteAndBounded(double seconds, long expectedMilliseconds)
    {
        Assert.Equal(expectedMilliseconds, BrowserMessageValidator.ToBoundedMilliseconds(seconds));
    }

    [Fact]
    public void GenericWebWarningNamesItsTrustBoundary()
    {
        Assert.Contains("track you", BrowserNavigationPolicy.GenericWebTrustWarning, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("only load sites you trust", BrowserNavigationPolicy.GenericWebTrustWarning, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CefPolicyProxyBlocksUnexpectedMainFrameNavigationButAllowsSubframes()
    {
        var proxy = (ITestCefRequestHandler)CefBrowserPolicyProxy.Create(
            typeof(ITestCefRequestHandler),
            candidate => BrowserNavigationPolicy.IsAllowedProviderDocument(
                candidate,
                "https://crystalcast.local/player.html"));

        Assert.False(proxy.OnBeforeBrowse(null, null, new TestCefFrame(true), new TestCefRequest("https://crystalcast.local/player.html"), false, false));
        Assert.True(proxy.OnBeforeBrowse(null, null, new TestCefFrame(true), new TestCefRequest("https://evil.example/"), false, false));
        Assert.False(proxy.OnBeforeBrowse(null, null, new TestCefFrame(false), new TestCefRequest("https://evil.example/"), false, false));
        Assert.True(proxy.OnOpenUrlFromTab(null, null, null, "https://evil.example/", null, false));
    }

    [Theory]
    [InlineData("Autoplay", true)]
    [InlineData("Camera", false)]
    [InlineData("Microphone", false)]
    [InlineData("Geolocation", false)]
    [InlineData("FileReadWrite", false)]
    [InlineData("MultipleAutomaticDownloads", false)]
    [InlineData(null, false)]
    public void BrowserPermissionsAllowPlaybackWithoutGrantingSensitiveCapabilities(string? permissionKind, bool allowed)
    {
        Assert.Equal(allowed, BrowserPermissionPolicy.IsAllowed(permissionKind));
    }

    [Fact]
    public void EveryWebView2ProviderAllowsHostInitiatedPlaybackWithoutStoredUserEngagement()
    {
        Assert.All(
            BrowserSourceDescriptors.All,
            descriptor => Assert.Contains(
                "--autoplay-policy=no-user-gesture-required",
                descriptor.WebView2AdditionalBrowserArguments,
                StringComparison.Ordinal));
    }

    [Fact]
    public void PlaybackIntentStartsPausedWhenAutoplayIsDisabled()
    {
        var intent = new BrowserPlaybackIntent(autoplay: false);

        Assert.False(intent.IsPlayRequested);
        intent.RequestPlay();
        Assert.True(intent.IsPlayRequested);
        intent.RequestPause();
        Assert.False(intent.IsPlayRequested);
    }

    [Fact]
    public void PlaybackIntentStartsPlayingWhenAutoplayIsEnabled()
    {
        Assert.True(new BrowserPlaybackIntent(autoplay: true).IsPlayRequested);
    }

    public interface ITestCefRequestHandler
    {
        bool OnBeforeBrowse(object? webBrowser, object? browser, TestCefFrame frame, TestCefRequest request, bool userGesture, bool redirect);
        bool OnOpenUrlFromTab(object? webBrowser, object? browser, object? frame, string targetUrl, object? disposition, bool userGesture);
    }

    public sealed record TestCefFrame(bool IsMain);
    public sealed record TestCefRequest(string Url);
}
