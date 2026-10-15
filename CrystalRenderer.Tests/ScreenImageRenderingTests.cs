using System.Numerics;
using System.Runtime.InteropServices;
using CrystalRenderer;
using CrystalRenderer.D3D11;
using SharpDX;
using SharpDX.Direct3D;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using Vector2 = System.Numerics.Vector2;
using Vector3 = System.Numerics.Vector3;
using ScreenImage = CrystalRenderer.D3D11.ScreenImage;
using D3DDevice = SharpDX.Direct3D11.Device;
using MapFlags = SharpDX.Direct3D11.MapFlags;

namespace CrystalRenderer.Tests;

// Render actual screen shaders using Windows' software D3D11 device; no game or GPU is needed.
public sealed class ScreenImageRenderingTests
{
    private readonly record struct Panel(uint Color, Vector3 Center, Vector3 Right);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CrossingOpaqueScreensOccludePerPixelRegardlessOfSubmissionOrder(bool reverse)
    {
        var red = new Panel(0xFFFF0000, new Vector3(0, 0, 0.5f), new Vector3(2, 0, 0.6f));
        var blue = new Panel(0xFF0000FF, new Vector3(0, 0, 0.5f), new Vector3(2, 0, -0.6f));
        var pixels = Render(reverse ? [blue, red] : [red, blue]);
        Assert.Equal(0xFF0000FFu, pixels[8 * 16 + 2]);
        Assert.Equal(0xFFFF0000u, pixels[8 * 16 + 13]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TransparentNearScreenDoesNotHideFarScreen(bool reverse)
    {
        var invisible = new Panel(0x00FF0000, new Vector3(0, 0, 0.7f), new Vector3(2, 0, 0));
        var blue = new Panel(0xFF0000FF, new Vector3(0, 0, 0.3f), new Vector3(2, 0, 0));
        var pixels = Render(reverse ? [blue, invisible] : [invisible, blue]);
        Assert.Equal(0xFF0000FFu, pixels[8 * 16 + 8]);
    }

    [Fact]
    public void TranslucentNearScreenBlendsOverFarOpaqueScreen()
    {
        var red = new Panel(0x80FF0000, new Vector3(0, 0, 0.7f), new Vector3(2, 0, 0));
        var blue = new Panel(0xFF0000FF, new Vector3(0, 0, 0.3f), new Vector3(2, 0, 0));
        var pixel = Render([red, blue])[8 * 16 + 8];
        Assert.InRange((pixel >> 16) & 0xFF, 127u, 129u);
        Assert.InRange(pixel & 0xFF, 126u, 128u);
        Assert.Equal(255u, pixel >> 24);
    }

    [Fact]
    public void DeferredDrawRetainsTextureAfterTheSourceWrapperIsDisposed()
    {
        var red = new Panel(0xFFFF0000, new Vector3(0, 0, 0.5f), new Vector3(2, 0, 0));
        Assert.Equal(0xFFFF0000u, Render([red], disposeSources: true)[8 * 16 + 8]);
    }

    [Fact]
    public void CancellingQueuedScreensDoesNotReplayThemInTheNextFrame()
    {
        var red = new Panel(0xFFFF0000, new Vector3(0, 0, 0.5f), new Vector3(2, 0, 0));
        Assert.All(Render([red], cancel: true), pixel => Assert.Equal(0u, pixel));
    }

    private static uint[] Render(Panel[] panels, bool disposeSources = false, bool cancel = false)
    {
        using var device = new D3DDevice(DriverType.Warp, DeviceCreationFlags.BgraSupport);
        using var context = new RenderContext(device);
        using var target = new RenderTarget(context, 16, 16, AlphaBlendMode.Add);
        using var image = new ScreenImage(context, 8, _ => { });
        using var sceneDepth = new Texture2D(device, new Texture2DDescription
        {
            Width = 16, Height = 16, MipLevels = 1, ArraySize = 1,
            Format = Format.R32_Float, SampleDescription = new(1, 0),
            Usage = ResourceUsage.Default, BindFlags = BindFlags.ShaderResource | BindFlags.RenderTarget,
        });
        using var depthRtv = new RenderTargetView(device, sceneDepth);
        using var depthSrv = new ShaderResourceView(device, sceneDepth);
        device.ImmediateContext.ClearRenderTargetView(depthRtv, new Color4(0));
        var shapeDescription = DepthStencilStateDescription.Default();
        shapeDescription.IsDepthEnabled = false;
        using var shapeState = new DepthStencilState(device, shapeDescription);
        var textures = new List<Texture2D>();
        var views = new List<ShaderResourceView>();
        try
        {
            target.Bind(context);
            context.Context.ClearDepthStencilView(target.ClipStencilDSV,
                DepthStencilClearFlags.Depth | DepthStencilClearFlags.Stencil, 0, 0);
            context.Context.OutputMerger.SetTargets(target.ClipStencilDSV, target.BaseRTV);
            context.Context.PixelShader.SetShaderResource(0, depthSrv);
            var projection = Matrix.Identity;
            projection.M43 = 0.1f;
            image.UpdateConstants(new ScreenImage.Constants
            {
                ViewProj = projection, InvViewProj = Matrix.Invert(projection),
                DepthViewProj = projection, DepthCoefficients = new Vector2(0, 0.1f),
                RenderTargetSize = new Vector2(16, 16), PixelToUv = new Vector2(1f / 16),
            });
            foreach (var panel in panels)
            {
                using var data = new DataStream(4, true, true);
                data.Write(panel.Color);
                var texture = new Texture2D(device, new Texture2DDescription
                {
                    Width = 1, Height = 1, MipLevels = 1, ArraySize = 1,
                    Format = Format.B8G8R8A8_UNorm, SampleDescription = new(1, 0),
                    Usage = ResourceUsage.Immutable, BindFlags = BindFlags.ShaderResource,
                }, new DataRectangle(data.DataPointer, 4));
                textures.Add(texture);
                var view = new ShaderResourceView(device, texture);
                views.Add(view);
                image.Add(view.NativePointer, panel.Center, panel.Right, new Vector3(0, -2, 0),
                    Vector2.Zero, Vector2.One, new ScreenAppearance
                    {
                        OccludedAlpha = 0, OcclusionTolerance = 0.001f,
                        FadeStart = float.PositiveInfinity, FadeStop = float.PositiveInfinity,
                    });
            }
            if (disposeSources)
            {
                foreach (var view in views) view.Dispose();
                foreach (var texture in textures) texture.Dispose();
            }
            if (cancel)
            {
                image.Discard();
                context.DiscardFrame();
                target.Bind(context);
            }
            image.FlushFlat(Vector3.Zero, shapeState);
            context.Execute();
            using var baseResource = target.BaseRTV.Resource;
            using var baseTexture = baseResource.QueryInterface<Texture2D>();
            var stagingDescription = baseTexture.Description;
            stagingDescription.Usage = ResourceUsage.Staging;
            stagingDescription.BindFlags = BindFlags.None;
            stagingDescription.CpuAccessFlags = CpuAccessFlags.Read;
            using var staging = new Texture2D(device, stagingDescription);
            device.ImmediateContext.CopyResource(baseTexture, staging);
            var box = device.ImmediateContext.MapSubresource(staging, 0, MapMode.Read, MapFlags.None);
            try
            {
                var pixels = new uint[16 * 16];
                for (var y = 0; y < 16; y++)
                    for (var x = 0; x < 16; x++)
                        pixels[y * 16 + x] = unchecked((uint)Marshal.ReadInt32(box.DataPointer, y * box.RowPitch + x * 4));
                return pixels;
            }
            finally
            {
                device.ImmediateContext.UnmapSubresource(staging, 0);
            }
        }
        finally
        {
            foreach (var view in views) view.Dispose();
            foreach (var texture in textures) texture.Dispose();
        }
    }
}
