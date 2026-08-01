using System.Diagnostics;
using System.Runtime.InteropServices;
using CrystalCast.Video;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Plugin.Services;
using SharpDX;
using SharpDX.Direct3D11;
using D3D11Device = SharpDX.Direct3D11.Device;

namespace CrystalCast.Rendering;

public sealed class DynamicVideoTexture : IDisposable
{
    private readonly ITextureProvider textureProvider;
    private IDalamudTextureWrap? wrap;
    private ShaderResourceView? shaderResourceView;
    private Texture2D? texture;
    private D3D11Device? device;
    private DeviceContext? context;
    private long uploadedSequence = -1;

    public DynamicVideoTexture(ITextureProvider textureProvider)
    {
        this.textureProvider = textureProvider;
    }

    public IDalamudTextureWrap? TextureWrap => wrap;
    public int Width { get; private set; }
    public int Height { get; private set; }
    public double LastUploadMilliseconds { get; private set; }
    public long UploadCount { get; private set; }
    public long UploadedSequence => uploadedSequence;

    public bool Upload(VideoFrame frame)
    {
        if (frame.Sequence == uploadedSequence)
            return false;

        if (frame.Pixels.Length != frame.Width * frame.Height * 4)
            return false;

        try
        {
            if (wrap == null || frame.Width != Width || frame.Height != Height)
                Recreate(frame.Width, frame.Height);

            if (texture == null || context == null)
                return false;

            var sw = Stopwatch.StartNew();
            var dataBox = context.MapSubresource(texture, 0, MapMode.WriteDiscard, MapFlags.None);
            try
            {
                CopyPixels(frame.Pixels, frame.Width, frame.Height, dataBox);
            }
            finally
            {
                context.UnmapSubresource(texture, 0);
            }

            sw.Stop();
            uploadedSequence = frame.Sequence;
            UploadCount++;
            LastUploadMilliseconds = sw.Elapsed.TotalMilliseconds;
            return true;
        }
        catch (Exception ex) when (NativeGraphicsError.IsDeviceLost(ex))
        {
            DisposeTexture();
            return false;
        }
    }

    public void Dispose()
    {
        DisposeTexture();
    }

    private void Recreate(int width, int height)
    {
        var specs = RawImageSpecification.Bgra32(width, height);
        IDalamudTextureWrap? nextWrap = null;
        ShaderResourceView? nextShaderResourceView = null;
        Texture2D? nextTexture = null;
        D3D11Device? nextDevice = null;
        DeviceContext? nextContext = null;
        var srvPointer = IntPtr.Zero;
        var referenceAdded = false;
        try
        {
            nextWrap = textureProvider.CreateEmpty(specs, cpuRead: false, cpuWrite: true, "CrystalCast dynamic video");
            srvPointer = (IntPtr)nextWrap.Handle.Handle;
            if (srvPointer == IntPtr.Zero)
                throw new InvalidOperationException("Dalamud returned an empty texture handle.");

            Marshal.AddRef(srvPointer);
            referenceAdded = true;
            nextShaderResourceView = new ShaderResourceView(srvPointer);
            referenceAdded = false;
            using var resource = nextShaderResourceView.Resource;
            nextTexture = resource.QueryInterface<Texture2D>();
            nextDevice = nextTexture.Device;
            nextContext = nextDevice.ImmediateContext;
        }
        catch
        {
            nextContext?.Dispose();
            nextDevice?.Dispose();
            nextTexture?.Dispose();
            nextShaderResourceView?.Dispose();
            if (referenceAdded)
                Marshal.Release(srvPointer);
            nextWrap?.Dispose();
            throw;
        }

        DisposeTexture();
        wrap = nextWrap;
        shaderResourceView = nextShaderResourceView;
        texture = nextTexture;
        device = nextDevice;
        context = nextContext;
        Width = width;
        Height = height;
        uploadedSequence = -1;
    }

    private void DisposeTexture()
    {
        context?.Dispose();
        context = null;
        device?.Dispose();
        device = null;
        texture?.Dispose();
        texture = null;
        shaderResourceView?.Dispose();
        shaderResourceView = null;
        wrap?.Dispose();
        wrap = null;
        Width = 0;
        Height = 0;
        uploadedSequence = -1;
    }

    private static unsafe void CopyPixels(byte[] pixels, int width, int height, DataBox dataBox)
    {
        var sourcePitch = width * 4;
        fixed (byte* sourceStart = pixels)
        {
            var destinationStart = (byte*)dataBox.DataPointer;
            for (var y = 0; y < height; y++)
            {
                var source = sourceStart + (y * sourcePitch);
                var destination = destinationStart + (y * dataBox.RowPitch);
                System.Buffer.MemoryCopy(source, destination, dataBox.RowPitch, sourcePitch);
            }
        }
    }
}
