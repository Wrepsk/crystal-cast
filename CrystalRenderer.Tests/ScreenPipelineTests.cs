using CrystalRenderer.D3D11;
using CrystalRenderer.Integration;
using SharpDX;
using SharpDX.Direct3D;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using Vector3 = System.Numerics.Vector3;
using D3DDevice = SharpDX.Direct3D11.Device;
using MapFlags = SharpDX.Direct3D11.MapFlags;

namespace CrystalRenderer.Tests;

public sealed class ScreenPipelineTests
{
    [Fact]
    public void NativeOutputCompositesTheScreenIntoTheOverlayTexture()
    {
        using var scene = new SoftwareScene();
        using var pipeline = scene.CreatePipeline(() => scene.Frame);
        pipeline.Submit(scene.RedScreen);
        var result = pipeline.Render(RenderOutput.NativeOverlay);
        Assert.NotNull(result);
        Assert.Equal(0xFFFF0000u, scene.ReadCenter(result.ProcessedTexture));
    }

    [Fact]
    public void MissingDepthClearsTheOverlayInsteadOfReplayingPreviousPixels()
    {
        using var scene = new SoftwareScene();
        var frame = scene.Frame;
        using var pipeline = scene.CreatePipeline(() => frame);
        pipeline.Submit(scene.RedScreen);
        Assert.Equal(0xFFFF0000u, scene.ReadCenter(pipeline.Render(RenderOutput.NativeOverlay)!.ProcessedTexture));
        frame = frame with { DepthTexture = 0 };
        pipeline.Submit(scene.RedScreen);
        Assert.Equal(0u, scene.ReadCenter(pipeline.Render(RenderOutput.NativeOverlay)!.ProcessedTexture));
        frame = scene.Frame;
        pipeline.Submit(scene.RedScreen);
        Assert.Equal(0xFFFF0000u, scene.ReadCenter(pipeline.Render(RenderOutput.NativeOverlay)!.ProcessedTexture));
    }

    [Fact]
    public void DeferredCompositeCapturesSceneOnlyWhenTheHookFlushes()
    {
        using var scene = new SoftwareScene();
        var captures = 0;
        using var pipeline = scene.CreatePipeline(() => { captures++; return scene.Frame; });
        pipeline.Submit(scene.RedScreen);
        pipeline.Schedule();
        Assert.Equal(0, captures);
        Assert.True(pipeline.Pending);
        pipeline.FlushSceneComposite();
        pipeline.Failures.ThrowIfFailed();
        Assert.Equal(1, captures);
        Assert.False(pipeline.Pending);
        Assert.Equal(0xFFFF0000u, scene.ReadCenter(scene.BackBuffer));
    }

    [Fact]
    public void CancelledCompositeCannotReplayItsScreenOnAFutureHook()
    {
        using var scene = new SoftwareScene();
        var captures = 0;
        using var pipeline = scene.CreatePipeline(() => { captures++; return scene.Frame; });
        pipeline.Submit(scene.RedScreen);
        pipeline.Schedule();
        pipeline.Cancel("screen deleted");
        pipeline.FlushSceneComposite();
        Assert.Equal(0, captures);
        Assert.False(pipeline.Pending);
        Assert.Equal(0u, scene.ReadCenter(scene.BackBuffer));
    }

    [Fact]
    public void ImGuiOutputMasksOpaqueGameUi()
    {
        using var scene = new SoftwareScene();
        using var ui = new RenderTargetView(scene.Device, scene.BackBuffer);
        scene.Device.ImmediateContext.ClearRenderTargetView(ui, new Color4(1, 1, 1, 1));
        using var pipeline = scene.CreatePipeline(() => scene.Frame);
        pipeline.Submit(scene.RedScreen);
        Assert.Equal(0u, scene.ReadCenter(pipeline.Render(RenderOutput.ImGuiOverlay)!.ProcessedTexture) >> 24);
    }

