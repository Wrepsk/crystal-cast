using CrystalRenderer.D3D11;
using CrystalRenderer.Integration;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Interface.Utility;
using Dalamud.Plugin;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit;
using KamiToolKit.Overlay.UiOverlay;

namespace CrystalRenderer;

/// <summary>Instance-owned FFXIV screen renderer. Use one active backend per plugin.</summary>
public sealed class DalamudScreenRenderer : IScreenRenderBackend
{
    private readonly object overlayGate = new();
    private readonly RendererServices services;
    private readonly ScreenRendererOptions options;
    private readonly ScreenRenderPipeline pipeline;
    private OverlayController? overlayController;
    private ScreenOverlayNode? overlay;
    private ScreenRenderFrame? activeFrame;
    private bool kamiInitialized;
    private bool disposed;

    public DalamudScreenRenderer(IDalamudPluginInterface pluginInterface, ScreenRendererOptions? options = null)
    {
        this.options = options ?? new ScreenRendererOptions();
        this.options.Validate();
        services = pluginInterface.Create<RendererServices>()
            ?? throw new InvalidOperationException("Could not resolve CrystalRenderer's Dalamud services.");
        try
        {
            pipeline = new ScreenRenderPipeline(services.Log, services.HookProvider, this.options.MaxSurfaces);
            KamiToolKitLibrary.Initialize(pluginInterface, "CrystalRendererOverlay");
            kamiInitialized = true;
            services.Framework.RunOnFrameworkThread(() =>
            {
                lock (overlayGate)
                {
                    if (disposed) return;
                    overlayController = new OverlayController();
                    overlay = new ScreenOverlayNode();
                    overlayController.AddNode(overlay);
                }
            });
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public string SceneCompositeStatus => pipeline.Status;
    public bool IsSceneCompositeHookUnavailable => pipeline.HookUnavailable;
    public bool IsSceneCompositePending => pipeline.Pending;
    public bool IsNativeOverlayAvailable { get { lock (overlayGate) return overlay != null; } }
    public bool IsSceneCompositeStalled(TimeSpan timeout) => pipeline.IsStalled(timeout);
    public void ThrowIfRenderFailed() => pipeline.Failures.ThrowIfFailed();
    public void CancelSceneComposite(string reason) => pipeline.Cancel(reason);

    public IScreenRenderFrame? BeginFrame(RenderOutput output)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!Enum.IsDefined(output)) throw new ArgumentOutOfRangeException(nameof(output));
        if (activeFrame != null) throw new InvalidOperationException("Complete the active render frame before beginning another.");
        if (output != RenderOutput.SceneComposite)
            pipeline.Cancel("output layer changed");
        if (output == RenderOutput.SceneComposite && pipeline.Pending) return null;
        if (IsSceneHidden())
        {
            pipeline.Cancel("scene hidden by cutscene or loading fade");
            HideNativeOverlay();
            return null;
        }
        // The fallback must be selected before rendering so the ImGui path receives its UI mask.
        if (output == RenderOutput.NativeOverlay && !IsNativeOverlayAvailable)
            output = RenderOutput.ImGuiOverlay;
        var selected = output;
        activeFrame = new ScreenRenderFrame(options.MaxSurfaces, pipeline.Submit, cancelled =>
        {
            try
            {
                if (cancelled)
                {
                    pipeline.Discard();
                    return;
                }
                if (selected == RenderOutput.SceneComposite)
                {
                    HideNativeOverlay();
                    pipeline.Schedule();
                    return;
                }
                var target = pipeline.Render(selected);
                if (target == null) { HideNativeOverlay(); return; }
                if (selected == RenderOutput.NativeOverlay)
                {
                    lock (overlayGate)
                    {
                        if (overlay == null) return;
                        overlay.UpdateTexture(target.ProcessedTexture, target.ProcessedSRV);
                        overlay.IsVisible = true;
                    }
                }
                else
                {
                    HideNativeOverlay();
                    var viewport = ImGuiHelpers.MainViewport;
                    ImGui.GetBackgroundDrawList(viewport).AddImage((ulong)target.ImguiHandle,
                        viewport.Pos, viewport.Pos + new System.Numerics.Vector2(target.Width, target.Height));
                }
            }
            finally
            {
                activeFrame = null;
            }
        });
        return activeFrame;
    }

    public void HideNativeOverlay()
    {
        lock (overlayGate)
            if (overlay != null) overlay.IsVisible = false;
    }

    private unsafe bool IsSceneHidden()
    {
        if (services.Condition[ConditionFlag.OccupiedInCutSceneEvent] || services.Condition[ConditionFlag.WatchingCutscene78])
            return true;
        var middle = (AtkUnitBase*)services.GameGui.GetAddonByName("FadeMiddle").Address;
        var black = (AtkUnitBase*)services.GameGui.GetAddonByName("FadeBlack").Address;
        return (middle != null && middle->IsVisible) || (black != null && black->IsVisible);
    }

    public void Dispose()
    {
        lock (overlayGate)
        {
            if (disposed) return;
            disposed = true;
            activeFrame?.Cancel();
            overlayController?.Dispose();
            overlayController = null;
            overlay = null;
            if (kamiInitialized) { KamiToolKitLibrary.Dispose(); kamiInitialized = false; }
        }
        pipeline?.Dispose();
    }
}
