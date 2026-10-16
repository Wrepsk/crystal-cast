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
    private Task<LinuxRuntimeInstallation>? installing;
    private CancellationTokenSource? installCancellation;
    private LinuxRuntimeInstallProgress installProgress = new("", 0);
    private LinuxRuntimeInstallation? installed;
    private LinuxRuntimeRelease? release;

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
        if (installing is { IsCompleted: true } completedInstall)
        {
            installing = null;
            installCancellation?.Dispose(); installCancellation = null;
            try
            {
                installed = completedInstall.GetAwaiter().GetResult();
                Status = $"Linux Browser {installed.Version} is installed and ready. Enable a screen to start watching.";
            }
            catch (OperationCanceledException) { Status = "Installation cancelled. The previous browser installation is unchanged."; }
            catch (Exception ex) { Status = "Linux Browser installation failed: " + BrowserMessageValidator.BoundText(ex.GetBaseException().Message, "unknown failure"); }
            IsOpen = true;
        }
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
        if (installed != null && !result.Available)
            Status += " Use Repair Linux Browser if a verified download is available.";
        IsOpen = !result.Available && showIfMissing;
    }

    public void Suspend()
    {
        installCancellation?.Cancel();
        cancellation?.Cancel();
        cancellation?.Dispose();
        cancellation = null;
        pending = null;
        IsOpen = false;
        previewMode = false;
    }

    public void Dispose()
    {
        Suspend();
        installCancellation?.Dispose();
        installCancellation = null;
    }

    private void SetDistribution(LinuxDistribution found)
    {
        distribution = found;
        guide = LinuxBrowserInstallationGuide.For(found);
        if (!previewMode)
        {
            try
            {
                release = LinuxBrowserRuntimeInstaller.SelectRelease(found);
                installed = LinuxBrowserRuntimeInstaller.Installed();
            }
            catch (Exception ex)
            {
                installed = null;
                Status = "Browser installation needs repair: " + BrowserMessageValidator.BoundText(ex.Message, "unknown failure");
            }
        }
    }

    private void Install()
    {
        if (release == null || installing != null || previewMode) return;
        installCancellation = new CancellationTokenSource();
        var token = installCancellation.Token;
        var chosen = release;
        installProgress = new("Preparing Linux Browser installation...", 0);
        installing = Task.Run(() => new LinuxBrowserRuntimeInstaller().InstallAsync(chosen,
            new InstallProgress(value => Volatile.Write(ref installProgress, value)), token), token);
    }

    private sealed class InstallProgress(Action<LinuxRuntimeInstallProgress> update) : IProgress<LinuxRuntimeInstallProgress>
    {
        public void Report(LinuxRuntimeInstallProgress value) => update(value);
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
            "An optional browser download for CrystalCast. No terminal or administrator access.");
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
            ImGui.TextWrapped(distribution.Name);
            ImGui.Spacing();
            ImGui.TextWrapped(installed == null ? "Linux Browser is not installed by CrystalCast."
                : $"Installed: Linux Browser {installed.Version} ({installed.Engine})");
            ImGui.TextWrapped(Status);
            ImGui.Spacing();
            if (installing != null)
            {
                var progress = Volatile.Read(ref installProgress);
                ImGui.ProgressBar((float)progress.Fraction, new Vector2(-1, 0));
                ImGui.TextWrapped(progress.Message);
                if (ImGui.Button("Cancel installation")) installCancellation?.Cancel();
            }
            else
            {
                ImGui.BeginDisabled(release == null || previewMode || IsChecking);
                CrystalCastUiTheme.PushPrimaryButtonStyle();
                var label = installed == null ? "Install Linux Browser" : release?.Version == installed.Version ? "Repair Linux Browser" : "Update Linux Browser";
                if (ImGui.Button(label)) Install();
                CrystalCastUiTheme.PopPrimaryButtonStyle();
                ImGui.EndDisabled();
                if (release == null)
                    ImGui.TextWrapped((previewMode ? null : LinuxBrowserRuntimeInstaller.AutomaticInstallBlocker())
                        ?? "No verified browser download has been published for this environment yet. Automatic installation is unavailable. You can select WebView2 in Browser settings meanwhile.");
                else
                    ImGui.TextWrapped($"Download: Linux Browser {release.Version}, {release.ArchiveBytes / 1048576.0:0.0} MiB. Installs only in your Linux user data directory.");
            }
            ImGui.BeginDisabled(previewMode || IsChecking || installing != null);
            if (ImGui.Button(IsChecking ? "Checking..." : "Retry detection") && !IsChecking)
                CheckAvailability(openIfMissing: true, showWhileChecking: true);
            ImGui.EndDisabled();
            ImGui.Spacing();
            ImGui.TextWrapped("CrystalCast starts and stops this browser automatically for active screens. This Linux backend remains experimental.");
            if (ImGui.CollapsingHeader("Advanced: diagnostics and developer setup"))
            {
                ImGui.TextWrapped($"Distribution metadata: {distribution.Source}");
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
                    "The optional Linux Browser download includes the helper and browser runtime. CrystalCast starts and stops it automatically for each active screen without modifying your Wine prefix.");
                CrystalCastUiTheme.DrawSectionHeader("Build the helper after installing WPE", "From the CrystalCast source checkout on the Linux host:");
                ImGui.TextWrapped(LinuxBrowserInstallationGuide.HelperInstall);
                if (ImGui.Button("Copy helper build commands")) ImGui.SetClipboardText(LinuxBrowserInstallationGuide.HelperInstall);
                ImGui.Spacing();
                WineWebView2SetupWindow.DrawCard("NativeCompatibility", "Other distributions and testing",
                    "Run as your desktop user. Keep XDG_RUNTIME_DIR and HOME available to the game launcher. Flatpak and Proton containers may need additional host integration.",
                    "Native Wine launching, audible audio and wider provider support still need testing. Choose WebView2 in Browser settings to return to the existing Wine backend.");
                if (ImGui.Button("Copy setup guide URL")) ImGui.SetClipboardText(GuideUrl);
            }
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
