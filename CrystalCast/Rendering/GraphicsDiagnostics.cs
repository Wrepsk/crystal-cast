using System.Runtime.InteropServices;
using GameDevice = FFXIVClientStructs.FFXIV.Client.Graphics.Kernel.Device;
using D3D11Device = SharpDX.Direct3D11.Device;

namespace CrystalCast.Rendering;

internal readonly record struct GraphicsEnvironmentSnapshot(
    string Adapter,
    string FeatureLevel,
    int ViewportWidth,
    int ViewportHeight,
    string Status);

internal static class GraphicsDiagnostics
{
    private static int enabled;

    public static bool Enabled
    {
        get => Volatile.Read(ref enabled) != 0;
        set => Volatile.Write(ref enabled, value ? 1 : 0);
    }

    public static unsafe GraphicsEnvironmentSnapshot CaptureEnvironment()
    {
        var gameDevice = GameDevice.Instance();
        if (gameDevice == null)
            return new("unavailable", "unavailable", 0, 0, "game graphics device is null");

        var width = (int)gameDevice->Width;
        var height = (int)gameDevice->Height;
        if (gameDevice->D3D11Forwarder == null)
            return new("unavailable", "unavailable", width, height, "game D3D11 device is null");

        var devicePointer = (nint)gameDevice->D3D11Forwarder;
        var referenceAdded = false;
        try
        {
            Marshal.AddRef(devicePointer);
            referenceAdded = true;
            using var device = new D3D11Device(devicePointer);
            referenceAdded = false;
            using var dxgiDevice = device.QueryInterface<SharpDX.DXGI.Device>();
            using var adapter = dxgiDevice.Adapter;
            var description = adapter.Description;
            var adapterName = description.Description.TrimEnd('\0', ' ');
            var videoMemoryMiB = Math.Max(0L, (long)description.DedicatedVideoMemory) / (1024L * 1024L);
            var adapterDetails = $"{adapterName}; vendor 0x{description.VendorId:X4}; device 0x{description.DeviceId:X4}; dedicated VRAM {videoMemoryMiB} MiB";
            return new(adapterDetails, device.FeatureLevel.ToString(), width, height, "ready");
        }
        catch (Exception ex)
        {
            if (referenceAdded)
                Marshal.Release(devicePointer);
            return new("unavailable", "unavailable", width, height, $"query failed: {ex.GetBaseException().Message}");
        }
    }
}
