namespace CrystalCast.Rendering;

internal sealed class RenderOutputPolicy
{
    public static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(2);
    private ScreenOutputMode? previousRequest;
    public string? FallbackReason { get; private set; }

    public static ScreenOutputMode GetDefault(bool isWindows, bool isWine)
        => isWindows && !isWine ? ScreenOutputMode.SceneComposite : ScreenOutputMode.NativeOverlay;

    public ScreenOutputMode Resolve(ScreenOutputMode requested, bool isWine, bool hookUnavailable, bool stalled)
    {
        if (previousRequest != requested)
            Reset();
        previousRequest = requested;
        if (requested != ScreenOutputMode.SceneComposite)
            return requested;

        FallbackReason ??= isWine ? "scene compositing is experimental under Wine"
            : hookUnavailable ? "scene composite hook unavailable"
            : stalled ? "scene composite timed out" : null;
        return FallbackReason == null ? requested : ScreenOutputMode.NativeOverlay;
    }

    public void Reset()
    {
        previousRequest = null;
        FallbackReason = null;
    }
}
