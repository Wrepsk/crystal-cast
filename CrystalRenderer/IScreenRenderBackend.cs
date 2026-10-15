namespace CrystalRenderer;

/// <summary>
/// Owns screen rendering resources and output integration. BeginFrame/Submit/Dispose are
/// called on the host's draw thread. Browser capture, playback, and configuration belong to the host.
/// </summary>
public interface IScreenRenderBackend : IDisposable
{
    string SceneCompositeStatus { get; }
    bool IsSceneCompositeHookUnavailable { get; }
    bool IsSceneCompositePending { get; }
    bool IsNativeOverlayAvailable { get; }
    bool IsSceneCompositeStalled(TimeSpan timeout);
    IScreenRenderFrame? BeginFrame(RenderOutput output);
    void HideNativeOverlay();
    void CancelSceneComposite(string reason);
    void ThrowIfRenderFailed();
}

public interface IScreenRenderFrame : IDisposable
{
    void Submit(ScreenSurface surface);
    void Cancel();
}
