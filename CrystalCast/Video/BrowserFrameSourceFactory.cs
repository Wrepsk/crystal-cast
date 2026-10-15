namespace CrystalCast.Video;

using Dalamud.Plugin.Services;

internal sealed record BrowserFrameSourceRequest(
    BrowserSourceProviderKind ProviderKind,
    BrowserSourceDescriptor? Descriptor,
    string Input,
    int Width,
    int Height,
    float CaptureFps,
    BrowserMediaEngine EnginePreference,
    bool Autoplay,
    bool Loop,
    bool PlaylistAutoplayNext,
    bool AudioEnabled,
    float Volume,
    float PlaybackRate)
{
    public Func<string, bool>? IsNavigationAllowed { get; init; }
}

internal interface IBrowserFrameSourceFactory
{
    IVideoFrameSource Create(BrowserFrameSourceRequest request);
}

internal sealed class BrowserFrameSourceFactory : IBrowserFrameSourceFactory
{
    private readonly IPluginLog log;
    private readonly Func<bool> nativeLinuxBrowserEnabled;

    public BrowserFrameSourceFactory(IPluginLog log, Func<bool>? nativeLinuxBrowserEnabled = null)
    {
        this.log = log;
        this.nativeLinuxBrowserEnabled = nativeLinuxBrowserEnabled ?? (() => false);
    }

    public IVideoFrameSource Create(BrowserFrameSourceRequest request)
    {
        if (BrowserPlatformPolicy.UseNativeLinuxBrowser(WineEnvironment.IsWine, nativeLinuxBrowserEnabled()))
            return new LinuxBrowserFrameSource(request);

        if (request.ProviderKind == BrowserSourceProviderKind.GenericWeb)
        {
            return new GenericWebBrowserFrameSource(
                request.Input,
                request.Width,
                request.Height,
                request.CaptureFps,
                request.Autoplay,
                request.Loop,
                request.AudioEnabled,
                request.Volume,
                request.PlaybackRate,
                BrowserPlatformPolicy.ResolveCaptureMode(request.EnginePreference, WineEnvironment.IsWine),
                log,
                request.IsNavigationAllowed);
        }

        if (request.Descriptor == null)
            throw new ArgumentException("A browser source descriptor is required.", nameof(request));

        return new BrowserFrameSource(
            request.Descriptor,
            request.Input,
            request.Width,
            request.Height,
            request.CaptureFps,
            request.EnginePreference,
            request.Autoplay,
            request.Loop,
            request.PlaylistAutoplayNext,
            request.AudioEnabled,
            request.Volume,
            request.PlaybackRate,
            log);
    }
}
