namespace CrystalCast.Rendering;

internal sealed class RenderOutputPolicy
{
    public static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(2);
    private (ScreenOutputMode Mode, bool WineSceneCompositeEnabled)? previousRequest;
    private bool callbackFailed;
    public string? FallbackReason { get; private set; }

    public static ScreenOutputMode GetDefault(bool isWindows, bool isWine)
        => isWindows || isWine ? ScreenOutputMode.SceneComposite : ScreenOutputMode.NativeOverlay;

    public ScreenOutputMode Resolve(ScreenOutputMode requested, bool isWine, bool hookUnavailable, bool stalled,
        bool enableWineSceneComposite = false)
    {
        var request = (requested, isWine && enableWineSceneComposite);
        if (previousRequest != request)
            Reset();
        previousRequest = request;
        if (requested != ScreenOutputMode.SceneComposite)
            return requested;

        FallbackReason ??= isWine && !enableWineSceneComposite ? "experimental Wine scene compositing is not enabled"
            : hookUnavailable ? "scene composite hook unavailable"
            : stalled ? "scene composite timed out" : null;
        return FallbackReason == null ? requested : ScreenOutputMode.NativeOverlay;
    }

    public void ReportSceneCompositeFailure()
    {
        if (previousRequest?.Mode != ScreenOutputMode.SceneComposite || FallbackReason != null)
            return;
        callbackFailed = true;
        FallbackReason = "scene composite callback failed";
    }

    public void ResetAfterGraphicsInitialization()
    {
        // Recover resources without repeatedly entering a failing experimental callback.
        // Changing the output layer or Wine opt-in explicitly allows another attempt.
        if (!callbackFailed)
            Reset();
    }

    public void Reset()
    {
        previousRequest = null;
        FallbackReason = null;
        callbackFailed = false;
    }
}
