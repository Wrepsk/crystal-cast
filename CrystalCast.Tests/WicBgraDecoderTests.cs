using CrystalCast.Video;

namespace CrystalCast.Tests;

public sealed class WicBgraDecoderTests
{
    // 64x32 JPEG: left half pure red, right half pure blue (quality 95).
    private const string RedBlueJpeg =
        "/9j/4AAQSkZJRgABAQEAYABgAAD/2wBDAAIBAQIBAQICAgICAgICAwUDAwMDAwYEBAMFBwYHBwcGBwcICQsJCAgKCAcHCg0KCgsMDAwMBwkODw0MDgsMDAz/2wBDAQICAgMDAwYDAwYMCAcIDAwMDAwMDAwMDAwMDAwMDAwMDAwMDAwMDAwMDAwMDAwMDAwMDAwMDAwMDAwMDAwMDAz/wAARCAAgAEADASIAAhEBAxEB/8QAHwAAAQUBAQEBAQEAAAAAAAAAAAECAwQFBgcICQoL/8QAtRAAAgEDAwIEAwUFBAQAAAF9AQIDAAQRBRIhMUEGE1FhByJxFDKBkaEII0KxwRVS0fAkM2JyggkKFhcYGRolJicoKSo0NTY3ODk6Q0RFRkdISUpTVFVWV1hZWmNkZWZnaGlqc3R1dnd4eXqDhIWGh4iJipKTlJWWl5iZmqKjpKWmp6ipqrKztLW2t7i5usLDxMXGx8jJytLT1NXW19jZ2uHi4+Tl5ufo6erx8vP09fb3+Pn6/8QAHwEAAwEBAQEBAQEBAQAAAAAAAAECAwQFBgcICQoL/8QAtREAAgECBAQDBAcFBAQAAQJ3AAECAxEEBSExBhJBUQdhcRMiMoEIFEKRobHBCSMzUvAVYnLRChYkNOEl8RcYGRomJygpKjU2Nzg5OkNERUZHSElKU1RVVldYWVpjZGVmZ2hpanN0dXZ3eHl6goOEhYaHiImKkpOUlZaXmJmaoqOkpaanqKmqsrO0tba3uLm6wsPExcbHyMnK0tPU1dbX2Nna4uPk5ebn6Onq8vP09fb3+Pn6/9oADAMBAAIRAxEAPwD4vooor+Uz/fwKKKKAPkOiiiv+qg/5rwooooA+vKKKK/5Vz/pQCiiigD5Dooor/qoP+a8KKKKAP//Z";

    [Fact]
    public void DecodesJpegIntoBgraPixels()
    {
        using var decoder = new WicBgraDecoder();

        using var frame = decoder.Decode(Convert.FromBase64String(RedBlueJpeg), sequence: 7, timestampUnixMs: 123);

        Assert.Equal(64, frame.Width);
        Assert.Equal(32, frame.Height);
        Assert.Equal(64 * 32 * 4, frame.PixelLength);
        Assert.Equal(7, frame.Sequence);
        AssertPixel(frame, 8, 16, blue: 0, green: 0, red: 255);
        AssertPixel(frame, 56, 16, blue: 255, green: 0, red: 0);
    }

    [Fact]
    public void DecoderCanBeReused()
    {
        using var decoder = new WicBgraDecoder();
        var jpeg = Convert.FromBase64String(RedBlueJpeg);

        using var first = decoder.Decode(jpeg, 1, 0);
        using var second = decoder.Decode(jpeg, 2, 0);

        Assert.Equal(first.Pixels.AsSpan(0, first.PixelLength).ToArray(), second.Pixels.AsSpan(0, second.PixelLength).ToArray());
    }

    [Fact]
    public void InvalidDataIsRejectedWithAClearError()
    {
        using var decoder = new WicBgraDecoder();

        var error = Assert.Throws<InvalidDataException>(() => decoder.Decode(new byte[] { 1, 2, 3, 4 }, 1, 0));
        Assert.Contains("image format", error.Message, StringComparison.Ordinal);
    }

    private static void AssertPixel(VideoFrame frame, int x, int y, int blue, int green, int red)
    {
        var offset = ((y * frame.Width) + x) * 4;
        Assert.InRange(frame.Pixels[offset], blue - 12, blue + 12);
        Assert.InRange(frame.Pixels[offset + 1], green - 12, green + 12);
        Assert.InRange(frame.Pixels[offset + 2], red - 12, red + 12);
        Assert.Equal(255, frame.Pixels[offset + 3]);
    }
}
