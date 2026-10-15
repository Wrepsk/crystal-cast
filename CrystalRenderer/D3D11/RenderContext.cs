// Derived from Pictomancy (sourpuh and contributors), AGPL-3.0-or-later.
// See LICENSE.Pictomancy and THIRD_PARTY_NOTICES.md.
using Dalamud.Plugin.Services;
using SharpDX.Direct3D11;
using System.Runtime.InteropServices;

namespace CrystalRenderer.D3D11;

// device + deferred context
internal class RenderContext : IDisposable
{
    public SharpDX.Direct3D11.Device Device { get; private set; }
    public SharpDX.Direct3D11.DeviceContext Context { get; private set; }
    internal SamplerState PointClampSampler { get; }
    internal SamplerState LinearClampSampler { get; }

    private class CacheEntry
    {
        public ShaderResourceView Srv = null!;
        public int LastFrame;
    }
    private readonly Dictionary<IntPtr, CacheEntry> _userSrvCache = new();
    private int _frameCounter;
    internal IPluginLog? Log { get; }
    private readonly bool _ownsDevice;
    private const int UserSrvTtlFrames = 120;

    public unsafe RenderContext(IPluginLog? log = null)
        : this(AcquireGameDevice(), ownsDevice: true, log)
    {
    }

    internal RenderContext(SharpDX.Direct3D11.Device device, bool ownsDevice = false, IPluginLog? log = null)
    {
        Device = device;
        Log = log;
        _ownsDevice = ownsDevice;
        try
        {
            Context = new(Device);
            var sampler = SamplerStateDescription.Default();
            sampler.AddressU = TextureAddressMode.Clamp;
            sampler.AddressV = TextureAddressMode.Clamp;
            sampler.AddressW = TextureAddressMode.Clamp;
            sampler.Filter = Filter.MinMagMipPoint;
            PointClampSampler = new SamplerState(Device, sampler);
            sampler.Filter = Filter.MinMagMipLinear;
            LinearClampSampler = new SamplerState(Device, sampler);
        }
        catch
        {
            LinearClampSampler?.Dispose();
            PointClampSampler?.Dispose();
            Context?.Dispose();
            if (_ownsDevice) Device.Dispose();
            throw;
        }
    }

    private static unsafe SharpDX.Direct3D11.Device AcquireGameDevice()
    {
        var gameDevice = FFXIVClientStructs.FFXIV.Client.Graphics.Kernel.Device.Instance();
        var pointer = gameDevice == null ? nint.Zero : (nint)gameDevice->D3D11Forwarder;
        if (pointer == nint.Zero)
            throw new InvalidOperationException("The game D3D11 device is unavailable.");
        Marshal.AddRef(pointer);
        try
        {
            return new SharpDX.Direct3D11.Device(pointer);
        }
        catch
        {
            Marshal.Release(pointer);
            throw;
        }
    }

    public void Dispose()
    {
        foreach (var entry in _userSrvCache.Values)
            entry.Srv.Dispose();
        _userSrvCache.Clear();
        LinearClampSampler.Dispose();
        PointClampSampler.Dispose();
        Context.Dispose();
        if (_ownsDevice) Device.Dispose();
    }

    public void Execute()
    {
        using var cmds = Context.FinishCommandList(true);
        Device.ImmediateContext.ExecuteCommandList(cmds, true);
        Context.ClearState();
    }

    public void DiscardFrame()
    {
        using var commands = Context.FinishCommandList(false);
        Context.ClearState();
    }

    public void BeginFrame()
    {
        _frameCounter++;
        int cutoff = _frameCounter - UserSrvTtlFrames;
        List<IntPtr>? stale = null;
        foreach (var kvp in _userSrvCache)
        {
            if (kvp.Value.LastFrame < cutoff)
                (stale ??= new()).Add(kvp.Key);
        }
        if (stale != null)
        {
            foreach (var key in stale)
            {
                _userSrvCache[key].Srv.Dispose();
                _userSrvCache.Remove(key);
            }
        }
    }

    public ShaderResourceView GetUserSrv(IntPtr nativePtr)
    {
        if (!_userSrvCache.TryGetValue(nativePtr, out var entry))
        {
            Marshal.AddRef(nativePtr);
            entry = new CacheEntry { Srv = new ShaderResourceView(nativePtr) };
            _userSrvCache[nativePtr] = entry;
        }
        entry.LastFrame = _frameCounter;
        return entry.Srv;
    }
}
