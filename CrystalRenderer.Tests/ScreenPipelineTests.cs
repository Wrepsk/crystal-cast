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

    [Fact]
    public void StabilizedScreenStillSamplesDepthAtTheGamesShiftedCoordinates()
    {
        using var scene = new SoftwareScene();
        scene.SetDepth((x, _) => x >= 12 ? 1f : 0f);
        var frame = scene.Frame;
        var shiftedDepth = frame.ViewProjection;
        shiftedDepth.M41 = 0.5f;
        frame = frame with { DepthViewProjection = shiftedDepth };
        using var pipeline = scene.CreatePipeline(() => frame);
        pipeline.Submit(scene.RedScreen);
        Assert.Equal(0u, scene.ReadCenter(pipeline.Render(RenderOutput.NativeOverlay)!.ProcessedTexture));

        shiftedDepth.M41 = -0.5f;
        frame = frame with { DepthViewProjection = shiftedDepth };
        pipeline.Submit(scene.RedScreen);
        Assert.Equal(0xFFFF0000u, scene.ReadCenter(pipeline.Render(RenderOutput.NativeOverlay)!.ProcessedTexture));
    }

    [Fact]
    public void ChangingDepthJitterDoesNotMoveStationaryScreenPixels()
    {
        using var scene = new SoftwareScene();
        var projection = System.Numerics.Matrix4x4.CreatePerspectiveFieldOfView(1f, 1f, 0.1f, 1000f);
        projection.M33 = 0f;
        projection.M43 = 0.1f;
        var frame = scene.Frame;
        using var pipeline = scene.CreatePipeline(() => frame);
        uint[]? previous = null;
        foreach (var jitter in new[] { -0.45f, 0f, 0.45f })
        {
            var depthProjection = projection;
            depthProjection.M31 = 2f * jitter / 16;
            Assert.True(CameraProjection.TryCreate(System.Numerics.Matrix4x4.Identity, projection, depthProjection, out var matrices));
            frame = frame with
            {
                ViewProjection = ToSharpDx(matrices.ViewProjection), DepthViewProjection = ToSharpDx(matrices.DepthViewProjection),
            };
            pipeline.Submit(scene.RedScreen with { Center = new Vector3(0, 0, -2), Right = Vector3.UnitX, Down = -Vector3.UnitY });
            var target = pipeline.Render(RenderOutput.NativeOverlay)!;
            var pixels = scene.ReadPixels(target.ProcessedTexture);
            Assert.Contains(0xFFFF0000u, pixels);
            Assert.Contains(0u, pixels);
            if (previous != null) Assert.Equal(previous, pixels);
            previous = pixels;
        }
    }

    [Theory]
    [InlineData(0.25f, true)]
    [InlineData(0.75f, false)]
    public void DepthSamplingDoesNotInventForegroundBetweenDepthTexels(float offsetPixels, bool expectedVisible)
    {
        using var scene = new SoftwareScene();
        scene.SetDepth((x, _) => x >= 9 ? 1f : 0f);
        var frame = scene.Frame;
        var depthProjection = frame.DepthViewProjection;
        depthProjection.M41 = offsetPixels * 2f / 16;
        frame = frame with { DepthViewProjection = depthProjection };
        using var pipeline = scene.CreatePipeline(() => frame);
        // The quarter-pixel sample is over clear depth; linear interpolation would
        // invent a foreground surface. The three-quarter sample must remain hidden.
        pipeline.Submit(scene.RedScreen with { Center = new Vector3(0, 0, -0.05f) });
        Assert.Equal(expectedVisible ? 0xFFFF0000u : 0u,
            scene.ReadCenter(pipeline.Render(RenderOutput.NativeOverlay)!.ProcessedTexture));
    }

    [Fact]
    public void VideoKeepsLinearFilteringAfterThePointSampledCompositePass()
    {
        using var scene = new SoftwareScene([0xFFFF0000u, 0xFF0000FFu]);
        using var pipeline = scene.CreatePipeline(() => scene.Frame);
        for (var frame = 0; frame < 2; frame++)
        {
            pipeline.Submit(scene.RedScreen);
            var pixel = scene.ReadCenter(pipeline.Render(RenderOutput.NativeOverlay)!.ProcessedTexture);
            Assert.InRange((pixel >> 16) & 255, 110u, 113u);
            Assert.InRange(pixel & 255, 142u, 145u);
        }
    }

    private static Matrix ToSharpDx(System.Numerics.Matrix4x4 matrix)
        => System.Runtime.CompilerServices.Unsafe.BitCast<System.Numerics.Matrix4x4, Matrix>(matrix);

    [Theory]
    [InlineData(0f)]
    [InlineData(100f)]
    public void FiniteReverseDepthFadeUsesLensCoefficientsAtAnyCameraPosition(float cameraZ)
    {
        using var scene = new SoftwareScene();
        var projection = System.Numerics.Matrix4x4.CreatePerspectiveFieldOfView(1f, 1f, 0.1f, 100f);
        projection.M33 = 0.1f / (100f - 0.1f);
        projection.M43 = 0.1f * 100f / (100f - 0.1f);
        var viewProjection = ToSharpDx(System.Numerics.Matrix4x4.CreateTranslation(0, 0, -cameraZ) * projection);
        var frame = scene.Frame with
        {
            ViewProjection = viewProjection, DepthViewProjection = viewProjection,
            DepthCoefficients = new System.Numerics.Vector2(projection.M33, projection.M43),
            CameraPosition = new Vector3(0, 0, cameraZ),
        };
        using var pipeline = scene.CreatePipeline(() => frame);
        pipeline.Submit(scene.RedScreen with
        {
            Center = new Vector3(0, 0, cameraZ - 2f),
            Appearance = new ScreenAppearance { FadeStart = 1f, FadeStop = 3f },
        });
        var pixel = scene.ReadCenter(pipeline.Render(RenderOutput.NativeOverlay)!.ProcessedTexture);
        Assert.InRange(pixel >> 24, 127u, 129u);
    }

    private sealed class SoftwareScene : IDisposable
    {
        internal D3DDevice Device { get; } = new(DriverType.Warp, DeviceCreationFlags.BgraSupport);
        internal Texture2D BackBuffer { get; }
        private readonly Texture2D depth;
        private readonly Texture2D screen;
        private readonly ShaderResourceView screenView;

        internal SoftwareScene(uint[]? screenPixels = null)
        {
            BackBuffer = CreateTarget(Format.B8G8R8A8_UNorm);
            depth = CreateTarget(Format.R32_Typeless);
            using var backbufferView = new RenderTargetView(Device, BackBuffer);
            using var depthView = new RenderTargetView(Device, depth,
                new RenderTargetViewDescription { Format = Format.R32_Float, Dimension = RenderTargetViewDimension.Texture2D });
            Device.ImmediateContext.ClearRenderTargetView(backbufferView, new Color4(0));
            Device.ImmediateContext.ClearRenderTargetView(depthView, new Color4(0));
            screenPixels ??= [0xFFFF0000u];
            using var pixels = new DataStream(screenPixels.Length * 4, true, true);
            foreach (var pixel in screenPixels) pixels.Write(pixel);
            screen = new Texture2D(Device, new Texture2DDescription
            {
                Width = screenPixels.Length, Height = 1, MipLevels = 1, ArraySize = 1,
                Format = Format.B8G8R8A8_UNorm, SampleDescription = new(1, 0),
                Usage = ResourceUsage.Immutable, BindFlags = BindFlags.ShaderResource,
            }, new DataRectangle(pixels.DataPointer, screenPixels.Length * 4));
            screenView = new ShaderResourceView(Device, screen);
        }

        internal GameSceneFrame Frame
        {
            get
            {
                var projection = Matrix.Identity;
                projection.M43 = 0.1f;
                return new GameSceneFrame(projection, projection, new System.Numerics.Vector2(0, 0.1f), new SharpDX.Vector2(16, 16), Vector3.Zero,
                    BackBuffer.NativePointer, depth.NativePointer, new SharpDX.Vector2(1, 1), false);
            }
        }

        internal ScreenSurface RedScreen => new(screenView.NativePointer, new Vector3(0, 0, 0.5f),
            new Vector3(2, 0, 0), new Vector3(0, -2, 0), new ScreenAppearance());

        internal ScreenRenderPipeline CreatePipeline(Func<GameSceneFrame?> capture)
            => new(new RenderContext(Device), 8, capture);

        internal void SetDepth(Func<int, int, float> value)
        {
            using var data = new DataStream(16 * 16 * sizeof(float), true, true);
            for (var y = 0; y < 16; y++)
                for (var x = 0; x < 16; x++) data.Write(value(x, y));
            Device.ImmediateContext.UpdateSubresource(new DataBox(data.DataPointer, 16 * sizeof(float), 0), depth);
        }

        private Texture2D CreateTarget(Format format) => new(Device, new Texture2DDescription
        {
            Width = 16, Height = 16, MipLevels = 1, ArraySize = 1,
            Format = format, SampleDescription = new(1, 0), Usage = ResourceUsage.Default,
            BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
        });

        internal uint ReadCenter(Texture2D texture)
            => ReadPixels(texture)[8 * 16 + 8];

        internal uint[] ReadPixels(Texture2D texture)
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
                var pixels = new uint[16 * 16];
                for (var y = 0; y < 16; y++)
                    for (var x = 0; x < 16; x++)
                        pixels[y * 16 + x] = unchecked((uint)System.Runtime.InteropServices.Marshal.ReadInt32(box.DataPointer, y * box.RowPitch + x * 4));
                return pixels;
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
