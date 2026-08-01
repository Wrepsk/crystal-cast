using Microsoft.Web.WebView2.Core;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace CrystalCast.Video;

internal sealed class WebView2JpegFrameCapture : IDisposable
{
    private readonly MemoryStream stream = new();

    public async Task<VideoFrame?> CaptureAsync(CoreWebView2 webView, long sequence)
    {
        stream.Position = 0;
        stream.SetLength(0);
        await webView.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Jpeg, stream);
        if (stream.Length == 0)
            return null;

        stream.Position = 0;
        using var image = Image.Load<Bgra32>(stream);
        var frame = VideoFrame.Rent(
            image.Width,
            image.Height,
            sequence,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        try
        {
            image.CopyPixelDataTo(frame.Pixels.AsSpan(0, frame.PixelLength));
            return frame;
        }
        catch
        {
            frame.Dispose();
            throw;
        }
    }

    public void Dispose() => stream.Dispose();
}
