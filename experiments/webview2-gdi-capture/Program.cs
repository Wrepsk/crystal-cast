using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using CrystalCast;
using CrystalCast.Video;
using Microsoft.Web.WebView2.Core;

// Usage: WebView2CaptureProbe.exe [seconds per method] [width] [height] [output.json]
// Opens a WebView2 window with an animated page and measures three ways to grab its frames:
// JPEG (CapturePreviewAsync + decode, CrystalCast's Wine path), BitBlt and PrintWindow.
var seconds = args.Length > 0 ? double.Parse(args[0], System.Globalization.CultureInfo.InvariantCulture) : 5;
var width = args.Length > 1 ? int.Parse(args[1]) : 1280;
var height = args.Length > 2 ? int.Parse(args[2]) : 720;
var output = args.Length > 3 && args[3] != "-" ? args[3] : Path.Combine(AppContext.BaseDirectory, "probe-results.json");
// Optional extra Chromium switches, for example "--disable-gpu --disable-gpu-compositing".
var browserArguments = args.Length > 4 ? string.Join(' ', args.Skip(4)) : string.Empty;

// WebView2 needs a single-threaded COM apartment, like CrystalCast's browser thread.
var exitCode = 1;
var ui = new Thread(() =>
{
    ApplicationConfiguration.Initialize();
    var form = new ProbeForm(width, height, seconds, output, browserArguments);
    Application.Run(form);
    exitCode = form.ExitCode;
});
ui.SetApartmentState(ApartmentState.STA);
ui.Start();
ui.Join();
return exitCode;

internal sealed class ProbeForm : Form
{
    private const string Page = """
<!doctype html><html><body style="margin:0;background:#000;overflow:hidden">
<canvas id="c"></canvas>
<script>
const c = document.getElementById('c'); c.width = innerWidth; c.height = innerHeight;
const g = c.getContext('2d'); let n = 0;
function draw() {
  n++;
  g.fillStyle = `rgb(${n % 256},${(n * 3) % 256},200)`;
  g.fillRect(0, 0, c.width, c.height);
  g.fillStyle = '#fff'; g.font = '96px sans-serif'; g.fillText('frame ' + n, 60, 160);
  requestAnimationFrame(draw);
}
requestAnimationFrame(draw);
</script></body></html>
""";

    private readonly int browserWidth;
    private readonly int browserHeight;
    private readonly double seconds;
    private readonly string output;
    private readonly string browserArguments;
    private readonly Dictionary<string, object> report = new();
    private CoreWebView2Controller? controller;

    public ProbeForm(int width, int height, double seconds, string output, string browserArguments)
    {
        this.browserArguments = browserArguments;
        browserWidth = width;
        browserHeight = height;
        this.seconds = seconds;
        this.output = output;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        Location = new Point(0, 0);
        ClientSize = new Size(width, height);
        Text = "WebView2 capture probe";
        ShowInTaskbar = true;
    }

    public int ExitCode { get; private set; } = 1;

