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
    private KeyedMutex? sharedTextureMutex;
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

        try
        {
            return UploadCore(frame);
        }
        catch (Exception ex) when (NativeGraphicsError.IsDeviceLost(ex))
        {
            ResetDevice();
            DiagnosticStatus = $"game D3D device lost: {ex.GetBaseException().Message}";
            return false;
        }
    }

    private unsafe bool UploadCore(NativeVideoFrame frame)
    {
        var sw = Stopwatch.StartNew();
        EnsureDevice();

        if (shaderResourceView == null
            || texture == null
            || sharedTexture == null
            || openedHandle != frame.SharedHandle
            || Width != frame.Width
            || Height != frame.Height)
        {
            Texture2D? nextSharedTexture = null;
            KeyedMutex? nextMutex = null;
            Texture2D? nextTexture = null;
            ShaderResourceView? nextShaderResourceView = null;
            try
            {
                nextSharedTexture = device!.OpenSharedResource<Texture2D>(frame.SharedHandle);
                nextMutex = nextSharedTexture.QueryInterface<KeyedMutex>();
                var description = nextSharedTexture.Description;
                nextTexture = new Texture2D(device, new Texture2DDescription
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
                nextShaderResourceView = new ShaderResourceView(device, nextTexture);
            }
            catch
            {
                nextShaderResourceView?.Dispose();
                nextTexture?.Dispose();
                nextMutex?.Dispose();
                nextSharedTexture?.Dispose();
                throw;
            }

            DisposeTexture();
            sharedTexture = nextSharedTexture;
            sharedTextureMutex = nextMutex;
            texture = nextTexture;
            shaderResourceView = nextShaderResourceView;
            openedHandle = frame.SharedHandle;
            Width = nextSharedTexture.Description.Width;
            Height = nextSharedTexture.Description.Height;
        }

        if (context == null || sharedTexture == null || sharedTextureMutex == null || texture == null)
            return false;

        try
        {
            sharedTextureMutex.Acquire(1, 5);
        }
        catch (Exception ex) when (NativeGraphicsError.IsWaitTimeout(ex))
        {
            return false;
        }

        try
        {
            context.CopyResource(sharedTexture, texture);
            if (GraphicsDiagnostics.Enabled)
            {
                UpdateDiagnosticStatus();
            }
            else if (diagnosticStagingTexture != null)
            {
                diagnosticStagingTexture.Dispose();
                diagnosticStagingTexture = null;
                DiagnosticStatus = "GPU sampling disabled";
            }
            context.Flush();
        }
        finally
        {
            sharedTextureMutex.Release(0);
        }

        sw.Stop();
        openedSequence = frame.Sequence;
        UploadCount++;
        LastUploadMilliseconds = sw.Elapsed.TotalMilliseconds;
        return true;
    }

    public void Dispose()
    {
        ResetDevice();
    }

    private unsafe void EnsureDevice()
    {
        if (device != null && context != null)
            return;

        var gameDevice = GameDevice.Instance();
        if (gameDevice == null || gameDevice->D3D11Forwarder == null)
            throw new InvalidOperationException("The game D3D11 device is unavailable.");

        var devicePtr = (nint)gameDevice->D3D11Forwarder;
        D3D11Device? nextDevice = null;
        DeviceContext? nextContext = null;
        var referenceAdded = false;
        try
        {
            Marshal.AddRef(devicePtr);
            referenceAdded = true;
            nextDevice = new D3D11Device(devicePtr);
            referenceAdded = false;
            nextContext = nextDevice.ImmediateContext;
        }
        catch
        {
            nextContext?.Dispose();
            nextDevice?.Dispose();
            if (referenceAdded)
                Marshal.Release(devicePtr);
            throw;
        }

        device = nextDevice;
        context = nextContext;
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

        var nextTexture = new Texture2D(device, new Texture2DDescription
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
        var previous = diagnosticStagingTexture;
        diagnosticStagingTexture = nextTexture;
        previous?.Dispose();
    }

    private void DisposeTexture()
    {
        shaderResourceView?.Dispose();
        shaderResourceView = null;
        texture?.Dispose();
        texture = null;
        sharedTextureMutex?.Dispose();
        sharedTextureMutex = null;
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

    private void ResetDevice()
    {
        DisposeTexture();
        context?.Dispose();
        context = null;
        device?.Dispose();
        device = null;
    }
}
