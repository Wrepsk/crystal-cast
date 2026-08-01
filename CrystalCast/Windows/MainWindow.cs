using System.Numerics;
using CrystalCast.Rendering;
using CrystalCast.Sync;
using CrystalCast.Video;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace CrystalCast.Windows;

public sealed class MainWindow : Window, IDisposable
{
    private readonly Plugin plugin;
    private readonly WorldScreenManager renderer;
    private readonly ScreenStateIpc ipc;
    private readonly PlacementUndoService placementUndoService = new();
    private readonly ScreenListPanel screenListPanel;
    private readonly AudioControlsPanel audioControlsPanel;
    private readonly PlacementPanel placementPanel;
    private readonly SourceControlsPanel sourceControlsPanel;

    internal MainWindow(Plugin plugin, WorldScreenManager renderer, ScreenStateIpc ipc, ScreenPlacementResolver placementResolver)
        : base("CrystalCast###CrystalCastMain")
    {
        this.plugin = plugin;
        this.renderer = renderer;
        this.ipc = ipc;
        screenListPanel = new ScreenListPanel(renderer);
        audioControlsPanel = new AudioControlsPanel(renderer);
        placementPanel = new PlacementPanel(renderer, placementUndoService, placementResolver);
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
        ImGui.Spacing();
        return screenListPanel.Draw(config, activeScreen, ClearDeletedScreenUiState);
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
        ImGui.TextDisabled("Source: Browser");

        var telemetry = renderer.PlaybackTelemetry;
        var position = telemetry == null
            ? "0:00"
            : FormatPlaybackPosition(telemetry.PositionMs);
        var state = GetBrowserPlaybackState(activeScreen, telemetry);
        var duration = telemetry is { DurationMs: > 0 }
            ? $" / {FormatPlaybackPosition(telemetry.DurationMs)}"
            : string.Empty;
        ImGui.TextUnformatted($"{state} @ {position}{duration}");

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
                changed |= sourceControlsPanel.Draw(activeScreen);
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Audio"))
            {
                ImGui.Spacing();
                changed |= audioControlsPanel.Draw(activeScreen);
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

    private static ScreenPlaybackState GetBrowserPlaybackState(BrowserScreenProfile screen, MediaPlaybackTelemetry? telemetry)
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
