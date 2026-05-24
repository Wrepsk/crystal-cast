using System.Numerics;
using CrystalCast.Rendering;
using CrystalCast.Sync;
using CrystalCast.Video;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace CrystalCast.Windows;

public sealed class MainWindow : Window, IDisposable
{
    private static readonly string[] SourceNames =
        ["Local video", "Browser screens"];

    private static readonly ScreenSourceKind[] SourceKinds =
        [ScreenSourceKind.LocalVideo, ScreenSourceKind.YouTubeBrowser];

    private readonly Plugin plugin;
    private readonly WorldScreenManager renderer;
    private readonly ScreenStateIpc ipc;
    private readonly PlacementUndoService placementUndoService = new();
    private readonly ScreenListPanel screenListPanel;
    private readonly AudioControlsPanel audioControlsPanel;
    private readonly PlacementPanel placementPanel;
    private readonly SourceControlsPanel sourceControlsPanel;

    public MainWindow(Plugin plugin, WorldScreenManager renderer, ScreenStateIpc ipc)
        : base("CrystalCast###CrystalCastMain")
    {
        this.plugin = plugin;
        this.renderer = renderer;
        this.ipc = ipc;
        screenListPanel = new ScreenListPanel(renderer);
        audioControlsPanel = new AudioControlsPanel(renderer);
        placementPanel = new PlacementPanel(renderer, placementUndoService);
        sourceControlsPanel = new SourceControlsPanel(renderer);

        Size = new Vector2(520, 560);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    public void Dispose()
    {
    }

    public override void Draw()
    {
        var changed = false;
        var config = plugin.Configuration;
        placementUndoService.BeginFrame();
        config.Normalize();
        var activeBrowserScreen = config.GetActiveBrowserScreen();

        DrawHeader();
        changed |= DrawTopControls(config, activeBrowserScreen);
        activeBrowserScreen = config.GetActiveBrowserScreen();
        changed |= DrawPlaybackShell(config, activeBrowserScreen);
        changed |= DrawMainTabs(config, activeBrowserScreen);
        changed |= placementPanel.DrawGizmo(config, activeBrowserScreen);

        if (changed)
            SaveAndPublish();
    }

    private static void DrawHeader()
    {
        ImGui.TextUnformatted("CrystalCast");
        ImGui.SameLine();
        ImGui.TextDisabled("World screen controls");
        ImGui.Separator();
    }

    private bool DrawTopControls(Configuration config, BrowserScreenProfile activeScreen)
    {
        var changed = DrawSourceCombo(config);
        if (config.SourceKind == ScreenSourceKind.YouTubeBrowser)
        {
            ImGui.Spacing();
            changed |= screenListPanel.Draw(config, activeScreen, ClearDeletedScreenUiState);
        }

        return changed;
    }

    private static bool DrawSourceCombo(Configuration config)
    {
        var changed = false;
        var current = FindSourceIndex(config.SourceKind);

        if (ImGui.BeginCombo("Source", SourceNames[current]))
        {
            for (var i = 0; i < SourceNames.Length; i++)
            {
                var selected = i == current;
                if (ImGui.Selectable(SourceNames[i], selected))
                {
                    config.SourceKind = SourceKinds[i];
                    changed = true;
                }

                if (selected)
                    ImGui.SetItemDefaultFocus();
            }

            ImGui.EndCombo();
        }

        return changed;
    }

    private bool DrawPlaybackShell(Configuration config, BrowserScreenProfile activeScreen)
    {
        var changed = false;
        var enabled = config.Enabled;
        if (ImGui.Checkbox("Plugin enabled", ref enabled))
        {
            config.Enabled = enabled;
            changed = true;
        }

        ImGui.SameLine();
        var source = SourceNames[FindSourceIndex(config.SourceKind)];
        ImGui.TextDisabled($"Source: {source}");

        if (config.SourceKind == ScreenSourceKind.YouTubeBrowser)
        {
            var telemetry = renderer.PlaybackTelemetry;
            var position = telemetry == null
                ? "0:00"
                : FormatPlaybackPosition(telemetry.PositionMs);
            var state = GetYouTubePlaybackState(activeScreen, telemetry);
            var duration = telemetry is { DurationMs: > 0 }
                ? $" / {FormatPlaybackPosition(telemetry.DurationMs)}"
                : string.Empty;
            ImGui.TextUnformatted($"{state} @ {position}{duration}");
        }
        else
        {
            var paused = config.PlaybackPaused;
            if (ImGui.Checkbox("Paused", ref paused))
            {
                config.PlaybackPaused = paused;
                changed = true;
            }
        }

        ImGui.TextDisabled(ShortStatus(renderer.SourceStatus));
        return changed;
    }

    private bool DrawMainTabs(Configuration config, BrowserScreenProfile activeScreen)
    {
        var changed = false;
        ImGui.Spacing();

        if (ImGui.BeginTabBar("CrystalCastMainTabs"))
        {
            if (ImGui.BeginTabItem("Source settings"))
            {
                ImGui.Spacing();
                changed |= sourceControlsPanel.Draw(config, activeScreen);
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Audio"))
            {
                ImGui.Spacing();
                changed |= audioControlsPanel.Draw(config, activeScreen);
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Placement"))
            {
                ImGui.Spacing();
                changed |= placementPanel.Draw(config, activeScreen);
                ImGui.EndTabItem();
            }

            ImGui.EndTabBar();
        }

        return changed;
    }

    private static int FindSourceIndex(ScreenSourceKind sourceKind)
    {
        for (var i = 0; i < SourceKinds.Length; i++)
        {
            if (SourceKinds[i] == sourceKind)
                return i;
        }

        return 0;
    }

    private static ScreenPlaybackState GetYouTubePlaybackState(BrowserScreenProfile screen, MediaPlaybackTelemetry? telemetry)
    {
        if (screen.PlaybackPaused)
            return ScreenPlaybackState.Paused;

        return telemetry?.State ?? ScreenPlaybackState.Stopped;
    }

    private static string FormatPlaybackPosition(long positionMs)
    {
        var time = TimeSpan.FromMilliseconds(Math.Max(0, positionMs));
        return time.TotalHours >= 1.0
            ? $"{(int)time.TotalHours}:{time.Minutes:00}:{time.Seconds:00}"
            : $"{time.Minutes}:{time.Seconds:00}";
    }

    private void ClearDeletedScreenUiState(string screenId)
    {
        sourceControlsPanel.ClearScreen(screenId);
        placementUndoService.Remove(screenId);
    }

    private static string ShortStatus(string status)
    {
        if (string.IsNullOrWhiteSpace(status))
            return string.Empty;

        const int maxLength = 96;
        return status.Length <= maxLength
            ? status
            : $"{status[..maxLength]}...";
    }

    private void SaveAndPublish()
    {
        ipc.PublishLocalState();
    }

}
