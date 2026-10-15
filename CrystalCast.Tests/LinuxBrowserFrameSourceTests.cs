using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.RegularExpressions;
using CrystalCast.Video;

namespace CrystalCast.Tests;

public sealed class LinuxBrowserFrameSourceTests
{
    [Fact]
    public async Task AuthenticatedSocketFeedsTheExistingFrameSourceAndPlaybackInterfaces()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var temporary = Path.Combine(Path.GetTempPath(), "CrystalCastNativeTest-" + Guid.NewGuid().ToString("N"));
        var request = new BrowserFrameSourceRequest(BrowserSourceProviderKind.GenericWeb, BrowserSourceDescriptors.GenericWeb,
            "https://example.org/video", 1280, 720, 60, BrowserMediaEngine.Auto, true, false, false, false, .7f, 1);
        using var source = new LinuxBrowserFrameSource(request,
            new TestLauncher(new LinuxBrowserEndpoint(port, new string('a', 64), temporary)), preferSharedFrames: false);
        var commands = new List<JsonElement>();
        var controlsReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = Task.Run(async () =>
        {
            using var peer = await listener.AcceptTcpClientAsync(deadline.Token);
            var stream = peer.GetStream();
            using var hello = await ReadMessage(stream, deadline.Token);
            Assert.Equal(new string('a', 64), hello.RootElement.GetProperty("token").GetString());
            await LinuxBrowserProtocol.WriteJsonAsync(stream,
                "{\"type\":\"hello\",\"version\":1,\"transport\":\"tcp\",\"slotSize\":8294400,\"slots\":3}", deadline.Token);
            using var load = await ReadMessage(stream, deadline.Token);
            Assert.Equal("load", load.RootElement.GetProperty("type").GetString());
            var script = load.RootElement.GetProperty("script").GetString()!;
            var nonce = Regex.Match(script, "const expectedNonce = \"([0-9a-f]+)\"").Groups[1].Value;
            Assert.Equal(32, nonce.Length);
            async Task BrowserMessage(object message) => await LinuxBrowserProtocol.WriteJsonAsync(stream,
                JsonSerializer.Serialize(new { type = "message", message = JsonSerializer.Serialize(message) }), deadline.Token);
            await BrowserMessage(new { type = "ready", nonce });
            await BrowserMessage(new { type = "status", nonce, state = 1, positionSeconds = 3.5, durationSeconds = 8, rate = 1, title = "fixture" });
            // A forged page message must not replace authenticated telemetry.
            await BrowserMessage(new { type = "status", nonce = "forged", state = 2, positionSeconds = 0, durationSeconds = 0 });
            var payload = LinuxBrowserProtocolTests.FramePayload();
            var header = new byte[16];
            BinaryPrimitives.WriteUInt32LittleEndian(header, LinuxBrowserProtocol.Magic);
            BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(4), 2);
            BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(8), payload.Length);
            await stream.WriteAsync(header, deadline.Token);
            await stream.WriteAsync(payload, deadline.Token);
            try
            {
                while (true)
                {
                    using var message = await ReadMessage(stream, deadline.Token);
                    if (message.RootElement.GetProperty("type").GetString() != "command") continue;
                    using var command = JsonDocument.Parse(message.RootElement.GetProperty("message").GetString()!);
                    lock (commands)
                    {
                        commands.Add(command.RootElement.Clone());
                        if (commands.Any(c => c.GetProperty("type").GetString() == "seekTo")) controlsReceived.TrySetResult();
                    }
                }
            }
            catch (EndOfStreamException) { disconnected.TrySetResult(); }
        }, deadline.Token);

        source.Start();
        await WaitUntil(() =>
        {
            if (!source.TryGetLatestFrame(out var frame)) return false;
            using (frame) return frame.Sequence == 1;
        }, deadline.Token);
        Assert.True(source.TryGetLatestFrame(out var received));
        using (received)
            Assert.Equal(new byte[] { 0, 0, 255, 255, 255, 0, 0, 255 }, received.Pixels[..received.PixelLength]);
        Assert.True(source.TryGetPlaybackTelemetry(out var telemetry));
        Assert.Equal(ScreenPlaybackState.Playing, telemetry.State);
        Assert.Equal(3500, telemetry.PositionMs);
        Assert.Equal("fixture", telemetry.Title);
        source.Pause();
        source.Start(); // Capture resumes without overriding the user's pause intent.
        source.Play();
        source.Stop(); // Temporary suspension must pause the browser as well as pixels.
        source.Start(); // Unlike Pause, Stop preserves the earlier play intent.
        source.SeekTo(1);
        await controlsReceived.Task.WaitAsync(deadline.Token);
        lock (commands)
        {
            Assert.Contains(commands, c => c.GetProperty("type").GetString() == "pause");
            Assert.True(commands.Count(c => c.GetProperty("type").GetString() == "play") >= 2);
            Assert.True(commands.Count(c => c.GetProperty("type").GetString() == "pause") >= 2);
            Assert.Contains(commands, c => c.GetProperty("type").GetString() == "settings"
                && !c.GetProperty("settings").GetProperty("audioEnabled").GetBoolean());
        }
        source.Dispose();
        await disconnected.Task.WaitAsync(deadline.Token);
        await server.WaitAsync(deadline.Token);
        Assert.False(source.IsRunning);
        Assert.False(source.TryGetLatestFrame(out _));
    }

    [Fact]
    public void NativeBackendFailureDoesNotInstantiateWebView2OrBlockTheCaller()
    {
        var request = new BrowserFrameSourceRequest(BrowserSourceProviderKind.GenericWeb, BrowserSourceDescriptors.GenericWeb,
            "https://example.org", 1280, 720, 60, BrowserMediaEngine.Auto, true, false, false, false, .7f, 1);
        using var source = new LinuxBrowserFrameSource(request, new FailingLauncher());
        source.Start();
        source.Dispose();
        Assert.False(source.IsRunning);
    }

    private static async Task<JsonDocument> ReadMessage(Stream stream, CancellationToken cancellationToken)
    {
        var header = await LinuxBrowserProtocol.ReadHeaderAsync(stream, cancellationToken);
        Assert.Equal(1, header.Kind);
        return await LinuxBrowserProtocol.ReadJsonAsync(stream, header.Length, cancellationToken);
    }

    private static async Task WaitUntil(Func<bool> condition, CancellationToken cancellationToken)
    {
        while (!condition()) await Task.Delay(10, cancellationToken);
    }

    private sealed class TestLauncher(LinuxBrowserEndpoint endpoint) : ILinuxBrowserLauncher
    {
        public Task<LinuxBrowserEndpoint> LaunchAsync(CancellationToken cancellationToken) => Task.FromResult(endpoint);
    }

    private sealed class FailingLauncher : ILinuxBrowserLauncher
    {
        public Task<LinuxBrowserEndpoint> LaunchAsync(CancellationToken cancellationToken)
            => Task.FromException<LinuxBrowserEndpoint>(new FileNotFoundException("helper is not installed"));
    }
}
