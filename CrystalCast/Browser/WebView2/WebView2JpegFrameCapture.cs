using Microsoft.Web.WebView2.Core;

namespace CrystalCast.Video;

internal sealed class WebView2JpegFrameCapture : IDisposable
{
    private readonly MemoryStream stream = new();
    private readonly WicBgraDecoder decoder = new();

    public async Task<VideoFrame?> CaptureAsync(CoreWebView2 webView, long sequence)
    {
        stream.Position = 0;
        stream.SetLength(0);
        await webView.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Jpeg, stream);
        if (stream.Length == 0)
            return null;

        var encoded = stream.TryGetBuffer(out var buffer)
            ? buffer.AsSpan(0, (int)stream.Length)
            : stream.ToArray();
        return decoder.Decode(encoded, sequence, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }

    public void Dispose()
    {
        decoder.Dispose();
        stream.Dispose();
    }
}
