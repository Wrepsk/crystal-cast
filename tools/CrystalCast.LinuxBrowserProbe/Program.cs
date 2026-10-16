using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using CrystalCast;
using CrystalCast.Video;
using SharpDX;
using SharpDX.Direct3D;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using Device = SharpDX.Direct3D11.Device;

var arguments = new Dictionary<string, string>(StringComparer.Ordinal);
for (var i = 0; i < args.Length; i += 2)
{
    if (i + 1 == args.Length) throw new ArgumentException("Options require a value.");
    arguments.Add(args[i], args[i + 1]);
}
var samples = new Dictionary<BrowserSourceProviderKind, string>
{
    [BrowserSourceProviderKind.YouTube] = "https://www.youtube.com/watch?v=jfKfPfyJRdk",
    [BrowserSourceProviderKind.Twitch] = "https://www.twitch.tv/monstercat",
    [BrowserSourceProviderKind.Dailymotion] = "https://www.dailymotion.com/video/x84sh87",
    [BrowserSourceProviderKind.Vimeo] = "https://vimeo.com/76979871",
};
if (arguments.TryGetValue("--describe-distro", out var distroFile))
{
    var distro = LinuxDistribution.Parse(File.ReadAllText(distroFile), "test os-release file");
    Console.WriteLine(JsonSerializer.Serialize(new { Distribution = distro, Guide = LinuxBrowserInstallationGuide.For(distro) },
        new JsonSerializerOptions { WriteIndented = true }));
    return;
}
if (arguments.GetValueOrDefault("--wine-launch-check", "false") == "true")
{
    Console.WriteLine(JsonSerializer.Serialize(new { Wine = WineEnvironment.IsWine, Distribution = LinuxDistribution.Detect(), Installed = LinuxBrowserRuntimeInstaller.Installed() }));
    var result = await new LinuxBrowserAvailabilityProbe().CheckAsync(CancellationToken.None);
    Console.WriteLine(JsonSerializer.Serialize(result));
    if (!result.Available) Environment.ExitCode = 1;
    return;
}
if (arguments.TryGetValue("--verify-package", out var packageFile))
{
    // Developer-only validation of a locally built candidate; the plugin never
    // trusts caller-provided hashes and uses its embedded release catalog.
    var manifestBytes = File.ReadAllBytes(arguments["--manifest"]);
    var version = JsonSerializer.Deserialize<LinuxRuntimeManifest>(manifestBytes, LinuxBrowserRuntimePackage.JsonOptions)!.Version;
    var release = new LinuxRuntimeRelease(version, "", Convert.ToHexStringLower(SHA256.HashData(manifestBytes)), "", "", 0, []);
    var manifest = LinuxBrowserRuntimePackage.ReadManifest(manifestBytes, release);
    await using var archive = File.OpenRead(packageFile);
    var destination = arguments["--extract-to"];
    await LinuxBrowserRuntimePackage.ExtractVerifiedAsync(archive, manifest, destination, null, CancellationToken.None);
    if (arguments.TryGetValue("--native-directory", out var nativeDirectory))
    {
        if (!WineEnvironment.IsWine || string.IsNullOrEmpty(Environment.GetEnvironmentVariable("XDG_DATA_HOME")))
            throw new InvalidOperationException("Native candidate validation requires Wine and an isolated test XDG_DATA_HOME.");
        if (!string.Equals(Path.GetFullPath(LinuxBrowserLauncher.ToWinePath(nativeDirectory)), Path.GetFullPath(destination), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Native candidate path must match the verified extraction destination.");
        Directory.CreateDirectory(LinuxBrowserLauncher.ToWinePath(LinuxBrowserRuntimeInstaller.NativeRoot));
        await LinuxBrowserRuntimeInstaller.SetPermissionsAsync(nativeDirectory, manifest, CancellationToken.None);
        var readiness = await new LinuxBrowserAvailabilityProbe(new LinuxBrowserLauncher(nativeDirectory + "/" + manifest.Entrypoint)).CheckAsync(CancellationToken.None);
        Console.WriteLine(JsonSerializer.Serialize(readiness));
        if (!readiness.Available) Environment.ExitCode = 1;
    }
    Console.WriteLine(JsonSerializer.Serialize(new { manifest.Version, Files = manifest.Files.Length, Bytes = manifest.Files.Sum(file => file.Size), Destination = destination }));
    return;
}
if (arguments.TryGetValue("--youtube-url", out var youtubeUrl)) samples[BrowserSourceProviderKind.YouTube] = youtubeUrl;
if (arguments.TryGetValue("--export-generic-script", out var scriptFile))
{
    File.WriteAllText(scriptFile, GenericWebBrowserFrameSource.BuildControllerScript("native-video-test"));
    return;
}
BrowserFrameSourceRequest Request(BrowserSourceProviderKind kind, string url) => new(
    kind, BrowserSourceDescriptors.Get(kind), url,
    int.Parse(arguments.GetValueOrDefault("--width", "1280"), System.Globalization.CultureInfo.InvariantCulture),
    int.Parse(arguments.GetValueOrDefault("--height", "720"), System.Globalization.CultureInfo.InvariantCulture),
    60, BrowserMediaEngine.Auto, true, false, false, false, 0.7f, 1);

if (arguments.TryGetValue("--export-pages", out var outputDirectory))
{
    Directory.CreateDirectory(outputDirectory);
    foreach (var (kind, url) in samples)
    {
        using var source = new LinuxBrowserFrameSource(Request(kind, url));
        File.WriteAllText(Path.Combine(outputDirectory, kind + ".json"), source.BuildLoadMessage());
    }
    Console.WriteLine("Exported existing CrystalCast provider/player pages for native browser tests.");
    return;
}

var endpointPath = arguments["--endpoint-file"];
if (arguments.GetValueOrDefault("--check-backend", "false") == "true")
{
    var result = await new LinuxBrowserAvailabilityProbe(new ExistingHelper(endpointPath)).CheckAsync(CancellationToken.None);
    Console.WriteLine(JsonSerializer.Serialize(result));
    if (!result.Available) Environment.ExitCode = 1;
    return;
}
var provider = Enum.Parse<BrowserSourceProviderKind>(arguments.GetValueOrDefault("--provider", "GenericWeb"));
var input = arguments.TryGetValue("--url", out var suppliedUrl) ? suppliedUrl : samples[provider];
using var browser = new LinuxBrowserFrameSource(Request(provider, input), new ExistingHelper(endpointPath),
    arguments.GetValueOrDefault("--transport", "tcp") == "shared-file");
using var device = new Device(DriverType.Warp, DeviceCreationFlags.BgraSupport);
Texture2D? texture = null;
browser.Start();
var duration = double.Parse(arguments.GetValueOrDefault("--seconds", "15"), System.Globalization.CultureInfo.InvariantCulture);
var clock = Stopwatch.StartNew();
long sequence = 0, frames = 0, uploadTicks = 0;
var hashes = new HashSet<uint>();
double? firstFrame = null;
var transitions = new List<ControlTransition>();
var controls = arguments.GetValueOrDefault("--controls", "false") == "true";
var stage = 0;
try
{
    while (clock.Elapsed.TotalSeconds < duration)
    {
        if (browser.TryGetLatestFrame(out var frame))
        {
            using (frame)
            {
                if (frame.Sequence != sequence)
                {
                    var started = Stopwatch.GetTimestamp();
                    if (texture == null)
                    {
                        texture = new Texture2D(device, new Texture2DDescription
                        {
                            Width = frame.Width, Height = frame.Height, MipLevels = 1, ArraySize = 1,
                            Format = Format.B8G8R8A8_UNorm, SampleDescription = new(1, 0),
                            Usage = ResourceUsage.Default, BindFlags = BindFlags.ShaderResource,
                        });
                    }
                    using var pixels = DataStream.Create(frame.Pixels, true, false);
                    device.ImmediateContext.UpdateSubresource(new DataBox(pixels.DataPointer, frame.Width * 4, 0), texture);
                    uploadTicks += Stopwatch.GetTimestamp() - started;
                    sequence = frame.Sequence;
                    firstFrame ??= clock.Elapsed.TotalSeconds;
                    uint hash = 2166136261;
                    for (var offset = 0; offset < frame.PixelLength; offset += 4096) hash = (hash ^ frame.Pixels[offset]) * 16777619;
                    hashes.Add(hash);
                    frames++;
                }
            }
        }
        if (controls && clock.Elapsed.TotalSeconds >= 4 + stage * 3 && stage < 3)
        {
            browser.TryGetPlaybackTelemetry(out var before);
            if (stage == 0) browser.Pause();
            if (stage == 1) { browser.SeekTo(1); browser.Play(); }
            if (stage == 2) browser.ApplyPlaybackSettings(false, .25f, .5f, true, false);
            transitions.Add(new(stage, before.State, before.PositionMs, before.Rate));
            stage++;
        }
        await Task.Delay(2);
    }
    browser.TryGetPlaybackTelemetry(out var telemetry);
    var controlsVerified = !controls || transitions.Count == 3
        && transitions[0].State == ScreenPlaybackState.Playing
        && transitions[1].State == ScreenPlaybackState.Paused
        && Math.Abs(transitions[1].PositionMs - transitions[0].PositionMs) < 1000
        && transitions[2].State == ScreenPlaybackState.Playing
        && transitions[2].PositionMs is > 1500 and < 6000
        && telemetry.State == ScreenPlaybackState.Playing && Math.Abs(telemetry.Rate - .5f) < .01f;
    var result = new
    {
        Frames = frames, ChangedFrameSamples = hashes.Count, Seconds = clock.Elapsed.TotalSeconds,
        ConsumedFps = frames / clock.Elapsed.TotalSeconds, FirstFrameSeconds = firstFrame,
        WarpUploadMsPerFrame = frames == 0 ? 0 : uploadTicks * 1000.0 / Stopwatch.Frequency / frames,
        browser.Status, Telemetry = telemetry, ControlTransitions = transitions,
        ControlsRequested = controls, ControlsVerified = controls ? controlsVerified : (bool?)null,
        Environment = ".NET adapter on Windows; native WPE helper through WSL; WARP upload, not FFXIV/DXVK",
    };
    var json = JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
    if (arguments.TryGetValue("--output", out var output)) File.WriteAllText(output, json);
    Console.WriteLine(json);
    if (frames < 10 || hashes.Count < 5 || telemetry.DurationMs < 1000 || !controlsVerified) Environment.ExitCode = 1;
}
finally { texture?.Dispose(); }

internal sealed class ExistingHelper(string endpointPath) : ILinuxBrowserLauncher
{
    public Task<LinuxBrowserEndpoint> LaunchAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(LinuxBrowserEndpoint.Read(endpointPath));
    }
}

internal sealed record ControlTransition(int Stage, ScreenPlaybackState State, long PositionMs, float Rate);
