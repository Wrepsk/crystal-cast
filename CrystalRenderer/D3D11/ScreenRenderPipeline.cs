using CrystalRenderer.Integration;
using Dalamud.Plugin.Services;
using SharpDX.Direct3D11;

namespace CrystalRenderer.D3D11;

/// <summary>The screen-only D3D11 pipeline; no browser, audio, IPC, or plugin settings.</summary>
internal sealed class ScreenRenderPipeline : IDisposable
{
    private readonly object gate = new();
    private readonly RenderContext context;
    private readonly Func<GameSceneFrame?> captureScene;
    private readonly ScreenImage images;
    private readonly SceneDepth depth;
    private readonly FullScreenPass composite;
    private readonly DepthStencilState shapeState;
    private readonly UIMaskCapture? hook;
    private readonly string? hookError;
    private readonly DeferredFrameTracker frames = new();
    private RenderTarget? target;
    private bool disposed;
    internal RenderFailureMailbox Failures { get; } = new();

    internal ScreenRenderPipeline(IPluginLog log, IGameInteropProvider hookProvider, int capacity)
        : this(new RenderContext(log), capacity,
            () => GameSceneSource.TryCapture(out var scene) ? scene : null)
    {
        try
        {
            hook = new UIMaskCapture(context, hookProvider, FlushSceneComposite,
                () => { lock (gate) return frames.Pending && frames.PendingMilliseconds >= 100; });
        }
        catch (Exception ex)
        {
            hookError = ex.GetBaseException().Message;
            log.Warning(ex, "CrystalRenderer scene hook unavailable; native output remains available.");
        }
    }

    // Injection keeps scene timing, compositing, and cancellation testable without FFXIV.
    internal ScreenRenderPipeline(RenderContext context, int capacity, Func<GameSceneFrame?> captureScene)
    {
        this.context = context;
        this.captureScene = captureScene;
        try
        {
            images = new ScreenImage(context, capacity);
            depth = new SceneDepth(context);
            composite = new FullScreenPass(context);
            var description = DepthStencilStateDescription.Default();
            description.IsDepthEnabled = false;
            shapeState = new DepthStencilState(context.Device, description);
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    internal bool HookUnavailable => hook?.IsHookInstalled != true;
    internal bool Pending { get { lock (gate) return frames.HasWork; } }
    internal bool IsStalled(TimeSpan timeout) { lock (gate) return frames.IsStalled(timeout); }
    internal string Status
    {
        get
        {
            lock (gate)
                return $"scene composite {(frames.HasWork ? $"pending for {frames.PendingMilliseconds} ms" : "not pending")}; "
                    + $"scheduled {frames.ScheduledCount}; flushed {frames.FlushedCount}; "
                    + $"hook {(HookUnavailable ? "unavailable" : "installed")}; calls {hook?.HookCallCount ?? 0}; "
                    + $"backbuffer binds {hook?.BackbufferBindCount ?? 0}; scene-stage binds {hook?.BackbufferDsvBindCount ?? 0}; "
                    + $"relaxed flushes {hook?.DsvlessBackbufferCallbackCount ?? 0}; "
                    + $"hook error {hook?.HookError ?? hookError ?? "none"}";
        }
    }

    internal void Submit(ScreenSurface surface)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (frames.HasWork) throw new InvalidOperationException("The preceding scene frame has not finished.");
            images.Add(surface.TextureHandle, surface.Center, surface.Right, surface.Down,
                surface.UvMin, surface.UvMax, surface.Appearance);
        }
    }

    internal void Schedule()
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            frames.Schedule();
        }
    }

    internal void Cancel(string reason)
    {
        lock (gate)
        {
            if (disposed || !frames.Cancel()) return;
            context.Log?.Debug("CrystalRenderer cancelled scene frame: {Reason}", reason);
            Discard();
        }
    }

    internal void Discard()
    {
        lock (gate)
        {
            if (disposed) return;
            images.Discard();
            context.DiscardFrame();
        }
    }

    internal RenderTarget? Render(RenderOutput output)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (captureScene() is not { } scene)
            {
                Discard();
                return null;
            }
            context.BeginFrame();
            if (target == null || target.Size != scene.ViewportSize)
            {
                target?.Dispose();
                target = new RenderTarget(context, (int)scene.ViewportSize.X, (int)scene.ViewportSize.Y, AlphaBlendMode.Add);
            }
            target.Bind(context);
            depth.Update(scene.DepthTexture, scene.DepthUvScale);
            if (depth.SRV == null)
            {
                Discard();
                target.Bind(context);
            }
            else
            {
                var inverse = scene.ViewProjection;
                inverse.Invert();
                images.UpdateConstants(new ScreenImage.Constants
                {
                    ViewProj = scene.ViewProjection,
                    InvViewProj = inverse,
                    DepthViewProj = scene.DepthViewProjection,
                    DepthCoefficients = scene.DepthCoefficients,
                    RenderTargetSize = new System.Numerics.Vector2(scene.ViewportSize.X, scene.ViewportSize.Y),
                    PixelToUv = new System.Numerics.Vector2(depth.UvScale.X / scene.ViewportSize.X, depth.UvScale.Y / scene.ViewportSize.Y),
                });
                context.Context.ClearDepthStencilView(target.ClipStencilDSV,
                    DepthStencilClearFlags.Depth | DepthStencilClearFlags.Stencil, 0, 0);
                context.Context.OutputMerger.SetTargets(target.ClipStencilDSV, target.BaseRTV);
                context.Context.PixelShader.SetShaderResource(0, depth.SRV);
                images.FlushFlat(scene.CameraPosition, shapeState);
            }

            using var backBuffer = new Texture2D(scene.BackBuffer);
            try
            {
                ShaderResourceView? mask = null;
                var maskUi = output == RenderOutput.ImGuiOverlay;
                if (maskUi && scene.ResolutionScaled)
                {
                    if (hook?.HasSnapshot == true)
                    {
                        hook.BuildMask(backBuffer);
                        mask = hook.MaskSRV;
                    }
                    hook?.BeginFrame();
                }
                composite.UpdateConstants(context, new FullScreenPass.Constants
                {
                    MaxAlpha = 1,
                    UseMask = maskUi && (!scene.ResolutionScaled || mask != null) ? 1 : 0,
                });
                if (output == RenderOutput.SceneComposite)
                    target.ExecuteFSPToBackBuffer(context, backBuffer, composite);
                else
                    target.ExecuteFSP(context, backBuffer, composite, mask);
            }
            finally
            {
                // Borrowed from FFXIV; the command list and RTV hold their own references.
                backBuffer.NativePointer = nint.Zero;
            }
            context.Execute();
            return target;
        }
    }

    internal void FlushSceneComposite()
    {
        lock (gate)
        {
            if (disposed || !frames.TryBeginFlush()) return;
            var success = false;
            try
            {
                success = Render(RenderOutput.SceneComposite) != null;
            }
            catch (Exception ex)
            {
                Failures.Report(ex);
                context.Log?.Error(ex, "CrystalRenderer scene composite failed.");
            }
            finally
            {
                frames.EndFlush(success);
            }
        }
    }

    public void Dispose()
    {
        // Detach before taking the gate so an in-flight callback can finish.
        hook?.Dispose();
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            target?.Dispose();
            images?.Dispose();
            depth?.Dispose();
            composite?.Dispose();
            shapeState?.Dispose();
            context.Dispose();
        }
    }
}
