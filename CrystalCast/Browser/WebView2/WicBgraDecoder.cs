using CrystalCast.Video;
using System.Runtime.InteropServices;

namespace CrystalCast.Browser.WebView2;

/// <summary>
/// Decodes JPEG (and other WIC-supported) images into 32-bit BGRA frames with the Windows
/// Imaging Component, which Windows and Wine both provide. Calls go straight through the COM
/// vtables, so no image library or COM interop package is needed.
/// </summary>
internal sealed unsafe class WicBgraDecoder : IDisposable
{
    private const int CoENotInitialized = unchecked((int)0x800401F0);
    private const int RpcEChangedMode = unchecked((int)0x80010106);
    private const uint ClsctxInprocServer = 0x1;
    private const uint CoinitMultithreaded = 0x0;
    private const int MaximumDimension = 16384;

    private static readonly Guid ClsidWicImagingFactory = new("cacaf262-9370-4615-a13b-9f5539da4c0a");
    private static readonly Guid IidWicImagingFactory = new("ec5ec8a9-c395-4314-9c77-54d7a935ff70");
    private static readonly Guid PixelFormat32bppBgra = new("6fddc324-4e03-4bfe-b185-3d77768dc90f");

    private nint factory;

    public VideoFrame Decode(ReadOnlySpan<byte> encoded, long sequence, long timestampUnixMs)
    {
        if (encoded.IsEmpty)
            throw new InvalidDataException("The image is empty.");

        var wicFactory = GetFactory();
        nint stream = 0, decoder = 0, frame = 0, converter = 0;
        try
        {
            fixed (byte* data = encoded)
            {
                // IWICImagingFactory::CreateStream, IWICStream::InitializeFromMemory
                Check(CallOut(wicFactory, 14, &stream), "create an image stream");
                Check(((delegate* unmanaged[Stdcall]<nint, byte*, uint, int>)VTable(stream, 16))(stream, data, (uint)encoded.Length), "read the image");

                // IWICImagingFactory::CreateDecoderFromStream(stream, no vendor, cache on demand)
                Check(((delegate* unmanaged[Stdcall]<nint, nint, Guid*, int, nint*, int>)VTable(wicFactory, 4))(wicFactory, stream, null, 0, &decoder), "recognize the image format");
                // IWICBitmapDecoder::GetFrame(0)
                Check(((delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)VTable(decoder, 13))(decoder, 0, &frame), "read the first image frame");

                // IWICImagingFactory::CreateFormatConverter, IWICFormatConverter::Initialize(BGRA)
                Check(CallOut(wicFactory, 10, &converter), "create a pixel converter");
                var format = PixelFormat32bppBgra;
                Check(((delegate* unmanaged[Stdcall]<nint, nint, Guid*, int, nint, double, int, int>)VTable(converter, 8))(converter, frame, &format, 0, 0, 0.0, 0), "convert the image to BGRA");

                // IWICBitmapSource::GetSize
                uint width, height;
                Check(((delegate* unmanaged[Stdcall]<nint, uint*, uint*, int>)VTable(converter, 3))(converter, &width, &height), "read the image size");
                if (width == 0 || height == 0 || width > MaximumDimension || height > MaximumDimension)
                    throw new InvalidDataException($"Unsupported image size {width} x {height}.");

                var result = VideoFrame.Rent((int)width, (int)height, sequence, timestampUnixMs);
                try
                {
                    fixed (byte* pixels = result.Pixels)
                    {
                        // IWICBitmapSource::CopyPixels(whole image, tightly packed rows)
                        Check(((delegate* unmanaged[Stdcall]<nint, void*, uint, uint, byte*, int>)VTable(converter, 7))(converter, null, width * 4, (uint)result.PixelLength, pixels), "copy the decoded pixels");
                    }

                    return result;
                }
                catch
                {
                    result.Dispose();
                    throw;
                }
            }
        }
        finally
        {
            Release(converter);
            Release(frame);
            Release(decoder);
            Release(stream);
        }
    }

    public void Dispose()
    {
        Release(factory);
        factory = 0;
    }

    private nint GetFactory()
    {
        if (factory != 0)
            return factory;

        var clsid = ClsidWicImagingFactory;
        var iid = IidWicImagingFactory;
        nint created;
        var result = CoCreateInstance(&clsid, 0, ClsctxInprocServer, &iid, &created);
        if (result == CoENotInitialized)
        {
            // Capture normally runs on WebView2's STA thread, where COM is already initialized.
            var init = CoInitializeEx(0, CoinitMultithreaded);
            if (init < 0 && init != RpcEChangedMode)
                Check(init, "initialize COM");
            result = CoCreateInstance(&clsid, 0, ClsctxInprocServer, &iid, &created);
        }

        Check(result, "start the Windows image decoder (WIC)");
        factory = created;
        return factory;
    }

    /// <summary>Calls a COM method whose only argument receives a new interface pointer.</summary>
    private static int CallOut(nint instance, int slot, nint* result)
    {
        return ((delegate* unmanaged[Stdcall]<nint, nint*, int>)VTable(instance, slot))(instance, result);
    }

    private static void* VTable(nint instance, int slot) => (*(void***)instance)[slot];

    private static void Release(nint instance)
    {
        if (instance != 0)
            ((delegate* unmanaged[Stdcall]<nint, uint>)VTable(instance, 2))(instance);
    }

    private static void Check(int hresult, string action)
    {
        if (hresult < 0)
            throw new InvalidDataException($"Could not {action} (HRESULT 0x{hresult:X8}).");
    }

    [DllImport("ole32.dll")]
    private static extern int CoCreateInstance(Guid* clsid, nint outer, uint context, Guid* iid, nint* instance);

    [DllImport("ole32.dll")]
    private static extern int CoInitializeEx(nint reserved, uint coInit);
}
