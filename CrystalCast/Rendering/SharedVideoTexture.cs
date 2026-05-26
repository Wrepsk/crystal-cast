using System.Diagnostics;
using System.Runtime.InteropServices;
using CrystalCast.Video;
using FFXIVClientStructs.FFXIV.Client.Graphics.Kernel;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using GameDevice = FFXIVClientStructs.FFXIV.Client.Graphics.Kernel.Device;
using D3D11Device = SharpDX.Direct3D11.Device;

namespace CrystalCast.Rendering;

public sealed class SharedVideoTexture : IDisposable
{
    private D3D11Device? device;
    private DeviceContext? context;
    private Texture2D? sharedTexture;
    private ShaderResourceView? shaderResourceView;
    private Texture2D? texture;
    private Texture2D? diagnosticStagingTexture;
    private IntPtr openedHandle;
    private long openedSequence = -1;
    private long lastDiagnosticTicks;

    public nint NativeHandle => shaderResourceView?.NativePointer ?? 0;
    public int Width { get; private set; }
    public int Height { get; private set; }
    public double LastUploadMilliseconds { get; private set; }
    public long UploadCount { get; private set; }
    public long UploadedSequence => openedSequence;
    public string DiagnosticStatus { get; private set; } = "game texture sample pending";

    public unsafe bool Upload(NativeVideoFrame frame)
    {
        if (frame.Sequence == openedSequence)
            return false;

        if (frame.SharedHandle == IntPtr.Zero || frame.Width <= 0 || frame.Height <= 0)
            return false;

        var sw = Stopwatch.StartNew();
        if (device == null)
        {
            var devicePtr = (nint)GameDevice.Instance()->D3D11Forwarder;
            Marshal.AddRef(devicePtr);
            device = new D3D11Device(devicePtr);
            context = device.ImmediateContext;
        }

        if (shaderResourceView == null
            || texture == null
            || sharedTexture == null
            || openedHandle != frame.SharedHandle
            || Width != frame.Width
            || Height != frame.Height)
        {
            DisposeTexture();
            sharedTexture = device.OpenSharedResource<Texture2D>(frame.SharedHandle);
            var description = sharedTexture.Description;
            texture = new Texture2D(device, new Texture2DDescription
            {
                Width = description.Width,
                Height = description.Height,
                MipLevels = 1,
                ArraySize = 1,
                Format = description.Format,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.ShaderResource,
                CpuAccessFlags = CpuAccessFlags.None,
                OptionFlags = ResourceOptionFlags.None,
            });
            shaderResourceView = new ShaderResourceView(device, texture);
            openedHandle = frame.SharedHandle;
            Width = frame.Width;
            Height = frame.Height;
        }

        if (context == null || sharedTexture == null || texture == null)
            return false;

        context.CopyResource(sharedTexture, texture);
        UpdateDiagnosticStatus();
        context.Flush();

        sw.Stop();
        openedSequence = frame.Sequence;
        UploadCount++;
        LastUploadMilliseconds = sw.Elapsed.TotalMilliseconds;
        return true;
    }

    public void Dispose()
    {
        DisposeTexture();
        context = null;
        device?.Dispose();
        device = null;
    }

    private void UpdateDiagnosticStatus()
    {
        if (context == null || texture == null)
            return;

        var now = Stopwatch.GetTimestamp();
        if (now - lastDiagnosticTicks < Stopwatch.Frequency)
            return;

        lastDiagnosticTicks = now;
        try
        {
            EnsureDiagnosticStagingTexture();
            if (diagnosticStagingTexture == null)
                return;

            context.CopyResource(texture, diagnosticStagingTexture);
            DiagnosticStatus = TextureSampleDiagnostics.SampleBgraTexture(
                context,
                diagnosticStagingTexture,
                "game");
        }
        catch (Exception ex)
        {
            DiagnosticStatus = $"game texture sample failed: {ex.GetBaseException().Message}";
        }
    }

    private void EnsureDiagnosticStagingTexture()
    {
        if (device == null || texture == null)
            return;

        var description = texture.Description;
        if (diagnosticStagingTexture != null
            && diagnosticStagingTexture.Description.Width == description.Width
            && diagnosticStagingTexture.Description.Height == description.Height
            && diagnosticStagingTexture.Description.Format == description.Format)
        {
            return;
        }

        diagnosticStagingTexture?.Dispose();
        diagnosticStagingTexture = new Texture2D(device, new Texture2DDescription
        {
            Width = description.Width,
            Height = description.Height,
            MipLevels = 1,
            ArraySize = 1,
            Format = description.Format,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Staging,
            BindFlags = BindFlags.None,
            CpuAccessFlags = CpuAccessFlags.Read,
            OptionFlags = ResourceOptionFlags.None,
        });
    }

    private void DisposeTexture()
    {
        shaderResourceView?.Dispose();
        shaderResourceView = null;
        texture?.Dispose();
        texture = null;
        sharedTexture?.Dispose();
        sharedTexture = null;
        diagnosticStagingTexture?.Dispose();
        diagnosticStagingTexture = null;
        openedHandle = IntPtr.Zero;
        Width = 0;
        Height = 0;
        openedSequence = -1;
        lastDiagnosticTicks = 0;
        DiagnosticStatus = "game texture sample pending";
    }
}
