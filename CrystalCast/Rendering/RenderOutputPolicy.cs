namespace CrystalCast.Rendering;

/// <summary>
/// Picks the output layer. Scene composite is the default on every platform, Wine included; it falls back to
/// the native overlay when its hook is missing, a frame stalls, or a callback fails, and stays there until the
/// user switches layers or graphics reinitialize.
/// </summary>
internal sealed class RenderOutputPolicy
{
    public static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(2);
    private ScreenOutputMode? previousRequest;
    private bool callbackFailed;
    public string? FallbackReason { get; private set; }

    public static ScreenOutputMode GetDefault() => ScreenOutputMode.SceneComposite;

    public ScreenOutputMode Resolve(ScreenOutputMode requested, bool hookUnavailable, bool stalled)
    {
        if (previousRequest != requested)
            Reset();
        previousRequest = requested;
        if (requested != ScreenOutputMode.SceneComposite)
            return requested;

        FallbackReason ??= hookUnavailable ? "scene composite hook unavailable"
            : stalled ? "scene composite timed out" : null;
        return FallbackReason == null ? requested : ScreenOutputMode.NativeOverlay;
    }

    public void ReportSceneCompositeFailure()
    {
        if (previousRequest != ScreenOutputMode.SceneComposite || FallbackReason != null)
            return;
        callbackFailed = true;
        FallbackReason = "scene composite callback failed";
    }

    public void ResetAfterGraphicsInitialization()
    {
        // Recover resources without repeatedly entering a failing callback.
        // Changing the output layer explicitly allows another attempt.
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
