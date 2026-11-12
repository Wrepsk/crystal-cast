using CrystalCast.Sources;
using CrystalCast.Video;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Dalamud.Plugin.Services;

namespace CrystalCast.Browser.WebView2;

/// <summary>
/// Plays a video site through the player page CrystalCast generates for it. The page is served from a
/// virtual host, cannot navigate away, and reports the player's state in messages.
/// </summary>
internal sealed class WebView2BrowserFrameSource : WebView2FrameSourceBase
{
    private readonly BrowserSourceDescriptor descriptor;
    private readonly BrowserPlayerPageResource? playerPage;
    private readonly BrowserPlayerState playerState;
    private CoreWebView2Environment? pageEnvironment;
    private bool playlistAutoplayNext;

    public WebView2BrowserFrameSource(
        BrowserSourceDescriptor descriptor,
        string input,
        int width,
        int height,
        float captureFps,
        bool autoplay,
        bool loop,
        bool playlistAutoplayNext,
        bool audioEnabled,
        float volume,
        float playbackRate,
        WebView2CaptureMode captureMode,
        IPluginLog log,
        JpegCaptureResolution jpegResolution = JpegCaptureResolution.Full1080p)
        : base(
            descriptor,
            input,
            descriptor.TryParse(input, out var source),
            width,
            height,
            captureFps,
            autoplay,
            loop,
            audioEnabled,
            volume,
            playbackRate,
            captureMode,
            log,
            jpegResolution)
    {
        this.descriptor = descriptor;
        this.playlistAutoplayNext = playlistAutoplayNext;

        if (IsValidSource)
        {
            playerPage = BrowserPlayerPageResource.Create(
                descriptor,
                source,
                new BrowserPlaybackSettings(autoplay, loop, playlistAutoplayNext, audioEnabled, Volume, PlaybackRate));
        }

        playerState = new BrowserPlayerState(descriptor, source, PlaybackRate);
    }

    public override float DetectedVideoFps => playerState.DetectedVideoFps;

    protected override string PlayerStatus
    {
        get => playerState.PlayerStatus;
        set => playerState.PlayerStatus = value;
    }

    protected override string MessageNonce => playerPage?.Nonce ?? string.Empty;

    protected override string StartUrl =>
        playerPage?.Url ?? throw new InvalidOperationException("Player page was not created.");

    protected override string LoadingDescription => $"{descriptor.DisplayName} player";

    protected override string CaptureLogName => "WebView2";

    protected override MediaPlaybackTelemetry CurrentTelemetry => playerState.Telemetry;

    public override void ApplyPlaybackSettings(bool audioEnabled, float volume, float playbackRate, bool loop, bool playlistAutoplayNext)
    {
        volume = BrowserPlayerState.QuantizeVolume(volume);
        playbackRate = BrowserPlayerState.ClampPlaybackRate(playbackRate);

        if (MatchesPlaybackSettings(audioEnabled, volume, playbackRate, loop)
            && this.playlistAutoplayNext == playlistAutoplayNext)
        {
            return;
        }

        StorePlaybackSettings(audioEnabled, volume, playbackRate, loop);
        this.playlistAutoplayNext = playlistAutoplayNext;
        SendPlaybackSettings(playlistAutoplayNext);
    }

    internal static bool TryGetWebView2Runtime(out string runtimeVersion, out string error)
    {
        return TryGetWebView2Runtime(out runtimeVersion, out error, out _);
    }

    /// <summary>
    /// Also returns the downloaded runtime's folder, or null for the installed runtime, to pass to
    /// CoreWebView2Environment.CreateAsync.
    /// </summary>
    internal static bool TryGetWebView2Runtime(out string runtimeVersion, out string error, out string? browserExecutableFolder)
    {
        var found = WebView2FixedRuntime.TryFind(out var runtime, out error);
        runtimeVersion = found ? runtime.Describe() : string.Empty;
        browserExecutableFolder = found ? runtime.BrowserExecutableFolder : null;
        return found;
    }

    protected override void SetPlaybackState(ScreenPlaybackState state)
    {
        playerState.SetState(state, PlaybackRate);
    }

    protected override void HandleWebMessage(JsonElement root)
    {
        if (playerPage == null
            || playerState.HandleMessage(root, playerPage.Nonce, PlaybackRate) != BrowserPlayerMessageResult.Ready)
            return;

        MarkPlayerReady();
        StartWindowCaptureWhenReady();
        PlayIfRequested();
    }

    protected override bool IsFromExpectedDocument(string messageSource, string? currentDocument)
    {
        return playerPage != null && BrowserNavigationPolicy.IsExpectedMessageSource(messageSource, playerPage.Url);
    }

    protected override void OnNavigationStarting(CoreWebView2NavigationStartingEventArgs args)
    {
        if (playerPage == null || !BrowserNavigationPolicy.IsAllowedProviderDocument(args.Uri, playerPage.Url))
        {
            args.Cancel = true;
            BrowserStatus = "blocked browser navigation outside the provider player";
        }
    }

    protected override void ConfigureController(CoreWebView2Controller controller)
    {
        controller.DefaultBackgroundColor = System.Drawing.Color.Black;
    }

    /// <summary>The generated player page is answered from memory instead of the network.</summary>
    protected override Task PreparePageAsync(CoreWebView2 webView, CoreWebView2Environment environment)
    {
        pageEnvironment = environment;
        webView.WebResourceRequested += OnWebResourceRequested;
        webView.AddWebResourceRequestedFilter(StartUrl, CoreWebView2WebResourceContext.Document);
        return Task.CompletedTask;
    }

    protected override void DetachPage(CoreWebView2 webView)
    {
        webView.WebResourceRequested -= OnWebResourceRequested;
        pageEnvironment = null;
    }

    private void OnWebResourceRequested(object? sender, CoreWebView2WebResourceRequestedEventArgs args)
    {
        var page = playerPage;
        var environment = pageEnvironment;
        if (page == null
            || environment == null
            || !string.Equals(args.Request.Uri, page.Url, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var stream = new MemoryStream(page.Utf8Content.ToArray(), writable: false);
        args.Response = environment.CreateWebResourceResponse(
            stream,
            200,
            "OK",
            "Content-Type: text/html; charset=utf-8\r\nCache-Control: no-store");
    }
}
