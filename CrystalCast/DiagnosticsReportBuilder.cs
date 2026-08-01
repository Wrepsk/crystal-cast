using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using CrystalCast.Rendering;
using CrystalCast.Sync;
using CrystalCast.Video;
using Dalamud.Plugin;

namespace CrystalCast;

internal static class DiagnosticsReportBuilder
{
    public static string Build(Configuration configuration, WorldScreenManager renderer)
    {
        var graphics = GraphicsDiagnostics.CaptureEnvironment();
        var webView2Available = WebView2BrowserFrameSource.TryGetWebView2Runtime(out var webView2Version, out var webView2Error);
        var screens = renderer.GetDiagnosticSnapshots();
        var assembly = typeof(DiagnosticsReportBuilder).Assembly;
        var pluginVersion = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString()
            ?? "unknown";
        var dalamudVersion = typeof(IDalamudPluginInterface).Assembly.GetName().Version?.ToString() ?? "unknown";
        var resolvedCaptureMode = BrowserPlatformPolicy.ResolveCaptureMode(configuration.YouTubeBrowserEngine, WineEnvironment.IsWine);

        var report = new StringBuilder(4096);
        report.AppendLine("CrystalCast diagnostics");
        Append(report, "Generated (UTC)", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        Append(report, "Privacy", "source URLs, screen names, screen IDs, character details, and world positions are omitted");

        report.AppendLine();
        report.AppendLine("[Environment]");
        Append(report, "CrystalCast", pluginVersion);
        Append(report, "Configuration schema", configuration.Version);
        Append(report, "Dalamud assembly", dalamudVersion);
        Append(report, ".NET", RuntimeInformation.FrameworkDescription);
        Append(report, "OS", RuntimeInformation.OSDescription);
        Append(report, "Architecture", $"process {RuntimeInformation.ProcessArchitecture}; OS {RuntimeInformation.OSArchitecture}");
        Append(report, "Wine", YesNo(WineEnvironment.IsWine));
        Append(report, "WebView2", webView2Available ? webView2Version : $"unavailable; {webView2Error}");

        report.AppendLine();
        report.AppendLine("[Graphics]");
        Append(report, "Adapter", graphics.Adapter);
        Append(report, "D3D feature level", graphics.FeatureLevel);
        Append(report, "Game viewport", $"{graphics.ViewportWidth} x {graphics.ViewportHeight}");
        Append(report, "Graphics query", graphics.Status);
        Append(report, "Configured output layer", DescribeOutputMode(configuration.OutputMode));
        Append(report, "Configured browser backend", configuration.YouTubeBrowserEngine);
        Append(report, "Resolved capture mode", DescribeCaptureMode(resolvedCaptureMode));
        Append(report, "GPU texture sampling", configuration.EnableGpuDiagnostics ? "enabled" : "disabled");
        Append(report, "Renderer", renderer.Status);
        Append(report, "Draw", renderer.LastDrawStatus);
        Append(report, "Last graphics error", renderer.LastGraphicsError);
        Append(report, "Scene composite", renderer.SceneCompositeStatus);

        report.AppendLine();
        report.AppendLine("[Plugin state]");
        Append(report, "Plugin rendering enabled", YesNo(configuration.Enabled));
        Append(report, "IPC enabled", YesNo(configuration.IpcEnabled));
        Append(report, "IPC API", ScreenStateIpc.ApiVersion);
        Append(report, "Screens configured", configuration.BrowserScreens.Count);
        Append(report, "Browser runtimes capturing", renderer.ActiveBrowserRuntimeCount);
        Append(report, "Browser budget", renderer.BrowserResourceBudgetStatus);
        Append(report, "Selected source", renderer.SourceName);
        Append(report, "Selected source status", renderer.SourceStatus);
        Append(report, "Selected audio", renderer.AudioStatus);
        Append(report, "Selected audio distance", $"{renderer.AudioDistanceMeters.ToString("0.00", CultureInfo.InvariantCulture)} m");
        Append(report, "Selected audio falloff", $"{(renderer.SpatialAudioAttenuation * 100.0f).ToString("0", CultureInfo.InvariantCulture)}%");
        Append(report, "Selected effective volume", $"{(renderer.EffectiveAudioVolume * 100.0f).ToString("0", CultureInfo.InvariantCulture)}%");

        foreach (var screen in screens)
            AppendScreen(report, screen);

        report.AppendLine();
        report.AppendLine(configuration.EnableGpuDiagnostics
            ? "GPU sampling is enabled; WGC source and game-texture sample results appear in source/GPU sample status."
            : "For invisible or black WGC output, enable GPU texture sampling and copy the report again after a few seconds.");
        return report.ToString().TrimEnd();
    }

    private static void AppendScreen(StringBuilder report, WorldScreenDiagnosticSnapshot screen)
    {
        report.AppendLine();
        report.Append("[Screen ").Append(screen.Number);
        if (screen.IsSelected)
            report.Append(" - selected");
        report.AppendLine("]");
        Append(report, "Enabled", YesNo(screen.Enabled));
        Append(report, "Within browser budget", YesNo(screen.WithinResourceBudget));
        Append(report, "Origin", screen.CreatedByIpc ? "IPC" : "local");
        Append(report, "Provider", screen.Provider);
        Append(report, "Source configured", YesNo(screen.SourceConfigured));
        Append(report, "Playback paused", YesNo(screen.PlaybackPaused));
        Append(report, "Placement", screen.PlacementMode);
        Append(report, "Panel", $"{Format(screen.WidthMeters)} x {Format(screen.HeightMeters)} m; curve {Format(screen.CurveMeters)} m; occluded alpha {Format(screen.OccludedAlpha)}; distance fade {YesNo(screen.DistanceFadeEnabled)}");
        Append(report, "Browser", $"{screen.ConfiguredWidth} x {screen.ConfiguredHeight}; {Format(screen.ConfiguredCaptureFps)} fps ({(screen.CaptureFpsManual ? "manual" : "automatic")}); {DescribeCaptureMode(screen.CaptureMode)}");
        Append(report, "Runtime", $"created {YesNo(screen.RuntimeCreated)}; capturing {YesNo(screen.CaptureRunning)}; controls visible {YesNo(screen.BrowserControlsVisible)}");
        Append(report, "Source", screen.SourceName);
        Append(report, "Source status", screen.SourceStatus);
        Append(report, "Draw status", screen.DrawStatus);
        Append(report, "Last screen error", screen.LastError);
        Append(report, "Texture", $"{screen.TexturePipeline}; {screen.TextureWidth} x {screen.TextureHeight}; uploads {screen.UploadCount}; last {screen.LastUploadMilliseconds.ToString("0.000", CultureInfo.InvariantCulture)} ms; frame age {screen.FrameAgeMilliseconds} ms");
        Append(report, "GPU sample", screen.GpuSampleStatus);
    }

    private static string DescribeOutputMode(ScreenOutputMode outputMode)
    {
        return outputMode switch
        {
            ScreenOutputMode.ImGuiOverlay => "ImGui overlay",
            ScreenOutputMode.NativeOverlay => "Native overlay",
            ScreenOutputMode.SceneComposite => "Scene composite",
            _ => $"unknown ({(int)outputMode})",
        };
    }

    private static string DescribeCaptureMode(WebView2CaptureMode captureMode)
    {
        return captureMode == WebView2CaptureMode.WindowGraphicsCapture
            ? "WebView2 window graphics capture"
            : "WebView2 JPEG capture";
    }

    private static string YesNo(bool value) => value ? "yes" : "no";

    private static string Format(float value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    private static void Append(StringBuilder report, string label, object value)
    {
        report.Append(label).Append(": ").AppendLine(Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty);
    }
}
