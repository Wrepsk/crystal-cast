using CrystalCast.Video;

namespace CrystalCast.Tests;

public sealed class BrowserFrameSourceFactoryTests
{
    [Theory]
    [InlineData(BrowserSourceProviderKind.YouTube)]
    [InlineData(BrowserSourceProviderKind.Twitch)]
    [InlineData(BrowserSourceProviderKind.Dailymotion)]
    [InlineData(BrowserSourceProviderKind.Vimeo)]
    [InlineData(BrowserSourceProviderKind.GenericWeb)]
    public void RegistryCanBuildEveryProviderWithoutStartingNativeBrowser(BrowserSourceProviderKind providerKind)
    {
        var screen = new BrowserScreenProfile { ProviderKind = providerKind };
        var factory = new RecordingBrowserFrameSourceFactory();

        var source = BrowserSourceProviderRegistry.CreateFrameSource(screen, BrowserMediaEngine.Auto, factory);

        Assert.Same(factory.Source, source);
        Assert.NotNull(factory.Request);
        Assert.Equal(providerKind, factory.Request.ProviderKind);
    }

    [Fact]
    public void YouTubeSettingsAreMappedIntoFactoryRequest()
    {
        var screen = new BrowserScreenProfile
        {
            ProviderKind = BrowserSourceProviderKind.YouTube,
            YouTubeUrl = "dQw4w9WgXcQ",
            YouTubeBrowserWidth = 1920,
            YouTubeBrowserHeight = 1080,
            YouTubeCaptureFps = 30,
            YouTubeAutoplay = false,
            LoopYouTube = true,
            YouTubePlaylistAutoplayNext = false,
            YouTubeAudioEnabled = true,
            YouTubeVolume = 0.25f,
            YouTubePlaybackRate = 1.5f,
        };
        var factory = new RecordingBrowserFrameSourceFactory();

        BrowserSourceProviderRegistry.CreateFrameSource(screen, BrowserMediaEngine.CefOffScreen, factory);

        var request = Assert.IsType<BrowserFrameSourceRequest>(factory.Request);
        Assert.Equal("dQw4w9WgXcQ", request.Input);
        Assert.Equal(1920, request.Width);
        Assert.Equal(1080, request.Height);
        Assert.Equal(30, request.CaptureFps);
        Assert.Equal(BrowserMediaEngine.CefOffScreen, request.EnginePreference);
        Assert.False(request.Autoplay);
        Assert.True(request.Loop);
        Assert.False(request.PlaylistAutoplayNext);
        Assert.True(request.AudioEnabled);
        Assert.Equal(0.25f, request.Volume);
        Assert.Equal(1.5f, request.PlaybackRate);
    }

    [Theory]
    [InlineData(BrowserMediaEngine.Auto, WebView2CaptureMode.WindowGraphicsCapture)]
    [InlineData(BrowserMediaEngine.WebView2WindowCapture, WebView2CaptureMode.WindowGraphicsCapture)]
    [InlineData(BrowserMediaEngine.WebView2Capture, WebView2CaptureMode.PreviewJpeg)]
    public void GenericWebMapsEngineToCaptureMode(BrowserMediaEngine engine, WebView2CaptureMode expectedMode)
    {
        var screen = new BrowserScreenProfile { ProviderKind = BrowserSourceProviderKind.GenericWeb };
        var factory = new RecordingBrowserFrameSourceFactory();

        BrowserSourceProviderRegistry.CreateFrameSource(screen, engine, factory);

        Assert.NotNull(factory.Request);
        Assert.Equal(expectedMode, factory.Request.GenericWebCaptureMode);
    }

    private sealed class RecordingBrowserFrameSourceFactory : IBrowserFrameSourceFactory
    {
        public BrowserFrameSourceRequest? Request { get; private set; }
        public StubVideoFrameSource Source { get; } = new();

        public IVideoFrameSource Create(BrowserFrameSourceRequest request)
        {
            Request = request;
            return Source;
        }
    }

    private sealed class StubVideoFrameSource : IVideoFrameSource
    {
        public string Name => "stub";
        public int Width => 1;
        public int Height => 1;
        public float FramesPerSecond => 1;
        public bool IsRunning => false;
        public string Status => "stub";

        public void Start() { }
        public void Stop() { }
        public bool TryGetLatestFrame(out VideoFrame frame)
        {
            frame = null!;
            return false;
        }
        public void Dispose() { }
    }
}
