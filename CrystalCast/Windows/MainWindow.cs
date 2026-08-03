using System.Numerics;
using CrystalCast.Rendering;
using CrystalCast.Sync;
using CrystalCast.Video;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Windowing;

namespace CrystalCast.Windows;

public sealed class MainWindow : Window, IDisposable
{
#if DEBUG
    private const string DebugIpcOwnerId = "CrystalCast.Debug";
#endif

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
        TitleBarButtons =
        [
            new TitleBarButton
            {
                Icon = FontAwesomeIcon.Cog,
                IconOffset = new Vector2(2.0f, 1.0f),
                Click = _ => plugin.ToggleConfigUi(),
                ShowTooltip = () =>
                {
                    ImGui.BeginTooltip();
                    ImGui.TextUnformatted("Open CrystalCast settings");
                    ImGui.EndTooltip();
                },
            },
        ];
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

#if DEBUG
            if (ImGui.BeginTabItem("IPC testing"))
            {
                ImGui.Spacing();
                changed |= DrawIpcTesting(config, activeScreen);
                ImGui.EndTabItem();
            }
#endif

            ImGui.EndTabBar();
        }

        return changed;
    }

#if DEBUG
    private bool DrawIpcTesting(Configuration config, BrowserScreenProfile activeScreen)
    {
        var changed = false;
        ImGui.TextWrapped("Debug-only tools for creating screens that use the same CreatedByIpc metadata and resource limits as external integrations.");
        ImGui.Spacing();

        var ipcScreenCount = ScreenLimitPolicy.CountIpcScreens(config.BrowserScreens);
        ImGui.TextUnformatted($"IPC screens: {ipcScreenCount}/{Configuration.MaxIpcBrowserScreens}");
        ImGui.TextDisabled("New test screens copy the selected screen's provider and source settings.");

        var canCreate = ScreenLimitPolicy.CanCreateIpcScreen(config.BrowserScreens);
        if (!canCreate)
            ImGui.BeginDisabled();
        if (ImGui.Button("Create and place IPC test screen"))
        {
            var screen = CreateDebugIpcScreen(config, activeScreen);
            config.BrowserScreens.Add(screen);
            config.ActiveBrowserScreenId = screen.ScreenId;
            renderer.PlaceBrowserScreenInFrontOfPlayer(screen);
            changed = true;
        }
        if (!canCreate)
            ImGui.EndDisabled();

        if (!canCreate)
            ImGui.TextDisabled("The IPC or total screen limit has been reached.");

        ImGui.Separator();
        if (!activeScreen.CreatedByIpc)
        {
            ImGui.TextDisabled("Select an IPC-created screen to use the controls below.");
            return changed;
        }

        ImGui.TextUnformatted("Selected screen is IPC-created");
        ImGui.TextDisabled($"Owner: {(string.IsNullOrWhiteSpace(activeScreen.IpcOwnerId) ? "(none)" : activeScreen.IpcOwnerId)}");

        if (ImGui.Button("Place selected IPC screen in front"))
            changed |= renderer.PlaceBrowserScreenInFrontOfPlayer(activeScreen);

        var sourceLocked = activeScreen.SourceControlsLocked;
        if (ImGui.Checkbox("Simulate IPC source and placement lock", ref sourceLocked))
        {
            activeScreen.SourceControlsLocked = sourceLocked;
            activeScreen.SourceControlsOwnerId = sourceLocked ? DebugIpcOwnerId : string.Empty;
            changed = true;
        }
        ImGui.TextDisabled("The lock reproduces an integration-owned screen while this tab remains able to reposition it.");
        return changed;
    }

    private static BrowserScreenProfile CreateDebugIpcScreen(Configuration config, BrowserScreenProfile source)
    {
        var screen = source.CloneAsNew(GetNextDebugIpcScreenName(config));
        screen.CreatedByIpc = true;
        screen.IpcOwnerId = DebugIpcOwnerId;
        screen.SourceControlsLocked = false;
        screen.SourceControlsOwnerId = string.Empty;
        screen.Enabled = true;
        screen.SpatialAudioEnabled = true;
        return screen;
    }

    private static string GetNextDebugIpcScreenName(Configuration config)
    {
        for (var i = 1; i <= Configuration.MaxIpcBrowserScreens; i++)
        {
            var name = $"IPC test screen {i}";
            if (config.BrowserScreens.All(screen => !string.Equals(screen.Name, name, StringComparison.OrdinalIgnoreCase)))
                return name;
        }

        return $"IPC test screen {config.BrowserScreens.Count + 1}";
    }
#endif

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
