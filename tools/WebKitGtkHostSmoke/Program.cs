using System.Collections.Concurrent;
using System.Text.Json;
using CrystalCast.Video;

// Usage under Wine: WebKitGtkHostSmoke.exe <native dir> [seconds] [width] [height]
// Exit code 0 means frames arrived, changed, and page messages worked in both directions.
var nativeDirectory = args.Length > 0 ? args[0] : Path.Combine(AppContext.BaseDirectory, "native", "linux-x64");
var seconds = args.Length > 1 ? int.Parse(args[1]) : 10;
var width = args.Length > 2 ? int.Parse(args[2]) : 1280;
var height = args.Length > 3 ? int.Parse(args[3]) : 720;
const string Nonce = "smoke-nonce";

const string Html = """
<!doctype html>
<html><body style="margin:0;background:#000">
<canvas id="c" style="display:block"></canvas>
<script>
  const canvas = document.getElementById('c');
  canvas.width = innerWidth; canvas.height = innerHeight;
  const g = canvas.getContext('2d');
  let n = 0;
  function draw() {
    n++;
    g.fillStyle = `rgb(${n % 256},${(n * 3) % 256},200)`;
    g.fillRect(0, 0, canvas.width, canvas.height);
    g.fillStyle = '#ffffff';
    g.font = '64px sans-serif';
    g.fillText('frame ' + n, 40, 120);
    requestAnimationFrame(draw);
  }
  requestAnimationFrame(draw);
  const post = (m) => window.chrome && window.chrome.webview && window.chrome.webview.postMessage(Object.assign({ nonce: 'smoke-nonce' }, m));
  window.chrome.webview.addEventListener('message', (e) => {
    if (e.data && e.data.type === 'ping') post({ type: 'pong', value: e.data.value, origin: location.origin });
  });
  post({ type: 'ready', ua: navigator.userAgent });
</script>
</body></html>
""";

var listener = new SmokeListener();
var log = Path.Combine(Path.GetTempPath(), "crystalcast-webkit-smoke.log");
var options = new WebKitGtkHostOptions(
    Path.Combine(nativeDirectory, "crystalcast-webkit-host"),
    Path.Combine(nativeDirectory, "crystalcast-webkit-host.sh"),
    log,
    width,
    height,
    60);

Console.WriteLine($"Wine host system: {WineHostInterop.HostSystemName}");
using var session = new WebKitGtkHostSession(options, listener);
session.LoadPage("https://crystalcast.local/player/smoke.html", Html);
session.SetMuted(true);
session.SetCaptureEnabled(true);
session.Start();

var deadline = DateTime.UtcNow.AddSeconds(seconds + 30);
var measureEnd = DateTime.MaxValue;
var pinged = false;
while (DateTime.UtcNow < deadline && listener.Failure == null)
{
    if (listener.Ready && !pinged)
    {
        session.PostToPage(JsonSerializer.Serialize(new { type = "ping", nonce = Nonce, value = 42 }));
        pinged = true;
        measureEnd = DateTime.UtcNow.AddSeconds(seconds);
        listener.ResetCounters();
    }

    if (DateTime.UtcNow >= measureEnd)
        break;
    Thread.Sleep(50);
}

var measured = pinged ? seconds : 0;
var report = new
{
    runtime = session.RuntimeDescription,
    failure = listener.Failure,
    failure_details = listener.FailureDetails,
    statuses = listener.Statuses.ToArray(),
    graphics = listener.Graphics,
    ready = listener.Ready,
    pong = listener.Pong,
    frames = listener.Frames,
    distinct_frames = listener.Distinct,
    fps = measured > 0 ? listener.Frames / (double)measured : 0,
    extract_ms_mean = listener.Frames > 0 ? listener.ExtractTotal / listener.Frames : 0,
    first_pixel_bgra = listener.FirstPixel,
};
Console.WriteLine(JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
var ok = listener.Failure == null && listener.Ready && listener.Pong && listener.Frames > 0 && listener.Distinct > 1;
Console.WriteLine(ok ? "SMOKE PASSED" : "SMOKE FAILED");
return ok ? 0 : 1;

internal sealed class SmokeListener : IWebKitGtkHostListener
{
    private readonly HashSet<ulong> hashes = [];

    public ConcurrentQueue<string> Statuses { get; } = new();
    public string? Failure { get; private set; }
    public string Graphics { get; private set; } = string.Empty;
    public bool Ready { get; private set; }
    public bool Pong { get; private set; }
    public int Frames { get; private set; }
    public int Distinct { get; private set; }
    public double ExtractTotal { get; private set; }
    public string FirstPixel { get; private set; } = string.Empty;

    public void ResetCounters()
    {
        lock (hashes)
        {
            Frames = 0;
            Distinct = 0;
            ExtractTotal = 0;
            hashes.Clear();
        }
    }

    public void OnHostStatus(string status) => Statuses.Enqueue(status);
    public string FailureDetails { get; private set; } = string.Empty;
    public void OnHostLog(string message) => Statuses.Enqueue("log: " + message);
    public void OnHostFailed(string reason, string details)
    {
        Failure = reason;
        FailureDetails = details;
    }
    public void OnHostGraphics(string browserTexture, string outputTexture) => Graphics = $"{browserTexture} -> {outputTexture}";
    public void OnPageLoading() => Statuses.Enqueue("page loading");
    public void OnControlsVisibilityChanged(bool visible) => Statuses.Enqueue($"controls {visible}");

    public void OnHostFrame(VideoFrame frame, double extractMilliseconds)
    {
        using (frame)
        {
            var hash = 14695981039346656037UL;
            var pixels = frame.Pixels.AsSpan(0, frame.PixelLength);
            for (var i = 0; i < pixels.Length; i += 4099)
                hash = (hash ^ pixels[i]) * 1099511628211UL;
            lock (hashes)
            {
                Frames++;
                ExtractTotal += extractMilliseconds;
                if (hashes.Add(hash))
                    Distinct++;
                var center = ((frame.Height / 2 * frame.Width) + (frame.Width / 2)) * 4;
                FirstPixel = $"{pixels[center]},{pixels[center + 1]},{pixels[center + 2]},{pixels[center + 3]}";
            }
        }
    }

    public void OnPageMessage(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.GetProperty("nonce").GetString() != "smoke-nonce")
            return;

        switch (root.GetProperty("type").GetString())
        {
            case "ready":
                Statuses.Enqueue("page ready: " + root.GetProperty("ua").GetString());
                Ready = true;
                break;
            case "pong":
                Pong = root.GetProperty("value").GetInt32() == 42;
                Statuses.Enqueue("pong from " + root.GetProperty("origin").GetString());
                break;
        }
    }
}
