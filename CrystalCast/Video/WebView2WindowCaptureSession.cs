using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using FFXIVClientStructs.FFXIV.Client.Graphics.Kernel;
using SharpDX;
using SharpDX.Direct3D;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using D3D11Device = SharpDX.Direct3D11.Device;
using D3D11Texture2D = SharpDX.Direct3D11.Texture2D;
using DxgiDevice = SharpDX.DXGI.Device;
using DxgiFormat = SharpDX.DXGI.Format;
using GameDevice = FFXIVClientStructs.FFXIV.Client.Graphics.Kernel.Device;

namespace CrystalCast.Video;

[SupportedOSPlatform("windows10.0.19041")]
internal sealed class WebView2WindowCaptureSession : IDisposable
{
    private const int DirectXPixelFormatB8G8R8A8UIntNormalized = 87;
    private const int FramePoolBufferCount = 2;
    private const int RoInitSingleThreaded = 0;
    private const int SFalse = 1;
    private const int RpcEChangedMode = unchecked((int)0x80010106);

    private static readonly Guid GraphicsCaptureItemGuid = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");
    private static readonly Guid GraphicsCaptureItemInteropGuid = new("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356");
    private static readonly Guid GraphicsCaptureSession2Guid = new("2C39AE40-7D2E-5044-804E-8B6799D4CF9E");
    private static readonly Guid GraphicsCaptureSessionStaticsGuid = new("2224A540-5974-49AA-B232-0882536F4CB5");
    private static readonly Guid Direct3D11CaptureFramePoolStatics2Guid = new("589B103F-6BBC-5DF5-A991-02E28B3B66D5");
    private static readonly Guid Direct3D11Texture2DGuid = new("6F15AAF2-D208-4E89-9AB4-489535D34F9C");
    private static readonly Guid Direct3DDxgiInterfaceAccessGuid = new("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1");
    private static readonly Guid ClosableGuid = new("30D5A829-7FA4-4026-83BB-D75BAE4EA99E");

    private readonly Func<bool> shouldCapture;
    private readonly Func<float> captureFpsProvider;
    private readonly Action<IntPtr, int, int> publishSharedTexture;
    private readonly Action<double> reportCaptureMilliseconds;
    private readonly Action<string> reportStatus;
    private readonly D3D11Device d3dDevice;
    private readonly DeviceContext d3dContext;
    private readonly bool uninitializeWinRt;
    private IntPtr winRtDevice;
    private IntPtr item;
    private IntPtr framePool;
    private IntPtr session;
    private CancellationTokenSource? captureCancellation;
    private Task? captureTask;
    private D3D11Texture2D? sharedTexture;
    private D3D11Texture2D? diagnosticStagingTexture;
    private IntPtr sharedTextureHandle;
    private SizeInt32 captureSize;
    private long lastPublishedTicks;
    private long lastDiagnosticTicks;
    private string lastDiagnosticStatus = "diagnostic pending";
    private bool disposed;

    [SupportedOSPlatform("windows10.0.19041")]
    public WebView2WindowCaptureSession(
        IntPtr hwnd,
        int width,
        int height,
        Func<bool> shouldCapture,
        Func<float> captureFpsProvider,
        Action<IntPtr, int, int> publishSharedTexture,
        Action<double> reportCaptureMilliseconds,
        Action<string> reportStatus)
    {
        this.shouldCapture = shouldCapture;
        this.captureFpsProvider = captureFpsProvider;
        this.publishSharedTexture = publishSharedTexture;
        this.reportCaptureMilliseconds = reportCaptureMilliseconds;
        this.reportStatus = reportStatus;

        uninitializeWinRt = InitializeWinRt();
        d3dDevice = CreateCaptureDevice();
        d3dContext = d3dDevice.ImmediateContext;
        winRtDevice = CreateDirect3DDevice(d3dDevice);
        item = CreateCaptureItem(hwnd);
        captureSize = GetCaptureSize(item, width, height);
        framePool = CreateFramePool(winRtDevice, captureSize);
        session = CreateCaptureSession(framePool, item);
        DisableCursorCapture(session);
    }