    protected override async void OnShown(EventArgs e)
    {
        base.OnShown(e);
        try
        {
            report["runtime"] = CoreWebView2Environment.GetAvailableBrowserVersionString();
            report["wine"] = NativeLibrary.TryLoad("ntdll.dll", out var ntdll) && NativeLibrary.TryGetExport(ntdll, "wine_get_version", out _);
            var userData = Path.Combine(Path.GetTempPath(), "webview2-capture-probe");
            report["browser_arguments"] = browserArguments;
            var options = new CoreWebView2EnvironmentOptions(("--autoplay-policy=no-user-gesture-required " + browserArguments).Trim());
            var environment = await CoreWebView2Environment.CreateAsync(null, userData, options);
            controller = await environment.CreateCoreWebView2ControllerAsync(Handle);
            controller.Bounds = new Rectangle(0, 0, browserWidth, browserHeight);
            controller.IsVisible = true;
            var loaded = new TaskCompletionSource();
            controller.CoreWebView2.NavigationCompleted += (_, _) => loaded.TrySetResult();
            // Real video mode (PROBE_YOUTUBE=<video id>): embed a muted, autoplaying YouTube video from
            // a local page served at an https address, like CrystalCast's player pages, so YouTube
            // accepts the embed. Otherwise use the synthetic animated canvas.
            var youtube = Environment.GetEnvironmentVariable("PROBE_YOUTUBE");
            if (!string.IsNullOrWhiteSpace(youtube))
            {
                var folder = Path.Combine(Path.GetTempPath(), "webview2-capture-probe-page");
                Directory.CreateDirectory(folder);
                File.WriteAllText(Path.Combine(folder, "index.html"), $$"""
<!doctype html><html><body style="margin:0;background:#000;overflow:hidden">
<iframe width="100%" height="100%" style="position:absolute;inset:0;border:0"
  src="https://www.youtube.com/embed/{{youtube}}?autoplay=1&mute=1&playsinline=1&controls=0&loop=1&playlist={{youtube}}"
  allow="autoplay; encrypted-media" referrerpolicy="strict-origin-when-cross-origin"></iframe>
</body></html>
""");
                controller.CoreWebView2.SetVirtualHostNameToFolderMapping("probe.local", folder, CoreWebView2HostResourceAccessKind.Allow);
                controller.CoreWebView2.Navigate("https://probe.local/index.html");
                report["video"] = youtube;
                await loaded.Task;
                await Task.Delay(TimeSpan.FromSeconds(12)); // let the player start and settle on a quality
            }
            else
            {
                controller.CoreWebView2.NavigateToString(Page);
                await loaded.Task;
                await Task.Delay(1500);
            }

            // Hold mode (PROBE_HOLD_SECONDS): keep the page animating and publish the window's
            // X11 id, which Wine stores as a window property, for an external capture test.
            if (double.TryParse(Environment.GetEnvironmentVariable("PROBE_HOLD_SECONDS"), System.Globalization.CultureInfo.InvariantCulture, out var hold))
            {
                var xid = GetPropW(Handle, "__wine_x11_whole_window");
                report["x11_window"] = (long)xid;
                File.WriteAllText(output + ".xid", ((long)xid).ToString(System.Globalization.CultureInfo.InvariantCulture));
                await Task.Delay(TimeSpan.FromSeconds(hold));
                ExitCode = 0;
                throw new OperationCanceledException("hold finished");
            }

            // Screencast mode (PROBE_MODE=screencast): today's screenshot JPEG versus DevTools screencast.
            if (Environment.GetEnvironmentVariable("PROBE_MODE") == "screencast")
            {
                report["jpeg_screenshot"] = await WithCpuAsync(MeasureJpegAsync);
                report["screencast_1080p_q80"] = await WithCpuAsync(() => MeasureScreencastAsync(1920, 1080, 80));
                report["screencast_720p_q80"] = await WithCpuAsync(() => MeasureScreencastAsync(1280, 720, 80));
                report["screencast_720p_q60"] = await WithCpuAsync(() => MeasureScreencastAsync(1280, 720, 60));
                ExitCode = 0;
                throw new OperationCanceledException("screencast finished");
            }

            // Plugin mode (PROBE_MODE=plugin): the plugin's own screencast class and size policy, driven
            // the way the plugin's capture loop drives it, with DevTools disabled as in the plugin.
            if (Environment.GetEnvironmentVariable("PROBE_MODE") == "plugin")
            {
                report["plugin_screencast"] = await WithCpuAsync(MeasurePluginScreencastAsync);
                ExitCode = 0;
                throw new OperationCanceledException("plugin screencast finished");
            }

            report["jpeg"] = await MeasureJpegAsync();
            report["bitblt"] = await MeasureGdiAsync(usePrintWindow: false);
            report["printwindow"] = await MeasureGdiAsync(usePrintWindow: true);
            report["screen_bitblt"] = await MeasureGdiAsync(usePrintWindow: false, fromScreen: true);
            ExitCode = 0;
        }
        catch (OperationCanceledException) when (ExitCode == 0)
        {
        }
        catch (Exception ex)
        {
            report["error"] = ex.ToString();
        }

        var json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(output, json);
        Console.WriteLine(json);
        Close();
    }

