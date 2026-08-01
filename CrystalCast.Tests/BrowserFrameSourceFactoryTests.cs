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

        BrowserSourceProviderRegistry.CreateFrameSource(screen, BrowserMediaEngine.WebView2Capture, factory);

        var request = Assert.IsType<BrowserFrameSourceRequest>(factory.Request);
        Assert.Equal("dQw4w9WgXcQ", request.Input);
        Assert.Equal(1920, request.Width);
        Assert.Equal(1080, request.Height);
        Assert.Equal(30, request.CaptureFps);
        Assert.Equal(BrowserMediaEngine.WebView2Capture, request.EnginePreference);
        Assert.False(request.Autoplay);
        Assert.True(request.Loop);
        Assert.False(request.PlaylistAutoplayNext);
        Assert.True(request.AudioEnabled);
        Assert.Equal(0.25f, request.Volume);
        Assert.Equal(1.5f, request.PlaybackRate);
    }

    [Theory]
    [InlineData(BrowserMediaEngine.Auto)]
    [InlineData(BrowserMediaEngine.WebView2WindowCapture)]
    [InlineData(BrowserMediaEngine.WebView2Capture)]
    public void GenericWebPassesEnginePreferenceToFactory(BrowserMediaEngine engine)
    {
        var screen = new BrowserScreenProfile { ProviderKind = BrowserSourceProviderKind.GenericWeb };
        var factory = new RecordingBrowserFrameSourceFactory();

        BrowserSourceProviderRegistry.CreateFrameSource(screen, engine, factory);

        Assert.NotNull(factory.Request);
        Assert.Equal(engine, factory.Request.EnginePreference);
    }

    [Fact]
    public void CaptureFpsChangesAreAppliedToAnExistingSource()
    {
        var screen = new BrowserScreenProfile
        {
            ProviderKind = BrowserSourceProviderKind.YouTube,
            YouTubeCaptureFpsManual = true,
            YouTubeCaptureFps = 42.0f,
        };
        var factory = new RecordingBrowserFrameSourceFactory();
        var source = BrowserSourceProviderRegistry.CreateFrameSource(screen, BrowserMediaEngine.Auto, factory);

        BrowserSourceProviderRegistry.ApplyCaptureFps(source, screen);

        Assert.Equal(42.0f, factory.Source.LastUpdatedCaptureFps);
    }

    [Fact]
    public void RegistryDefinesEverySupportedProviderOnce()
    {
        var expected = Enum.GetValues<BrowserSourceProviderKind>();
        var options = BrowserSourceProviderRegistry.Options;

        Assert.Equal(expected.Length, options.Count);
        Assert.Equal(expected.Order(), options.Select(option => option.Kind).Order());
        Assert.Equal(options.Count, options.Select(option => option.Kind).Distinct().Count());
    }

    [Fact]
    public void RegistryNormalizationUsesTheRuntimeFpsRange()
    {
        var screen = new BrowserScreenProfile
        {
            ProviderKind = BrowserSourceProviderKind.YouTube,
            YouTubeBrowserWidth = 10,
            YouTubeBrowserHeight = 10_000,
            YouTubeCaptureFps = 240.0f,
            YouTubeVolume = -1.0f,
            YouTubePlaybackRate = 8.0f,
        };

        Assert.True(BrowserSourceProviderRegistry.NormalizeProviderSettings(screen));
        Assert.Equal(320, screen.YouTubeBrowserWidth);
        Assert.Equal(2160, screen.YouTubeBrowserHeight);
        Assert.Equal(120.0f, screen.YouTubeCaptureFps);
        Assert.Equal(0.0f, screen.YouTubeVolume);
        Assert.Equal(2.0f, screen.YouTubePlaybackRate);
    }

    private sealed class RecordingBrowserFrameSourceFactory : IBrowserFrameSourceFactory
    {
        public BrowserFrameSourceRequest? Request { get; private set; }
        public StubVideoFrameSource Source { get; } = new();

        public IVideoFrameSource Create(BrowserFrameSourceRequest request)
        {
            Request = request;
            Source.ProviderKind = request.ProviderKind;
            return Source;
        }
    }

    private sealed class StubVideoFrameSource : IVideoFrameSource, IBrowserFrameSourceRuntime
    {
        public BrowserSourceProviderKind ProviderKind { get; set; }
        public float DetectedVideoFps => 0.0f;
        public float LastUpdatedCaptureFps { get; private set; }
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
        public void UpdateCaptureFps(float fps) => LastUpdatedCaptureFps = fps;
        public void Dispose() { }
    }
}
