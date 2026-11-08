using System.Runtime.InteropServices;
using CrystalCast.Rendering;
using SharpDX.Direct3D;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using D3D11Device = SharpDX.Direct3D11.Device;

namespace CrystalCast.Tests;

public sealed class DynamicVideoTextureReferenceTests
{
    [Fact]
    public void AcquiringAndReleasingTheDeviceLeavesItsReferenceCountUnchanged()
    {
        using var owner = new D3D11Device(DriverType.Warp, DeviceCreationFlags.BgraSupport);
        using var texture = new Texture2D(owner, new Texture2DDescription
        {
            Width = 4,
            Height = 4,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Dynamic,
            BindFlags = BindFlags.ShaderResource,
            CpuAccessFlags = CpuAccessFlags.Write,
        });

        var before = ReferenceCount(owner.NativePointer);

        // Same order as DynamicVideoTexture: a texture wrapper from QueryInterface, the device
        // and its immediate context, then disposal of context, texture and device.
        var wrapper = texture.QueryInterface<Texture2D>();
        var device = DynamicVideoTexture.AcquireDevice(wrapper);
        var context = device.ImmediateContext;
        context.Dispose();
        wrapper.Dispose();
        device.Dispose();

        Assert.Equal(before, ReferenceCount(owner.NativePointer));
    }

    private static int ReferenceCount(nint pointer)
    {
        Marshal.AddRef(pointer);
        return Marshal.Release(pointer);
    }
}
