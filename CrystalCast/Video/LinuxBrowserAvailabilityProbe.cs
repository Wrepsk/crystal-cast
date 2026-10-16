using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace CrystalCast.Video;

internal sealed record LinuxBrowserAvailability(bool Available, string Message);

// Exercise the same launcher and authenticated bridge as a screen, without
// contacting a website. A tiny in-memory page verifies child-process startup,
// JavaScript and actual frame delivery. Run off the Dalamud draw thread.
internal sealed class LinuxBrowserAvailabilityProbe(ILinuxBrowserLauncher? launcher = null)
{
    public async Task<LinuxBrowserAvailability> CheckAsync(CancellationToken cancellationToken)
    {
        LinuxBrowserEndpoint? endpoint = null;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(22));
        using var client = new TcpClient { NoDelay = true };
        try
        {
            endpoint = await (launcher ?? new LinuxBrowserLauncher()).LaunchAsync(deadline.Token).ConfigureAwait(false);
            await client.ConnectAsync(IPAddress.Loopback, endpoint.Port, deadline.Token).ConfigureAwait(false);
            var stream = client.GetStream();
            await LinuxBrowserProtocol.WriteJsonAsync(stream, JsonSerializer.Serialize(new
            {
                type = "hello", version = 1, token = endpoint.Token, transport = "tcp",
            }), deadline.Token).ConfigureAwait(false);
            using (var hello = await ReadMessage(stream, deadline.Token).ConfigureAwait(false))
            {
                var root = hello.RootElement;
                if (root.GetProperty("type").GetString() != "hello" || root.GetProperty("version").GetInt32() != 1
                    || root.GetProperty("transport").GetString() != "tcp")
                    throw new InvalidDataException("The installed native browser helper has an incompatible handshake.");
            }
            await LinuxBrowserProtocol.WriteJsonAsync(stream, "{\"type\":\"probe\"}", deadline.Token).ConfigureAwait(false);
            while (true)
            {
                using var message = await ReadMessage(stream, deadline.Token).ConfigureAwait(false);
                var root = message.RootElement;
                ThrowIfError(root);
                if (root.GetProperty("type").GetString() != "probe") continue;
                if (root.GetProperty("protocol").GetInt32() != 1 || root.GetProperty("major").GetInt32() < 2
                    || root.GetProperty("major").GetInt32() == 2 && root.GetProperty("minor").GetInt32() < 54)
                    throw new InvalidDataException("Native browser detection requires a matching helper and WPE WebKit 2.54+.");
                var version = $"{root.GetProperty("major").GetInt32()}.{root.GetProperty("minor").GetInt32()}.{root.GetProperty("micro").GetInt32()}";
                var codecs = root.GetProperty("h264Software").GetBoolean() && root.GetProperty("aacSoftware").GetBoolean()
                    ? "Software H.264/AAC decoders found."
                    : "Some software H.264/AAC decoders are missing; check GStreamer plugins.";
                await CheckRenderingAsync(stream, deadline.Token).ConfigureAwait(false);
                return new(true, $"WPE WebKit {version} initialized; browser startup, JavaScript and frame delivery verified. {codecs} Playback and audio still need testing.");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (OperationCanceledException)
        {
            return new(false, "Native browser detection timed out. Check the installed helper version, WPE/GStreamer dependencies, and the launcher's access to host libraries and XDG_RUNTIME_DIR.");
        }
        catch (Exception ex)
        {
            return new(false, "Native browser unavailable: " + BrowserMessageValidator.BoundText(ex.GetBaseException().Message, "unknown failure"));
        }
        finally
        {
            // EOF shuts the temporary probe helper down, including its children.
            client.Close();
            endpoint?.Launcher?.Dispose();
        }
    }

    private static void ThrowIfError(JsonElement root)
    {
        if (root.GetProperty("type").GetString() == "error")
            throw new IOException(BrowserMessageValidator.BoundText(root.GetProperty("message").GetString(), "Native browser startup failed."));
    }

    private static async Task CheckRenderingAsync(Stream stream, CancellationToken token)
    {
        var nonce = Guid.NewGuid().ToString("N");
        var ready = JsonSerializer.Serialize(new { type = "setupReady", nonce });
        await LinuxBrowserProtocol.WriteJsonAsync(stream, JsonSerializer.Serialize(new
        {
            type = "load", uri = $"https://crystalcast.local/player/{nonce}.html", generic = false,
            width = 320, height = 180, fps = 5,
            html = "<!doctype html><body style='margin:0;background:#123456'><script>window.chrome.webview.postMessage(" + ready + ");</script>",
        }), token).ConfigureAwait(false);
        var javascript = false;
        var pixels = false;
        while (!javascript || !pixels)
        {
            var header = await LinuxBrowserProtocol.ReadHeaderAsync(stream, token).ConfigureAwait(false);
            if (header.Kind == 2)
            {
                using var frame = await LinuxBrowserProtocol.ReadFrameAsync(stream, header.Length, token).ConfigureAwait(false);
                if (frame.Width != 320 || frame.Height != 180) throw new InvalidDataException("Unexpected setup frame dimensions.");
                pixels = true;
                continue;
            }
            using var message = await LinuxBrowserProtocol.ReadJsonAsync(stream, header.Length, token).ConfigureAwait(false);
            var root = message.RootElement;
            ThrowIfError(root);
            if (root.GetProperty("type").GetString() != "message") continue;
            using var payload = JsonDocument.Parse(root.GetProperty("message").GetString()!, new JsonDocumentOptions { MaxDepth = 4 });
            javascript |= payload.RootElement.TryGetProperty("type", out var type) && type.GetString() == "setupReady"
                && payload.RootElement.TryGetProperty("nonce", out var received) && received.GetString() == nonce;
        }
    }

    private static async Task<JsonDocument> ReadMessage(Stream stream, CancellationToken cancellationToken)
    {
        var header = await LinuxBrowserProtocol.ReadHeaderAsync(stream, cancellationToken).ConfigureAwait(false);
        if (header.Kind != 1) throw new InvalidDataException("Unexpected video data during native browser detection.");
        return await LinuxBrowserProtocol.ReadJsonAsync(stream, header.Length, cancellationToken).ConfigureAwait(false);
    }
}