    private async Task<Dictionary<string, object>> MeasureJpegAsync()
    {
        var webView = controller!.CoreWebView2;
        using var stream = new MemoryStream();
        using var decoder = new WicBgraDecoder();
        var stats = new Stats();
        var end = Stopwatch.GetTimestamp() + (long)(seconds * Stopwatch.Frequency);
        while (Stopwatch.GetTimestamp() < end)
        {
            var sw = Stopwatch.StartNew();
            stream.SetLength(0);
            await webView.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Jpeg, stream);
            using var frame = decoder.Decode(stream.GetBuffer().AsSpan(0, (int)stream.Length), stats.Captures, 0);
            sw.Stop();
            stats.Record(frame.Pixels.AsSpan(0, frame.PixelLength), sw.Elapsed.TotalMilliseconds);
        }

        return stats.Summary(seconds);
    }

    /// <summary>
    /// DevTools screencast: the browser pushes a JPEG whenever the page changes and waits for an
    /// acknowledgement before the next one. Frames are decoded with the plugin's WIC decoder.
    /// </summary>
    private async Task<Dictionary<string, object>> MeasureScreencastAsync(int maxWidth, int maxHeight, int quality)
    {
        var webView = controller!.CoreWebView2;
        using var decoder = new WicBgraDecoder();
        var stats = new Stats();
        long encodedBytes = 0;
        double decodeMilliseconds = 0;
        int width = 0, height = 0;
        var receiver = webView.GetDevToolsProtocolEventReceiver("Page.screencastFrame");
        async void OnFrame(object? sender, CoreWebView2DevToolsProtocolEventReceivedEventArgs args)
        {
            using var document = JsonDocument.Parse(args.ParameterObjectAsJson);
            var root = document.RootElement;
            var sessionId = root.GetProperty("sessionId").GetInt32();
            // Acknowledge first so the browser can start encoding the next frame while we decode.
            _ = webView.CallDevToolsProtocolMethodAsync("Page.screencastFrameAck", $"{{\"sessionId\":{sessionId}}}");
            var jpeg = Convert.FromBase64String(root.GetProperty("data").GetString()!);
            var sw = Stopwatch.StartNew();
            using var frame = decoder.Decode(jpeg, stats.Captures, 0);
            sw.Stop();
            encodedBytes += jpeg.Length;
            decodeMilliseconds += sw.Elapsed.TotalMilliseconds;
            width = frame.Width;
            height = frame.Height;
            stats.Record(frame.Pixels.AsSpan(0, frame.PixelLength), sw.Elapsed.TotalMilliseconds);
            await Task.CompletedTask;
        }

        receiver.DevToolsProtocolEventReceived += OnFrame;
        try
        {
            await webView.CallDevToolsProtocolMethodAsync("Page.enable", "{}");
            await webView.CallDevToolsProtocolMethodAsync(
                "Page.startScreencast",
                $"{{\"format\":\"jpeg\",\"quality\":{quality},\"maxWidth\":{maxWidth},\"maxHeight\":{maxHeight},\"everyNthFrame\":1}}");
            await Task.Delay(TimeSpan.FromSeconds(seconds));
            await webView.CallDevToolsProtocolMethodAsync("Page.stopScreencast", "{}");
            await Task.Delay(300);
        }
        finally
        {
            receiver.DevToolsProtocolEventReceived -= OnFrame;
        }

        var summary = stats.Summary(seconds);
        summary["frame_size"] = $"{width}x{height}";
        summary["mean_jpeg_kb"] = stats.Captures == 0 ? 0 : Math.Round(encodedBytes / 1024.0 / stats.Captures, 1);
        summary["mean_decode_ms"] = stats.Captures == 0 ? 0 : Math.Round(decodeMilliseconds / stats.Captures, 2);
        return summary;
    }

    private async Task<Dictionary<string, object>> MeasurePluginScreencastAsync()
    {
        var webView = controller!.CoreWebView2;
        webView.Settings.AreDevToolsEnabled = false;
        var settings = WebView2ScreencastPolicy.Resolve(browserWidth, browserHeight, JpegCaptureResolution.Full1080p,
            Environment.GetEnvironmentVariable(WebView2ScreencastPolicy.OverrideVariable));
        var stats = new Stats();
        var gate = new object();
        long sequence = 0;
        int width = 0, height = 0;
        using var capture = new WebView2ScreencastCapture(
            frame =>
            {
                using (frame)
                {
                    lock (gate)
                    {
                        width = frame.Width;
                        height = frame.Height;
                        stats.Record(frame.Pixels.AsSpan(0, frame.PixelLength), 0);
                    }
                }
            },
            () => Interlocked.Increment(ref sequence),
            () => 60f);
        var running = true;
        var end = Stopwatch.GetTimestamp() + (long)(seconds * Stopwatch.Frequency);
        while (Stopwatch.GetTimestamp() < end)
        {
            running &= await capture.KeepRunningAsync(webView, settings);
            await Task.Delay(200);
        }

        var status = capture.Status;
        await capture.PauseAsync();
        Dictionary<string, object> summary;
        lock (gate)
        {
            summary = stats.Summary(seconds);
            summary["frame_size"] = $"{width}x{height}";
        }

        summary["running"] = running;
        summary["status"] = status;
        summary["failure"] = capture.FailureReason;
        return summary;
    }

    /// <summary>Adds CPU use (in cores) of this process and all WebView2 processes during a measurement.</summary>
    private async Task<Dictionary<string, object>> WithCpuAsync(Func<Task<Dictionary<string, object>>> measure)
    {
        static TimeSpan WebView2Cpu()
        {
            var total = TimeSpan.Zero;
            foreach (var process in Process.GetProcessesByName("msedgewebview2"))
            {
                try { total += process.TotalProcessorTime; } catch { } finally { process.Dispose(); }
            }

            return total;
        }

        var self = Process.GetCurrentProcess();
        var selfStart = self.TotalProcessorTime;
        var browserStart = WebView2Cpu();
        var wall = Stopwatch.StartNew();
        var result = await measure();
        wall.Stop();
        self.Refresh();
        result["probe_cpu_cores"] = Math.Round((self.TotalProcessorTime - selfStart).TotalSeconds / wall.Elapsed.TotalSeconds, 2);
        result["webview2_cpu_cores"] = Math.Round((WebView2Cpu() - browserStart).TotalSeconds / wall.Elapsed.TotalSeconds, 2);
        return result;
    }

    private async Task<object> MeasureGdiAsync(bool usePrintWindow, bool fromScreen = false)
    {
        // The screen (desktop) DC shows whatever is actually on the display at the window's position.
        var target = fromScreen ? 0 : usePrintWindow ? Handle : FindRenderWindow(Handle);
        var pixels = new byte[browserWidth * browserHeight * 4];
        var stats = new Stats();
        var windowDc = GetDC(target);
        var memoryDc = CreateCompatibleDC(windowDc);
        var info = new BitmapInfo
        {
            Size = Marshal.SizeOf<BitmapInfo>(),
            Width = browserWidth,
            Height = -browserHeight,
            Planes = 1,
            BitCount = 32,
        };
        var bitmap = CreateDIBSection(windowDc, ref info, 0, out var bits, 0, 0);
        var previous = SelectObject(memoryDc, bitmap);
        try
        {
            var end = Stopwatch.GetTimestamp() + (long)(seconds * Stopwatch.Frequency);
            var failures = 0;
            while (Stopwatch.GetTimestamp() < end)
            {
                var sw = Stopwatch.StartNew();
                var ok = usePrintWindow
                    ? PrintWindow(Handle, memoryDc, 2 /* PW_RENDERFULLCONTENT */)
                    : BitBlt(memoryDc, 0, 0, browserWidth, browserHeight, windowDc, 0, 0, 0x00CC0020 | 0x40000000 /* SRCCOPY | CAPTUREBLT */);
                GdiFlush();
                Marshal.Copy(bits, pixels, 0, pixels.Length);
                sw.Stop();
                if (!ok)
                    failures++;
                stats.Record(pixels, sw.Elapsed.TotalMilliseconds);
                await Task.Yield();
                Application.DoEvents();
            }

            var summary = stats.Summary(seconds);
            summary["failed_calls"] = failures;
            summary["target_window"] = fromScreen ? "screen" : GetClassName(target);
            return summary;
        }
        finally
        {
            SelectObject(memoryDc, previous);
            DeleteObject(bitmap);
            DeleteDC(memoryDc);
            ReleaseDC(target, windowDc);
        }
    }

    /// <summary>Chromium draws into a child window; prefer the deepest one for BitBlt.</summary>
    private static nint FindRenderWindow(nint root)
    {
        var found = root;
        EnumChildWindows(root, (child, _) =>
        {
            found = child;
            return true;
        }, 0);
        return found;
    }

    private static string GetClassName(nint hwnd)
    {
        var buffer = new char[256];
        var length = GetClassNameW(hwnd, buffer, buffer.Length);
        return new string(buffer, 0, Math.Max(0, length));
    }

    private sealed class Stats
    {
        private readonly HashSet<ulong> hashes = [];
        private double totalMilliseconds;
        private int nonBlack;

        public int Captures { get; private set; }

        public void Record(ReadOnlySpan<byte> pixels, double milliseconds)
        {
            Captures++;
            totalMilliseconds += milliseconds;
            var hash = 14695981039346656037UL;
            var bright = false;
            for (var i = 0; i < pixels.Length; i += 4099)
            {
                hash = (hash ^ pixels[i]) * 1099511628211UL;
                bright |= pixels[i] > 16;
            }

            hashes.Add(hash);
            if (bright)
                nonBlack++;
        }

        public Dictionary<string, object> Summary(double seconds) => new()
        {
            ["captures"] = Captures,
            ["distinct_frames"] = hashes.Count,
            ["distinct_fps"] = Math.Round(hashes.Count / seconds, 1),
            ["mean_ms"] = Captures == 0 ? 0 : Math.Round(totalMilliseconds / Captures, 2),
            ["non_black_captures"] = nonBlack,
        };
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfo
    {
        public int Size;
        public int Width;
        public int Height;
        public short Planes;
        public short BitCount;
        public int Compression;
        public int SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public int ClrUsed;
        public int ClrImportant;
    }

    private delegate bool EnumWindowsProc(nint hwnd, nint parameter);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint GetPropW(nint hwnd, string name);
    [DllImport("user32.dll")] private static extern nint GetDC(nint hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(nint hwnd, nint hdc);
    [DllImport("user32.dll")] private static extern bool PrintWindow(nint hwnd, nint hdc, uint flags);
    [DllImport("user32.dll")] private static extern bool EnumChildWindows(nint parent, EnumWindowsProc callback, nint parameter);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassNameW(nint hwnd, char[] name, int count);
    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleDC(nint hdc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(nint hdc);
    [DllImport("gdi32.dll")] private static extern nint CreateDIBSection(nint hdc, ref BitmapInfo info, uint usage, out nint bits, nint section, uint offset);
    [DllImport("gdi32.dll")] private static extern nint SelectObject(nint hdc, nint obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint obj);
    [DllImport("gdi32.dll")] private static extern bool BitBlt(nint dest, int x, int y, int width, int height, nint source, int sourceX, int sourceY, uint rop);
    [DllImport("gdi32.dll")] private static extern bool GdiFlush();
}
