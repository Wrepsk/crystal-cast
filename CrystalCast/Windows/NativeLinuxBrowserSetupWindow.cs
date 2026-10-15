using System.Numerics;
using CrystalCast.Video;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace CrystalCast.Windows;

internal sealed class NativeLinuxBrowserSetupWindow(Plugin plugin) : Window(
    "CrystalCast Native Linux Browser Setup###CrystalCastNativeLinuxBrowserSetup"), IDisposable
{
    private const string GuideUrl = "https://github.com/Wrepsk/crystal-cast/blob/master/NativeLinuxBrowser/README.md";
    private CancellationTokenSource? cancellation;
    private Task<(LinuxBrowserAvailability Availability, LinuxDistribution Distribution)>? pending;
    private bool showIfMissing;
    private bool previewMode;
    private LinuxDistribution distribution = LinuxDistribution.Unknown;
    private LinuxBrowserInstallationGuide guide = LinuxBrowserInstallationGuide.For(LinuxDistribution.Unknown);

    public string Status { get; private set; } = "Native WPE browser has not been checked yet.";
    public bool IsChecking => pending != null;

#if DEBUG
    private static readonly (string Label, string Metadata)[] PreviewDistributions =
    [
        ("Arch", "ID=arch\nPRETTY_NAME=Arch Linux"),
        ("Ubuntu", "ID=ubuntu\nPRETTY_NAME=Ubuntu 24.04 LTS\nVERSION_ID=24.04\nID_LIKE=debian"),
        ("Fedora", "ID=fedora\nPRETTY_NAME=Fedora Workstation\nVARIANT_ID=workstation"),
        ("SteamOS", "ID=steamos\nPRETTY_NAME=SteamOS\nID_LIKE=arch"),
        ("Fedora Atomic", "ID=fedora\nPRETTY_NAME=Fedora Silverblue\nVARIANT_ID=silverblue"),
        ("Unknown / sandbox", "ID=org.freedesktop.platform\nPRETTY_NAME=Freedesktop runtime"),
    ];
    private int previewDistribution;

    public void ShowDebugPreview()
    {
        Suspend();
        previewMode = true;
        previewDistribution = 0;
        SetDistribution(LinuxDistribution.Parse(PreviewDistributions[0].Metadata, "debug sample"));
        Status = "Debug preview. Native WPE detection runs on Linux/Wine; this preview does not start a helper or change setup preferences.";
        IsOpen = true;
    }
#endif

    public void CheckAvailability(bool openIfMissing, bool showWhileChecking = false)
    {
        Suspend();
        showIfMissing = openIfMissing;
        Status = "Checking the native helper, WPE initialization and GStreamer decoders...";
        cancellation = new CancellationTokenSource();
        var token = cancellation.Token;
        pending = Task.Run(async () =>
        {
            var found = LinuxDistribution.Detect();
            var availability = await new LinuxBrowserAvailabilityProbe().CheckAsync(token).ConfigureAwait(false);
            return (availability, found);
        }, token);
        IsOpen = showWhileChecking;
    }

    public void RefreshState()
    {
        if (pending is not { IsCompleted: true } completed) return;
        pending = null;
        cancellation?.Dispose();
        cancellation = null;
        if (completed.IsCanceled) return;
        LinuxBrowserAvailability result;
        try
        {
            var completedResult = completed.GetAwaiter().GetResult();
            result = completedResult.Availability;
            SetDistribution(completedResult.Distribution);
        }
        catch (Exception ex) { result = new(false, "Native browser detection failed: " + BrowserMessageValidator.BoundText(ex.Message, "unknown failure")); }
        Status = result.Message;
        IsOpen = !result.Available && showIfMissing;
    }

    public void Suspend()
    {
        cancellation?.Cancel();
        cancellation?.Dispose();
        cancellation = null;
        pending = null;
        IsOpen = false;
        previewMode = false;
    }

    public void Dispose() => Suspend();

    private void SetDistribution(LinuxDistribution found)
    {
        distribution = found;
        guide = LinuxBrowserInstallationGuide.For(found);
    }

    public override void PreDraw()
    {
        Size = new Vector2(680, 600);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    public override void Draw()
    {
        if (!previewMode && !BrowserPlatformPolicy.UseNativeLinuxBrowser(WineEnvironment.IsWine, plugin.Configuration.EnableNativeLinuxBrowser))
        {
            Suspend();
            return;
        }
        CrystalCastUiTheme.DrawWindowHeader("LinuxBrowser", "LINUX / WINE", "Set up the native WPE browser",
            "Install the helper and WPE on Linux, outside the game's Wine prefix.");
        var footerHeight = ImGui.GetFrameHeightWithSpacing() * 2;
        if (ImGui.BeginChild("CrystalCastNativeSetupContent", new Vector2(0, -footerHeight), false))
        {
#if DEBUG
            if (previewMode)
            {
                if (ImGui.BeginCombo("Preview Linux distro", PreviewDistributions[previewDistribution].Label))
                {
                    for (var i = 0; i < PreviewDistributions.Length; i++)
                        if (ImGui.Selectable(PreviewDistributions[i].Label, i == previewDistribution))
                        {
                            previewDistribution = i;
                            SetDistribution(LinuxDistribution.Parse(PreviewDistributions[i].Metadata, "debug sample"));
                        }
                    ImGui.EndCombo();
                }
            }
#endif
            WineWebView2SetupWindow.DrawCard("NativeDistro", "Linux distribution", distribution.Name,
                $"Detected from: {distribution.Source}");
            WineWebView2SetupWindow.DrawCard("NativeDetection", "Detection status", Status);
            ImGui.BeginDisabled(previewMode || IsChecking);
            if (ImGui.Button(IsChecking ? "Checking..." : "Retry detection") && !IsChecking)
                CheckAvailability(openIfMissing: true, showWhileChecking: true);
            ImGui.EndDisabled();
            ImGui.Spacing();
            WineWebView2SetupWindow.DrawCard("NativeRequirements", "What needs to be installed",
                "CrystalCast's crystalcast-browser-helper and WPE WebKit 2.54+ with WPEPlatform 2.0 are both required. A missing helper means WPE cannot be verified from the plugin.",
                "GStreamer plugins supply video/audio codecs. A successful dependency check does not establish playback or hardware acceleration.");
            WineWebView2SetupWindow.DrawCard("NativeDistroGuide", guide.Title, guide.Explanation);
            if (guide.PackageCommand != null)
            {
                ImGui.TextWrapped(guide.PackageCommand);
                if (ImGui.Button(guide.Family == LinuxInstallFamily.Arch ? "Copy dependency install command" : "Copy prerequisite install command"))
                    ImGui.SetClipboardText(guide.PackageCommand);
            }
            if (guide.RepositoryCheck != null)
            {
                ImGui.Spacing();
                ImGui.TextWrapped(guide.RepositoryCheck);
                if (ImGui.Button("Copy WPE repository check")) ImGui.SetClipboardText(guide.RepositoryCheck);
            }
            ImGui.Spacing();
            CrystalCastUiTheme.DrawSectionHeader("Check the required WPE version", "This must pass before building the helper:");
            ImGui.TextWrapped(LinuxBrowserInstallationGuide.VersionCheck);
            if (ImGui.Button("Copy WPE version check")) ImGui.SetClipboardText(LinuxBrowserInstallationGuide.VersionCheck);
            ImGui.Spacing();
            WineWebView2SetupWindow.DrawCard("NativeHelper", "Why a helper is needed",
                "CrystalCast runs inside Windows Dalamud under Wine. The helper is a small native Linux process that hosts WPE, receives playback commands and sends browser frames to the plugin.",
                "Install it once on Linux. CrystalCast then starts and stops it automatically for each active screen. It does not bundle a browser engine or modify your Wine prefix.");
            CrystalCastUiTheme.DrawSectionHeader("Build the helper after installing WPE", "From the CrystalCast source checkout on the Linux host:");
            ImGui.TextWrapped(LinuxBrowserInstallationGuide.HelperInstall);
            if (ImGui.Button("Copy helper build commands")) ImGui.SetClipboardText(LinuxBrowserInstallationGuide.HelperInstall);
            ImGui.Spacing();
            WineWebView2SetupWindow.DrawCard("NativeCompatibility", "Other distributions and testing",
                "Run as your desktop user. Keep XDG_RUNTIME_DIR and HOME available to the game launcher. Flatpak and Proton containers may need additional host integration.",
                "Native Wine launching, audible audio and wider provider support still need testing. Choose WebView2 in Browser settings to return to the existing Wine backend.");
            if (ImGui.Button("Copy setup guide URL")) ImGui.SetClipboardText(GuideUrl);
        }
        ImGui.EndChild();
        if (previewMode)
        {
            if (ImGui.Button("Close preview")) Suspend();
            return;
        }
        if (ImGui.Button("Remind me later")) { showIfMissing = false; IsOpen = false; }
        ImGui.SameLine();
        if (ImGui.Button("Don't show again"))
        {
            plugin.Configuration.NativeLinuxBrowserSetupDismissed = true;
            plugin.Configuration.Save();
            showIfMissing = false;
            IsOpen = false;
        }
    }
}
