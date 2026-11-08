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
    /// <summary>
    /// Capture rotates several shared textures; each is opened once and reused. Older sizes are
    /// dropped on resize, and the cap bounds handles kept for a capture session that restarts.
    /// </summary>
    private const int MaximumOpenedSharedTextures = 8;

    private readonly Dictionary<IntPtr, OpenedSharedTexture> openedSharedTextures = new();
    private D3D11Device? device;
    private DeviceContext? context;
    private ShaderResourceView? shaderResourceView;
    private Texture2D? texture;
    private Texture2D? diagnosticStagingTexture;
    private long openedSequence = -1;
    private long openOrder;
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
            throw;
        }
    }

    private unsafe bool UploadCore(NativeVideoFrame frame)
    {
        var sw = Stopwatch.StartNew();
        EnsureDevice();

        var shared = GetOrOpenSharedTexture(frame.SharedHandle);
        var description = shared.Texture.Description;
        if (shaderResourceView == null || texture == null || Width != description.Width || Height != description.Height)
            RecreateDisplayTexture(description);

        if (context == null || texture == null)
            return false;

        // Never stall the game's render thread: if capture is writing this texture right now,
        // keep showing the previous frame and try again next game frame.
        if (!KeyedMutexSynchronization.TryAcquire(shared.Mutex, 1, 0))
            return false;

        try
        {
            context.CopyResource(shared.Texture, texture);
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
            shared.Mutex.Release(0);
        }

        sw.Stop();
        openedSequence = frame.Sequence;
        UploadCount++;
        LastUploadMilliseconds = sw.Elapsed.TotalMilliseconds;
        return true;
    }

    private OpenedSharedTexture GetOrOpenSharedTexture(IntPtr handle)
    {
        if (openedSharedTextures.TryGetValue(handle, out var existing))
        {
            existing.LastUsed = ++openOrder;
            return existing;
        }

        Texture2D? sharedTexture = null;
        KeyedMutex? mutex = null;
        try
        {
            sharedTexture = device!.OpenSharedResource<Texture2D>(handle);
            mutex = sharedTexture.QueryInterface<KeyedMutex>();
        }
        catch
        {
            mutex?.Dispose();
            sharedTexture?.Dispose();
            throw;
        }

        var opened = new OpenedSharedTexture(sharedTexture, mutex) { LastUsed = ++openOrder };
        var size = sharedTexture.Description;
        // Textures of another size belong to a capture size that is gone.
        foreach (var (otherHandle, other) in openedSharedTextures.ToArray())
        {
            var otherSize = other.Texture.Description;
            if (otherSize.Width != size.Width || otherSize.Height != size.Height)
            {
                other.Dispose();
                openedSharedTextures.Remove(otherHandle);
            }
        }

        while (openedSharedTextures.Count >= MaximumOpenedSharedTextures)
        {
            var oldest = openedSharedTextures.MinBy(entry => entry.Value.LastUsed);
            oldest.Value.Dispose();
            openedSharedTextures.Remove(oldest.Key);
        }

        openedSharedTextures[handle] = opened;
        return opened;
    }

    private void RecreateDisplayTexture(Texture2DDescription sharedDescription)
    {
        Texture2D? nextTexture = null;
        ShaderResourceView? nextShaderResourceView = null;
        try
        {
            nextTexture = new Texture2D(device, new Texture2DDescription
            {
                Width = sharedDescription.Width,
                Height = sharedDescription.Height,
                MipLevels = 1,
                ArraySize = 1,
                Format = sharedDescription.Format,
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
            throw;
        }

        DisposeDisplayTexture();
        texture = nextTexture;
        shaderResourceView = nextShaderResourceView;
        Width = sharedDescription.Width;
        Height = sharedDescription.Height;
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

    private void DisposeDisplayTexture()
    {
        shaderResourceView?.Dispose();
        shaderResourceView = null;
        texture?.Dispose();
        texture = null;
        diagnosticStagingTexture?.Dispose();
        diagnosticStagingTexture = null;
        Width = 0;
        Height = 0;
        lastDiagnosticTicks = 0;
        DiagnosticStatus = "game texture sample pending";
    }

    private void DisposeTexture()
    {
        DisposeDisplayTexture();
        foreach (var opened in openedSharedTextures.Values)
            opened.Dispose();
        openedSharedTextures.Clear();
        openedSequence = -1;
    }

    private sealed class OpenedSharedTexture : IDisposable
    {
        public OpenedSharedTexture(Texture2D texture, KeyedMutex mutex)
        {
            Texture = texture;
            Mutex = mutex;
        }

        public Texture2D Texture { get; }
        public KeyedMutex Mutex { get; }
        public long LastUsed { get; set; }

        public void Dispose()
        {
            Mutex.Dispose();
            Texture.Dispose();
        }
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
