using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using CrystalCast.Video;

namespace CrystalCast.Tests;

public sealed class LinuxBrowserAvailabilityProbeTests
{
    [Theory]
    [InlineData(54, true, true)]
    [InlineData(54, false, true)]
    [InlineData(53, true, false)]
    public async Task ProbeAuthenticatesChecksTheInitializedEngineAndDisconnectsWithoutLoadingAPage(
        int minorVersion, bool codecsPresent, bool expectedAvailable)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var launcherHandle = new Handle();
        var server = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(deadline.Token);
            var stream = client.GetStream();
            using var hello = await ReadMessage(stream, deadline.Token);
            Assert.Equal("hello", hello.RootElement.GetProperty("type").GetString());
            Assert.Equal(new string('b', 64), hello.RootElement.GetProperty("token").GetString());
            await LinuxBrowserProtocol.WriteJsonAsync(stream,
                "{\"type\":\"hello\",\"version\":1,\"transport\":\"tcp\"}", deadline.Token);
            using var command = await ReadMessage(stream, deadline.Token);
            Assert.Equal("probe", command.RootElement.GetProperty("type").GetString());
            await LinuxBrowserProtocol.WriteJsonAsync(stream, JsonSerializer.Serialize(new
            {
                type = "probe", protocol = 1, major = 2, minor = minorVersion, micro = 1,
                h264Software = codecsPresent, aacSoftware = codecsPresent,
            }), deadline.Token);
            Assert.Equal(0, await stream.ReadAsync(new byte[1], deadline.Token));
        }, deadline.Token);
        var probe = new LinuxBrowserAvailabilityProbe(new Launcher(new(port, new string('b', 64), "unused", launcherHandle)));
        var result = await probe.CheckAsync(deadline.Token);
        Assert.Equal(expectedAvailable, result.Available);
        if (expectedAvailable && !codecsPresent) Assert.Contains("missing", result.Message);
        await server.WaitAsync(deadline.Token);
        Assert.True(launcherHandle.Disposed);
    }

    [Fact]
    public async Task MissingHelperProducesASetupReasonRatherThanAWebView2Fallback()
    {
        var result = await new LinuxBrowserAvailabilityProbe(new MissingLauncher()).CheckAsync(CancellationToken.None);
        Assert.False(result.Available);
        Assert.Contains("not installed", result.Message);
    }

    [Fact]
    public async Task CancellationDisconnectsAnUnresponsiveProbe()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var cancellation = new CancellationTokenSource();
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(deadline.Token);
            var stream = client.GetStream();
            using var hello = await ReadMessage(stream, deadline.Token);
            connected.SetResult();
            Assert.Equal(0, await stream.ReadAsync(new byte[1], deadline.Token));
        }, deadline.Token);
        var probe = new LinuxBrowserAvailabilityProbe(new Launcher(new(
            ((IPEndPoint)listener.LocalEndpoint).Port, new string('c', 64), "unused")));
        var checking = probe.CheckAsync(cancellation.Token);
        await connected.Task.WaitAsync(deadline.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => checking);
        await server.WaitAsync(deadline.Token);
    }

    private static async Task<JsonDocument> ReadMessage(Stream stream, CancellationToken cancellationToken)
    {
        var header = await LinuxBrowserProtocol.ReadHeaderAsync(stream, cancellationToken);
        return await LinuxBrowserProtocol.ReadJsonAsync(stream, header.Length, cancellationToken);
    }

    private sealed class Launcher(LinuxBrowserEndpoint endpoint) : ILinuxBrowserLauncher
    {
        public Task<LinuxBrowserEndpoint> LaunchAsync(CancellationToken cancellationToken) => Task.FromResult(endpoint);
    }
    private sealed class MissingLauncher : ILinuxBrowserLauncher
    {
        public Task<LinuxBrowserEndpoint> LaunchAsync(CancellationToken cancellationToken)
            => Task.FromException<LinuxBrowserEndpoint>(new FileNotFoundException("helper not installed"));
    }
    private sealed class Handle : IDisposable
    {
        public bool Disposed { get; private set; }
        public void Dispose() => Disposed = true;
    }
}