    [SupportedOSPlatformGuard("windows10.0.19041")]
    public static bool IsSupported(out string status)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
        {
            status = "WebView2 window capture requires Windows 10 2004 or newer";
            return false;
        }

        var uninitialize = false;
        try
        {
            uninitialize = InitializeWinRt();
            if (!IsGraphicsCaptureSupported())
            {
                status = "Windows Graphics Capture is not supported on this system";
                return false;
            }
        }
        catch (Exception ex)
        {
            status = $"Windows Graphics Capture unavailable: {ex.GetBaseException().Message}";
            return false;
        }
        finally
        {
            if (uninitialize)
                RoUninitialize();
        }

        status = "Windows Graphics Capture available";
        return true;
    }

    public void Start()
    {
        ThrowIfDisposed();
        StartCapture(session);
        captureCancellation = new CancellationTokenSource();
        captureTask = Task.Run(() => CaptureLoopAsync(captureCancellation.Token));
        reportStatus("WebView2 window capture running");
    }

    public void Dispose()
    {
        if (disposed)
            return;

        disposed = true;
        captureCancellation?.Cancel();
        try
        {
            captureTask?.Wait(TimeSpan.FromSeconds(1));
        }
        catch
        {
            // The session is already shutting down; stale frame work is safe to abandon.
        }

        captureCancellation?.Dispose();
        captureCancellation = null;
        captureTask = null;
        CloseAndRelease(ref session);
        CloseAndRelease(ref framePool);
        Release(ref item);
        Release(ref winRtDevice);
        sharedTexture?.Dispose();
        sharedTexture = null;
        diagnosticStagingTexture?.Dispose();
        diagnosticStagingTexture = null;
        sharedTextureHandle = IntPtr.Zero;
        d3dContext.ClearState();
        d3dContext.Flush();
        d3dDevice.Dispose();

        if (uninitializeWinRt)
            RoUninitialize();
    }

    private async Task CaptureLoopAsync(CancellationToken token)
    {
        while (!disposed && !token.IsCancellationRequested)
        {
            if (!shouldCapture())
            {
                await DelayAsync(200, token);
                continue;
            }

            var sw = Stopwatch.StartNew();
            var frame = IntPtr.Zero;
            try
            {
                frame = TryGetLatestFrameFromPool(framePool);
                if (frame == IntPtr.Zero)
                {
                    await DelayAsync(1, token);
                    continue;
                }

                ProcessFrame(frame, sw);
            }
            catch (Exception ex) when (!token.IsCancellationRequested)
            {
                sw.Stop();
                reportCaptureMilliseconds(sw.Elapsed.TotalMilliseconds);
                reportStatus($"WebView2 window capture failed: {ex.GetBaseException().Message}");
                await DelayAsync(250, token);
            }
            finally
            {
                CloseAndRelease(ref frame);
            }

            await Task.Yield();
        }
    }

    private static async Task DelayAsync(int milliseconds, CancellationToken token)
    {
        try
        {
            await Task.Delay(milliseconds, token);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void ProcessFrame(IntPtr frame, Stopwatch sw)
    {
        var contentSize = GetFrameContentSize(frame);
        if (contentSize.Width <= 0 || contentSize.Height <= 0)
            return;

        if (contentSize.Width != captureSize.Width || contentSize.Height != captureSize.Height)
        {
            captureSize = contentSize;
            RecreateSharedTexture(contentSize.Width, contentSize.Height);
            RecreateFramePool(framePool, winRtDevice, captureSize);
            return;
        }

        if (!ShouldPublishFrame())
            return;

        EnsureSharedTexture(contentSize.Width, contentSize.Height);
        if (sharedTexture == null || sharedTextureHandle == IntPtr.Zero)
            return;

        var surface = IntPtr.Zero;
        try
        {
            surface = GetFrameSurface(frame);
            using var sourceTexture = GetDirect3DTexture(surface);
            d3dContext.CopyResource(sourceTexture, sharedTexture);
            var diagnosticStatus = UpdateDiagnosticStatus(sourceTexture, contentSize.Width, contentSize.Height);
            d3dContext.Flush();
            sw.Stop();
            reportCaptureMilliseconds(sw.Elapsed.TotalMilliseconds);
            publishSharedTexture(sharedTextureHandle, contentSize.Width, contentSize.Height);
            reportStatus($"GPU; {diagnosticStatus}");
        }
        finally
        {
            Release(ref surface);
        }
    }

    private bool ShouldPublishFrame()
    {
        var captureFps = Math.Clamp(captureFpsProvider(), 1.0f, 120.0f);
        if (captureFps >= 55.0f)
            return true;

        var now = Stopwatch.GetTimestamp();
        var previous = Interlocked.Read(ref lastPublishedTicks);
        var minimumTicks = (long)(Stopwatch.Frequency / captureFps * 0.85);
        if (previous != 0 && now - previous < minimumTicks)
            return false;

        Interlocked.Exchange(ref lastPublishedTicks, now);
        return true;
    }

    private static IntPtr TryGetLatestFrameFromPool(IntPtr currentFramePool)
    {
        var latestFrame = TryGetNextFrame(currentFramePool);
        if (latestFrame == IntPtr.Zero)
            return IntPtr.Zero;

        while (true)
        {
            var nextFrame = TryGetNextFrame(currentFramePool);
            if (nextFrame == IntPtr.Zero)
                return latestFrame;

            CloseAndRelease(ref latestFrame);
            latestFrame = nextFrame;
        }
    }

    private void EnsureSharedTexture(int width, int height)
    {
        if (sharedTexture != null
            && sharedTextureHandle != IntPtr.Zero
            && sharedTexture.Description.Width == width
            && sharedTexture.Description.Height == height)
        {
            return;
        }

        RecreateSharedTexture(width, height);
    }

    private void RecreateSharedTexture(int width, int height)
    {
        sharedTexture?.Dispose();
        sharedTextureHandle = IntPtr.Zero;
        sharedTexture = new D3D11Texture2D(d3dDevice, new Texture2DDescription
        {
            Width = width,
            Height = height,
            MipLevels = 1,
            ArraySize = 1,
            Format = DxgiFormat.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.ShaderResource,
            CpuAccessFlags = CpuAccessFlags.None,
            OptionFlags = ResourceOptionFlags.Shared,
        });
        using var dxgiResource = sharedTexture.QueryInterface<SharpDX.DXGI.Resource>();
        sharedTextureHandle = dxgiResource.SharedHandle;
    }

    private string UpdateDiagnosticStatus(D3D11Texture2D sourceTexture, int width, int height)
    {
        var now = Stopwatch.GetTimestamp();
        if (now - lastDiagnosticTicks < Stopwatch.Frequency)
            return lastDiagnosticStatus;

        lastDiagnosticTicks = now;
        try
        {
            EnsureDiagnosticStagingTexture(width, height);
            if (diagnosticStagingTexture == null)
                return lastDiagnosticStatus;

            d3dContext.CopyResource(sourceTexture, diagnosticStagingTexture);
            lastDiagnosticStatus = TextureSampleDiagnostics.SampleBgraTexture(
                d3dContext,
                diagnosticStagingTexture,
                "WGC");
        }
        catch (Exception ex)
        {
            lastDiagnosticStatus = $"WGC sample failed: {ex.GetBaseException().Message}";
        }

        return lastDiagnosticStatus;
    }

    private void EnsureDiagnosticStagingTexture(int width, int height)
    {
        if (diagnosticStagingTexture != null
            && diagnosticStagingTexture.Description.Width == width
            && diagnosticStagingTexture.Description.Height == height)
        {
            return;
        }

        diagnosticStagingTexture?.Dispose();
        diagnosticStagingTexture = new D3D11Texture2D(d3dDevice, new Texture2DDescription
        {
            Width = width,
            Height = height,
            MipLevels = 1,
            ArraySize = 1,
            Format = DxgiFormat.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Staging,
            BindFlags = BindFlags.None,
            CpuAccessFlags = CpuAccessFlags.Read,
            OptionFlags = ResourceOptionFlags.None,
        });
    }

    private void ThrowIfDisposed()
    {
        if (disposed)
            throw new ObjectDisposedException(nameof(WebView2WindowCaptureSession));
    }

    private static bool InitializeWinRt()
    {
        var hr = RoInitialize(RoInitSingleThreaded);
        if (hr == 0 || hr == SFalse)
            return true;

        if (hr == RpcEChangedMode)
            return false;

        Marshal.ThrowExceptionForHR(hr);
        return false;
    }

    private static IntPtr CreateDirect3DDevice(D3D11Device device)
    {
        using var dxgiDevice = device.QueryInterface<DxgiDevice>();
        Marshal.ThrowExceptionForHR(CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice.NativePointer, out var graphicsDevice));
        return graphicsDevice;
    }

    private static unsafe D3D11Device CreateCaptureDevice()
    {
        var gameDevicePtr = (IntPtr)GameDevice.Instance()->D3D11Forwarder;
        if (gameDevicePtr == IntPtr.Zero)
            return new D3D11Device(DriverType.Hardware, DeviceCreationFlags.BgraSupport);

        Marshal.AddRef(gameDevicePtr);
        using var gameDevice = new D3D11Device(gameDevicePtr);
        using var gameDxgiDevice = gameDevice.QueryInterface<DxgiDevice>();
        using var adapter = gameDxgiDevice.Adapter;
        return new D3D11Device(adapter, DeviceCreationFlags.BgraSupport);
    }

    private static IntPtr CreateCaptureItem(IntPtr hwnd)
    {
        var factory = GetActivationFactory("Windows.Graphics.Capture.GraphicsCaptureItem", GraphicsCaptureItemInteropGuid);
        try
        {
            return CreateCaptureItemForWindow(factory, hwnd);
        }
        finally
        {
            Release(ref factory);
        }
    }

    private static SizeInt32 GetCaptureSize(IntPtr captureItem, int width, int height)
    {
        var size = GetCaptureItemSize(captureItem);
        if (size.Width > 0 && size.Height > 0)
            return size;

        return new SizeInt32(Math.Max(1, width), Math.Max(1, height));
    }

    private static IntPtr CreateFramePool(IntPtr direct3DDevice, SizeInt32 size)
    {
        var statics = GetActivationFactory(
            "Windows.Graphics.Capture.Direct3D11CaptureFramePool",
            Direct3D11CaptureFramePoolStatics2Guid);
        try
        {
            return CreateFreeThreadedFramePool(statics, direct3DDevice, size);
        }
        finally
        {
            Release(ref statics);
        }
    }

    private static bool IsGraphicsCaptureSupported()
    {
        var statics = GetActivationFactory(
            "Windows.Graphics.Capture.GraphicsCaptureSession",
            GraphicsCaptureSessionStaticsGuid);
        try
        {
            return GetGraphicsCaptureIsSupported(statics);
        }
        finally
        {
            Release(ref statics);
        }
    }

    private static D3D11Texture2D GetDirect3DTexture(IntPtr surface)
    {
        var access = IntPtr.Zero;
        var accessGuid = Direct3DDxgiInterfaceAccessGuid;
        Marshal.ThrowExceptionForHR(Marshal.QueryInterface(surface, in accessGuid, out access));
        try
        {
            var texture = GetDxgiInterface(access, Direct3D11Texture2DGuid);
            return new D3D11Texture2D(texture);
        }
        finally
        {
            Release(ref access);
        }
    }

    private static void CloseAndRelease(ref IntPtr value)
    {
        var current = value;
        value = IntPtr.Zero;
        if (current == IntPtr.Zero)
            return;

        TryClose(current);
        Release(current);
    }

    private static void TryClose(IntPtr value)
    {
        var closable = IntPtr.Zero;
        var closableGuid = ClosableGuid;
        if (Marshal.QueryInterface(value, in closableGuid, out closable) != 0)
            return;

        try
        {
            Close(closable);
        }
        finally
        {
            Release(ref closable);
        }
    }

    private static void Release(ref IntPtr value)
    {
        var current = value;
        value = IntPtr.Zero;
        Release(current);
    }

    private static unsafe void Release(IntPtr value)
    {
        if (value == IntPtr.Zero)
            return;

        var vtable = *(IntPtr**)value;
        var release = (delegate* unmanaged[Stdcall]<IntPtr, uint>)vtable[2];
        release(value);
    }

    private static IntPtr GetActivationFactory(string runtimeClassName, Guid factoryGuid)
    {
        var className = IntPtr.Zero;
        try
        {
            Marshal.ThrowExceptionForHR(WindowsCreateString(runtimeClassName, runtimeClassName.Length, out className));
            Marshal.ThrowExceptionForHR(RoGetActivationFactory(className, ref factoryGuid, out var factory));
            return factory;
        }
        finally
        {
            if (className != IntPtr.Zero)
                WindowsDeleteString(className);
        }
    }

    private static unsafe bool GetGraphicsCaptureIsSupported(IntPtr statics)
    {
        var supported = (byte)0;
        var vtable = *(IntPtr**)statics;
        var isSupported = (delegate* unmanaged[Stdcall]<IntPtr, byte*, int>)vtable[6];
        Marshal.ThrowExceptionForHR(isSupported(statics, &supported));
        return supported != 0;
    }

    private static unsafe IntPtr CreateCaptureItemForWindow(IntPtr interop, IntPtr hwnd)
    {
        var itemGuid = GraphicsCaptureItemGuid;
        var item = IntPtr.Zero;
        var vtable = *(IntPtr**)interop;
        var createForWindow = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, Guid*, IntPtr*, int>)vtable[3];
        Marshal.ThrowExceptionForHR(createForWindow(interop, hwnd, &itemGuid, &item));
        return item;
    }

    private static unsafe SizeInt32 GetCaptureItemSize(IntPtr captureItem)
    {
        var size = default(SizeInt32);
        var vtable = *(IntPtr**)captureItem;
        var getSize = (delegate* unmanaged[Stdcall]<IntPtr, SizeInt32*, int>)vtable[7];
        Marshal.ThrowExceptionForHR(getSize(captureItem, &size));
        return size;
    }

    private static unsafe IntPtr CreateFreeThreadedFramePool(IntPtr statics, IntPtr direct3DDevice, SizeInt32 size)
    {
        var framePool = IntPtr.Zero;
        var vtable = *(IntPtr**)statics;
        var createFreeThreaded = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int, int, SizeInt32, IntPtr*, int>)vtable[6];
        Marshal.ThrowExceptionForHR(createFreeThreaded(
            statics,
            direct3DDevice,
            DirectXPixelFormatB8G8R8A8UIntNormalized,
            FramePoolBufferCount,
            size,
            &framePool));
        return framePool;
    }

    private static unsafe void RecreateFramePool(IntPtr currentFramePool, IntPtr direct3DDevice, SizeInt32 size)
    {
        var vtable = *(IntPtr**)currentFramePool;
        var recreate = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int, int, SizeInt32, int>)vtable[6];
        Marshal.ThrowExceptionForHR(recreate(
            currentFramePool,
            direct3DDevice,
            DirectXPixelFormatB8G8R8A8UIntNormalized,
            FramePoolBufferCount,
            size));
    }

    private static unsafe IntPtr TryGetNextFrame(IntPtr currentFramePool)
    {
        var frame = IntPtr.Zero;
        var vtable = *(IntPtr**)currentFramePool;
        var tryGetNextFrame = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)vtable[7];
        Marshal.ThrowExceptionForHR(tryGetNextFrame(currentFramePool, &frame));
        return frame;
    }

    private static unsafe IntPtr CreateCaptureSession(IntPtr currentFramePool, IntPtr captureItem)
    {
        var captureSession = IntPtr.Zero;
        var vtable = *(IntPtr**)currentFramePool;
        var createCaptureSession = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr*, int>)vtable[10];
        Marshal.ThrowExceptionForHR(createCaptureSession(currentFramePool, captureItem, &captureSession));
        return captureSession;
    }

    private static unsafe void StartCapture(IntPtr captureSession)
    {
        var vtable = *(IntPtr**)captureSession;
        var startCapture = (delegate* unmanaged[Stdcall]<IntPtr, int>)vtable[6];
        Marshal.ThrowExceptionForHR(startCapture(captureSession));
    }

    private static unsafe void DisableCursorCapture(IntPtr captureSession)
    {
        var session2 = IntPtr.Zero;
        var session2Guid = GraphicsCaptureSession2Guid;
        if (Marshal.QueryInterface(captureSession, in session2Guid, out session2) != 0)
            return;

        try
        {
            var vtable = *(IntPtr**)session2;
            var putIsCursorCaptureEnabled = (delegate* unmanaged[Stdcall]<IntPtr, byte, int>)vtable[7];
            Marshal.ThrowExceptionForHR(putIsCursorCaptureEnabled(session2, 0));
        }
        finally
        {
            Release(ref session2);
        }
    }

    private static unsafe IntPtr GetFrameSurface(IntPtr frame)
    {
        var surface = IntPtr.Zero;
        var vtable = *(IntPtr**)frame;
        var getSurface = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)vtable[6];
        Marshal.ThrowExceptionForHR(getSurface(frame, &surface));
        return surface;
    }

    private static unsafe SizeInt32 GetFrameContentSize(IntPtr frame)
    {
        var size = default(SizeInt32);
        var vtable = *(IntPtr**)frame;
        var getContentSize = (delegate* unmanaged[Stdcall]<IntPtr, SizeInt32*, int>)vtable[8];
        Marshal.ThrowExceptionForHR(getContentSize(frame, &size));
        return size;
    }

    private static unsafe IntPtr GetDxgiInterface(IntPtr access, Guid interfaceGuid)
    {
        var currentInterfaceGuid = interfaceGuid;
        var texture = IntPtr.Zero;
        var vtable = *(IntPtr**)access;
        var getInterface = (delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)vtable[3];
        Marshal.ThrowExceptionForHR(getInterface(access, &currentInterfaceGuid, &texture));
        return texture;
    }

    private static unsafe void Close(IntPtr closable)
    {
        var vtable = *(IntPtr**)closable;
        var close = (delegate* unmanaged[Stdcall]<IntPtr, int>)vtable[6];
        Marshal.ThrowExceptionForHR(close(closable));
    }

    [DllImport("combase.dll", ExactSpelling = true)]
    private static extern int RoInitialize(int initType);

    [DllImport("combase.dll", ExactSpelling = true)]
    private static extern void RoUninitialize();

    [DllImport("combase.dll", ExactSpelling = true)]
    private static extern int WindowsCreateString(
        [MarshalAs(UnmanagedType.LPWStr)] string sourceString,
        int length,
        out IntPtr hstring);

    [DllImport("combase.dll", ExactSpelling = true)]
    private static extern int WindowsDeleteString(IntPtr hstring);

    [DllImport("combase.dll", ExactSpelling = true)]
    private static extern int RoGetActivationFactory(IntPtr activatableClassId, ref Guid iid, out IntPtr factory);

    [DllImport("d3d11.dll", ExactSpelling = true, SetLastError = true)]
    private static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct SizeInt32
    {
        public SizeInt32(int width, int height)
        {
            Width = width;
            Height = height;
        }

        public readonly int Width;
        public readonly int Height;
    }
}
