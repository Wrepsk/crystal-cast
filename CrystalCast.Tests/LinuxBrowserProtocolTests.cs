using System.Buffers.Binary;
using System.Text.Json;
using CrystalCast.Video;

namespace CrystalCast.Tests;

public sealed class LinuxBrowserProtocolTests
{
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, true)]
    public void NativeBackendRequiresWineAndTheEnabledSetting(bool wine, bool enabled, bool expected)
        => Assert.Equal(expected, BrowserPlatformPolicy.UseNativeLinuxBrowser(wine, enabled));

    [Fact]
    public void ExistingConfigurationDoesNotSwitchBrowserBackend()
    {
        var config = JsonSerializer.Deserialize<Configuration>("{\"Version\":3,\"EnableNativeLinuxBrowser\":false}")!;
        config.Normalize();
        Assert.False(config.EnableNativeLinuxBrowser);
    }

    [Theory]
    [InlineData("/home/player/.local/bin/crystalcast-browser-helper", "Z:\\home\\player\\.local\\bin\\crystalcast-browser-helper")]
    [InlineData("/run/user/1000", "Z:\\run\\user\\1000")]
    public void NativePathsUseTheWineRootDrive(string native, string windows)
        => Assert.Equal(windows, LinuxBrowserLauncher.ToWinePath(native));

    [Theory]
    [InlineData("relative/path")]
    [InlineData("/tmp/../private")]
    [InlineData("/tmp/./private")]
    [InlineData("/tmp\\private")]
    public void NativePathsRejectTraversalAndAmbiguousSeparators(string path)
        => Assert.Throws<InvalidDataException>(() => LinuxBrowserLauncher.ToWinePath(path));

    [Theory]
    [InlineData(0, 1, 4, 1)]
    [InlineData(1921, 1, 7684, 1)]
    [InlineData(1, 1081, 4, 1)]
    [InlineData(1, 1, 8, 1)]
    [InlineData(1, 1, 4, 0)]
    public void FrameLayoutsAreBoundedBeforeAllocatingPixels(int width, int height, int stride, long sequence)
        => Assert.Throws<InvalidDataException>(() => LinuxBrowserProtocol.ValidateFrame(width, height, stride, sequence));

    [Fact]
    public async Task FramingPreservesJsonAcrossPartialReads()
    {
        using var buffer = new MemoryStream();
        await LinuxBrowserProtocol.WriteJsonAsync(buffer, "{\"type\":\"ping\"}", CancellationToken.None);
        using var partial = new PartialStream(buffer.ToArray());
        var header = await LinuxBrowserProtocol.ReadHeaderAsync(partial, CancellationToken.None);
        Assert.Equal(1, header.Kind);
        using var json = await LinuxBrowserProtocol.ReadJsonAsync(partial, header.Length, CancellationToken.None);
        Assert.Equal("ping", json.RootElement.GetProperty("type").GetString());
    }

    [Theory]
    [InlineData(0, 1, 10, 0)]
    [InlineData(LinuxBrowserProtocol.Magic, 3, 10, 0)]
    [InlineData(LinuxBrowserProtocol.Magic, 1, -1, 0)]
    [InlineData(LinuxBrowserProtocol.Magic, 1, LinuxBrowserProtocol.MaximumJsonBytes + 1, 0)]
    [InlineData(LinuxBrowserProtocol.Magic, 2, LinuxBrowserProtocol.SlotSize + 33, 0)]
    [InlineData(LinuxBrowserProtocol.Magic, 1, 10, 1)]
    public async Task InvalidHeadersCannotTriggerLargeAllocations(uint magic, int kind, int length, int reserved)
    {
        var header = new byte[16];
        BinaryPrimitives.WriteUInt32LittleEndian(header, magic);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(4), kind);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(8), length);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(12), reserved);
        using var stream = new MemoryStream(header);
        await Assert.ThrowsAsync<InvalidDataException>(() => LinuxBrowserProtocol.ReadHeaderAsync(stream, CancellationToken.None));
    }

    [Fact]
    public async Task RawFramesKeepBgraPixelsAndMonotonicSequenceAfterReconnect()
    {
        var payload = FramePayload();
        using var stream = new PartialStream(payload);
        using var frame = await LinuxBrowserProtocol.ReadFrameAsync(stream, payload.Length, CancellationToken.None, sequenceBase: 20);
        Assert.Equal(21, frame.Sequence);
        Assert.Equal(2, frame.Width);
        Assert.Equal(1, frame.Height);
        Assert.Equal(new byte[] { 0, 0, 255, 255, 255, 0, 0, 255 }, frame.Pixels[..frame.PixelLength]);
    }

    [Fact]
    public async Task TruncatedRawFrameIsRejected()
    {
        var payload = FramePayload();
        using var stream = new MemoryStream(payload[..^1]);
        await Assert.ThrowsAsync<EndOfStreamException>(() => LinuxBrowserProtocol.ReadFrameAsync(stream, payload.Length, CancellationToken.None));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(3)]
    public void SharedFrameSlotCannotEscapeTheMapping(int slot)
    {
        var path = Path.GetTempFileName();
        try
        {
            using (var file = File.OpenWrite(path)) file.SetLength(LinuxBrowserProtocol.SlotSize * LinuxBrowserProtocol.SlotCount);
            using var mapping = new LinuxBrowserFrameMapping(path);
            using var message = JsonDocument.Parse(JsonSerializer.Serialize(new { width = 2, height = 1, stride = 8, sequence = 1, timestamp = 1, slot }));
            Assert.Throws<InvalidDataException>(() => mapping.Read(message.RootElement));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void SharedFrameCopiesPixelsBeforeTheProducerCanReuseItsLease()
    {
        var path = Path.GetTempFileName();
        try
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
            file.SetLength(LinuxBrowserProtocol.SlotSize * LinuxBrowserProtocol.SlotCount);
            file.Position = LinuxBrowserProtocol.SlotSize;
            file.Write([1, 2, 3, 255, 4, 5, 6, 255]);
            file.Flush();
            using var mapping = new LinuxBrowserFrameMapping(path);
            using var message = JsonDocument.Parse("{\"width\":2,\"height\":1,\"stride\":8,\"sequence\":1,\"timestamp\":1,\"slot\":1}");
            using var frame = mapping.Read(message.RootElement);
            file.Position = LinuxBrowserProtocol.SlotSize;
            file.Write(new byte[8]);
            file.Flush();
            Assert.Equal(new byte[] { 1, 2, 3, 255, 4, 5, 6, 255 }, frame.Pixels[..frame.PixelLength]);
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData(BrowserSourceProviderKind.YouTube, "https://www.youtube.com/watch?v=dQw4w9WgXcQ")]
    [InlineData(BrowserSourceProviderKind.Twitch, "https://www.twitch.tv/monstercat")]
    [InlineData(BrowserSourceProviderKind.Dailymotion, "https://www.dailymotion.com/video/x84sh87")]
    [InlineData(BrowserSourceProviderKind.Vimeo, "https://vimeo.com/76979871")]
    public void NativeBackendReusesExistingProviderDocuments(BrowserSourceProviderKind kind, string url)
    {
        var descriptor = BrowserSourceDescriptors.Get(kind);
        var request = new BrowserFrameSourceRequest(kind, descriptor, url, 1280, 720, 60,
            BrowserMediaEngine.Auto, true, false, false, false, .7f, 1);
        using var source = new LinuxBrowserFrameSource(request);
        using var message = JsonDocument.Parse(source.BuildLoadMessage());
        var root = message.RootElement;
        Assert.False(root.GetProperty("generic").GetBoolean());
        Assert.StartsWith(descriptor.PlayerOrigin + "/player/", root.GetProperty("uri").GetString());
        Assert.Contains("crystalCastHostBridgeInstalled", root.GetProperty("html").GetString());
        Assert.Contains("expectedNonce", root.GetProperty("html").GetString());
    }

    internal static byte[] FramePayload()
    {
        var payload = new byte[40];
        BinaryPrimitives.WriteInt32LittleEndian(payload, 2);
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(4), 1);
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(8), 8);
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(12), 1);
        BinaryPrimitives.WriteInt64LittleEndian(payload.AsSpan(16), 1);
        BinaryPrimitives.WriteInt64LittleEndian(payload.AsSpan(24), 123);
        new byte[] { 0, 0, 255, 255, 255, 0, 0, 255 }.CopyTo(payload, 32);
        return payload;
    }

    private sealed class PartialStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => base.ReadAsync(buffer[..Math.Min(buffer.Length, 3)], cancellationToken);
    }
}