    [Fact]
    public void CallbackFailuresAreDeliveredOnceAndAllowManagedRecovery()
    {
        using var scene = new SoftwareScene();
        var failure = new InvalidOperationException("scene capture failed");
        using var pipeline = scene.CreatePipeline(() => throw failure);
        pipeline.Submit(scene.RedScreen);
        pipeline.Schedule();
        pipeline.FlushSceneComposite();
        Assert.False(pipeline.Pending);
        Assert.Same(failure, Record.Exception(pipeline.Failures.ThrowIfFailed));
        pipeline.Failures.ThrowIfFailed();
    }

    private sealed class SoftwareScene : IDisposable
    {
        internal D3DDevice Device { get; } = new(DriverType.Warp, DeviceCreationFlags.BgraSupport);
        internal Texture2D BackBuffer { get; }
        private readonly Texture2D depth;
        private readonly Texture2D screen;
        private readonly ShaderResourceView screenView;

        internal SoftwareScene()
        {
            BackBuffer = CreateTarget(Format.B8G8R8A8_UNorm);
            depth = CreateTarget(Format.R32_Typeless);
            using var backbufferView = new RenderTargetView(Device, BackBuffer);
            using var depthView = new RenderTargetView(Device, depth,
                new RenderTargetViewDescription { Format = Format.R32_Float, Dimension = RenderTargetViewDimension.Texture2D });
            Device.ImmediateContext.ClearRenderTargetView(backbufferView, new Color4(0));
            Device.ImmediateContext.ClearRenderTargetView(depthView, new Color4(0));
            using var pixels = new DataStream(4, true, true);
            pixels.Write(0xFFFF0000u);
            screen = new Texture2D(Device, new Texture2DDescription
            {
                Width = 1, Height = 1, MipLevels = 1, ArraySize = 1,
                Format = Format.B8G8R8A8_UNorm, SampleDescription = new(1, 0),
                Usage = ResourceUsage.Immutable, BindFlags = BindFlags.ShaderResource,
            }, new DataRectangle(pixels.DataPointer, 4));
            screenView = new ShaderResourceView(Device, screen);
        }

        internal GameSceneFrame Frame
        {
            get
            {
                var projection = Matrix.Identity;
                projection.M43 = 0.1f;
                return new GameSceneFrame(projection, new SharpDX.Vector2(16, 16), Vector3.Zero,
                    BackBuffer.NativePointer, depth.NativePointer, new SharpDX.Vector2(1, 1), false);
            }
        }

        internal ScreenSurface RedScreen => new(screenView.NativePointer, new Vector3(0, 0, 0.5f),
            new Vector3(2, 0, 0), new Vector3(0, -2, 0), new ScreenAppearance());

        internal ScreenRenderPipeline CreatePipeline(Func<GameSceneFrame?> capture)
            => new(new RenderContext(Device), 8, capture);

        private Texture2D CreateTarget(Format format) => new(Device, new Texture2DDescription
        {
            Width = 16, Height = 16, MipLevels = 1, ArraySize = 1,
            Format = format, SampleDescription = new(1, 0), Usage = ResourceUsage.Default,
            BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
        });

        internal uint ReadCenter(Texture2D texture)
        {
            var description = texture.Description;
            description.Usage = ResourceUsage.Staging;
            description.BindFlags = BindFlags.None;
            description.CpuAccessFlags = CpuAccessFlags.Read;
            using var staging = new Texture2D(Device, description);
            Device.ImmediateContext.CopyResource(texture, staging);
            var box = Device.ImmediateContext.MapSubresource(staging, 0, MapMode.Read, MapFlags.None);
            try
            {
                return unchecked((uint)System.Runtime.InteropServices.Marshal.ReadInt32(box.DataPointer, 8 * box.RowPitch + 8 * 4));
            }
            finally { Device.ImmediateContext.UnmapSubresource(staging, 0); }
        }

        public void Dispose()
        {
            screenView.Dispose();
            screen.Dispose();
            depth.Dispose();
            BackBuffer.Dispose();
            Device.Dispose();
        }
    }
}
