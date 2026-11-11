using System.Numerics;
using CrystalCast.Playlist;
using CrystalCast.Rendering;
using CrystalCast.Sync;
using CrystalCast.Video;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Windowing;
using static CrystalCast.Windows.CrystalCastUiTheme;
using Ui = CrystalCast.Windows.CrystalCastUiWidgets;

namespace CrystalCast.Windows;

public sealed class MainWindow : Window, IDisposable
{
#if DEBUG
    private const string DebugIpcOwnerId = "CrystalCast.Debug";
    private static readonly string[] TabLabels = ["Source", "Audio", "Playlist", "Placement", "IPC testing"];
#else
    private static readonly string[] TabLabels = ["Source", "Audio", "Playlist", "Placement"];
#endif

    /// <summary>The Placement tab; the in-world gizmo shows only while it is open.</summary>
    private const int PlacementTab = 3;

    private const float ArtworkHeight = 260.0f;
    private const float TransportHeight = 52.0f;

    private readonly Plugin plugin;
    private readonly WorldScreenManager renderer;
    private readonly ScreenStateIpc ipc;
    private readonly IpcScreenDeleteWindow ipcScreenDeleteWindow;
    private readonly PlacementUndoService placementUndoService = new();
    private readonly ScreenListPanel screenListPanel;
    private readonly AudioControlsPanel audioControlsPanel;
    private readonly PlacementPanel placementPanel;
    private readonly PlaylistPanel playlistPanel;
    private readonly SourceControlsPanel sourceControlsPanel;
    private readonly Dictionary<string, SourceProgressUiState> progressStates = new(StringComparer.Ordinal);
    private readonly ComReferenceHold previewHold = new();
    private float volumeReveal;
    private double volumeLastHoveredAt = double.NegativeInfinity;
    private int selectedTab;

    internal MainWindow(
        Plugin plugin,
        WorldScreenManager renderer,
        ScreenStateIpc ipc,
        ScreenPlacementResolver placementResolver,
        ScreenPlaylistAdvancer playlistAdvancer,
        PlaylistMetadataService playlistMetadata,
        IpcScreenDeleteWindow ipcScreenDeleteWindow)
        : base("CrystalCast###CrystalCastMain", ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse)
    {
        this.plugin = plugin;
        this.renderer = renderer;
        this.ipc = ipc;
        this.ipcScreenDeleteWindow = ipcScreenDeleteWindow;
        screenListPanel = new ScreenListPanel(renderer, playlistMetadata);
        audioControlsPanel = new AudioControlsPanel(renderer);
        placementPanel = new PlacementPanel(renderer, placementUndoService, placementResolver);
        playlistPanel = new PlaylistPanel(playlistAdvancer, ipc, playlistMetadata);
        sourceControlsPanel = new SourceControlsPanel(renderer);

        Size = new Vector2(580, 920);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(440, 560),
            MaximumSize = new Vector2(1200, 2000),
        };
    }

    public void Dispose()
    {
        previewHold.Dispose();
    }

    public override void OnClose()
    {
        previewHold.Hold(0);
    }

    public override void PreDraw()
    {
        PushWindowStyle(Vector2.Zero);
    }

    public override void PostDraw()
    {
        PopWindowStyle();
    }

    public override void Draw()
    {
        var changed = false;
        var config = plugin.Configuration;
        placementUndoService.BeginFrame();
        config.Normalize();

        changed |= DrawTitleBar(config);
        var activeScreen = config.GetActiveBrowserScreen();
        changed |= DrawPlayerHeader(config, activeScreen);
        if (activeScreen != null)
            changed |= DrawTransport(config, activeScreen);

        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, S(MainGutter, MainGutter));
        var bodyOpen = ImGui.BeginChild("##CrystalCastMainBody", Vector2.Zero, false, ImGuiWindowFlags.AlwaysUseWindowPadding);
        ImGui.PopStyleVar();
        if (bodyOpen)
        {
            changed |= DrawBody(config, activeScreen);
        }

        ImGui.EndChild();

        activeScreen = config.GetActiveBrowserScreen();
        if (activeScreen != null && selectedTab == PlacementTab)
            changed |= placementPanel.DrawGizmo(config, activeScreen);

        if (changed)
            SaveAndPublish();
    }

    private bool DrawTitleBar(Configuration config)
    {
        var changed = false;
        var start = ImGui.GetCursorScreenPos();
        var width = ImGui.GetWindowWidth();
        var height = S(TitleBarHeight);
        var dl = ImGui.GetWindowDrawList();

        CrystalArtwork.DrawLogo(dl, new Vector2(start.X + S(22.0f), start.Y + (height * 0.5f)));
        using (Ui.Font(f => f.SemiBold))
        {
            dl.AddText(new Vector2(start.X + S(38.0f), start.Y + ((height - ImGui.GetTextLineHeight()) * 0.5f)), U32(CrystalCastUiTheme.Text), "CrystalCast");
        }

        // Right side: Plugin switch, settings, close.
        float pluginLabelWidth;
        using (Ui.Font(f => f.Small))
            pluginLabelWidth = ImGui.CalcTextSize("Plugin").X;
        var right = start.X + width - S(8.0f);
        var closeX = right - Ui.FrameHeight;
        var settingsX = closeX - S(4.0f) - Ui.FrameHeight;
        var switchX = settingsX - S(10.0f) - S(34.0f);
        var labelX = switchX - S(8.0f) - pluginLabelWidth;
        var rowY = start.Y + ((height - Ui.FrameHeight) * 0.5f);

        ImGui.SetCursorScreenPos(new Vector2(labelX, rowY));
        var enabled = config.Enabled;
        if (Ui.SwitchLabeled("##PluginEnabled", "Plugin", ref enabled, labelFirst: true,
                tooltip: enabled ? "Turn every screen off and release their browsers" : "Turn CrystalCast back on"))
        {
            config.Enabled = enabled;
            changed = true;
        }

        ImGui.SetCursorScreenPos(new Vector2(settingsX, rowY));
        if (Ui.IconButton("##OpenSettings", FontAwesomeIcon.SlidersH, "Open settings"))
            plugin.ToggleConfigUi();

        ImGui.SetCursorScreenPos(new Vector2(closeX, rowY));
        if (Ui.IconButton("##CloseMain", FontAwesomeIcon.Times, "Close window"))
            IsOpen = false;

        ImGui.SetCursorScreenPos(start);
        ImGui.Dummy(new Vector2(width, height));
        ImGui.SetCursorScreenPos(new Vector2(start.X, start.Y + height));
        return changed;
    }

    private bool DrawPlayerHeader(Configuration config, BrowserScreenProfile? screen)
    {
        var changed = false;
        var start = ImGui.GetCursorScreenPos();
        var width = ImGui.GetWindowWidth();
        var height = S(ArtworkHeight);
        // Stay inside the 1 px window border.
        var min = start + new Vector2(1.0f, 0.0f);
        var max = start + new Vector2(width - 1.0f, height);
        var dl = ImGui.GetWindowDrawList();

        var telemetry = screen == null ? null : renderer.PlaybackTelemetry;
        var state = screen == null ? ScreenPlaybackState.Stopped : SourceControlUi.GetPlaybackState(screen, telemetry);
        var live = screen != null && config.Enabled && screen.Enabled;
        var playing = live && state == ScreenPlaybackState.Playing;

        var showingVideo = live && DrawLiveFrame(dl, screen!, min, max);
        if (!showingVideo)
            CrystalArtwork.DrawHeader(dl, min, max, playing);
        dl.PushClipRect(min, max, true);
        if (!playing)
            dl.AddRectFilled(min, max, U32(WithAlpha(Base, 0.38f)));
        var fadeStart = min.Y + (height * 0.40f);
        var fadeMid = min.Y + (height * 0.86f);
        dl.AddRectFilledMultiColor(new Vector2(min.X, fadeStart), new Vector2(max.X, fadeMid), U32(WithAlpha(Base, 0.0f)), U32(WithAlpha(Base, 0.0f)), U32(WithAlpha(Base, 0.9f)), U32(WithAlpha(Base, 0.9f)));
        dl.AddRectFilledMultiColor(new Vector2(min.X, fadeMid), max, U32(WithAlpha(Base, 0.9f)), U32(WithAlpha(Base, 0.9f)), U32(Base), U32(Base));
        dl.PopClipRect();

        // Title and status, bottom-left; Screen switch, bottom-right.
        var gutter = S(MainGutter);
        var bottom = max.Y - S(14.0f);
        float statusHeight;
        using (Ui.Font(f => f.Small))
            statusHeight = ImGui.GetTextLineHeight();
        float titleHeight;
        using (Ui.Font(f => f.PlayerTitle))
            titleHeight = ImGui.GetTextLineHeight();

        var switchWidth = 0.0f;
        if (screen != null)
        {
            using (Ui.Font(f => f.Small))
                switchWidth = ImGui.CalcTextSize("Screen").X + S(8.0f) + S(34.0f);
        }

        var titleWidth = width - (gutter * 2.0f) - switchWidth - S(16.0f);
        var titleY = bottom - statusHeight - S(6.0f) - titleHeight;
        using (Ui.Font(f => f.PlayerTitle))
        {
            dl.PushClipRect(new Vector2(min.X + gutter, titleY), new Vector2(min.X + gutter + titleWidth, bottom), true);
            dl.AddText(new Vector2(min.X + gutter, titleY), U32(TitleText), Ui.Ellipsize(HeaderTitle(screen, live), titleWidth));
            dl.PopClipRect();
        }

        ImGui.SetCursorScreenPos(new Vector2(min.X + gutter, bottom - statusHeight));
        if (screen == null)
        {
            Ui.StatusLine(TextTertiary, "Add a screen", "it appears in front of you");
        }
        else
        {
            var (dot, word) = DescribeStatus(config, screen, state);
            var details = live
                ? $"{screen.Name} · {sourceControlsPanel.DescribeSource(screen, playing)}"
                : $"{screen.Name} · {BrowserSourceControlsPanel.ProviderName(screen.ProviderKind)}";
            using (Ui.Font(f => f.Small))
                details = Ui.Ellipsize(details, titleWidth - S(90.0f));
            Ui.StatusLine(dot, word, details, disabled: !live);

            ImGui.SetCursorScreenPos(new Vector2(max.X - gutter - switchWidth, bottom - Ui.FrameHeight + S(5.0f)));
            var screenEnabled = screen.Enabled;
            if (Ui.SwitchLabeled("##ScreenEnabled", "Screen", ref screenEnabled, labelFirst: true,
                    tooltip: "Turn only this screen on or off"))
            {
                screen.Enabled = screenEnabled;
                changed = true;
            }
        }

        ImGui.SetCursorScreenPos(start);
        ImGui.Dummy(new Vector2(width, height));
        ImGui.SetCursorScreenPos(new Vector2(start.X, start.Y + height));
        return changed;
    }

    /// <summary>The video or page title reported by the player, or the screen name until one is known.</summary>
    private string HeaderTitle(BrowserScreenProfile? screen, bool live)
    {
        if (screen == null)
            return "No screens yet";

        var title = live ? renderer.PlaybackTelemetry?.Title : null;
        return string.IsNullOrWhiteSpace(title) ? screen.Name : title.Trim();
    }

    /// <summary>
    /// Draws the screen's current video frame over the header, cropped to cover it. Returns false when there is
    /// no real frame yet, so the crystal artwork is shown instead.
    /// </summary>
    private bool DrawLiveFrame(ImDrawListPtr dl, BrowserScreenProfile screen, Vector2 min, Vector2 max)
    {
        if (!renderer.TryGetPreviewTexture(screen, out var view, out var textureWidth, out var textureHeight)
            || textureWidth <= 0
            || textureHeight <= 0)
        {
            previewHold.Hold(0);
            return false;
        }

        // ImGui only records the pointer; the renderer may replace this view before the frame is rendered.
        previewHold.Hold(view);

        var size = max - min;
        var textureAspect = (float)textureWidth / textureHeight;
        var areaAspect = size.X / Math.Max(1.0f, size.Y);
        Vector2 uv0, uv1;
        if (textureAspect > areaAspect)
        {
            var visible = areaAspect / textureAspect;
            uv0 = new Vector2((1.0f - visible) * 0.5f, 0.0f);
            uv1 = new Vector2(1.0f - uv0.X, 1.0f);
        }
        else
        {
            var visible = textureAspect / areaAspect;
            uv0 = new Vector2(0.0f, (1.0f - visible) * 0.5f);
            uv1 = new Vector2(1.0f, 1.0f - uv0.Y);
        }

        dl.AddRectFilled(min, max, U32(new Vector4(0.0f, 0.0f, 0.0f, 1.0f)));
        dl.AddImage(new ImTextureID(view), min, max, uv0, uv1);
        return true;
    }

    private (Vector4 Dot, string Word) DescribeStatus(Configuration config, BrowserScreenProfile screen, ScreenPlaybackState state)
    {
        if (!config.Enabled)
            return (CrystalCastUiTheme.TextDisabled, "Plugin off");
        if (!screen.Enabled)
            return (CrystalCastUiTheme.TextDisabled, "Screen off");

        var word = state switch
        {
            ScreenPlaybackState.Playing => "Playing",
            ScreenPlaybackState.Paused => "Paused",
            _ => "Stopped",
        };

        if (SourceControlUi.TryDescribeSourceProblem(renderer.SourceStatus, out var kind, out _, out _))
            return (kind == BannerKind.Error ? Error : Warning, word);

        return (state == ScreenPlaybackState.Playing ? Accent : TextTertiary, word);
    }

    private bool DrawTransport(Configuration config, BrowserScreenProfile screen)
    {
        var changed = false;
        var start = ImGui.GetCursorScreenPos();
        var width = ImGui.GetWindowWidth();
        var height = S(TransportHeight);
        var dl = ImGui.GetWindowDrawList();
        dl.AddRectFilled(start + new Vector2(1.0f, 0.0f), start + new Vector2(width - 1.0f, height), U32(Raised));

        var telemetry = renderer.PlaybackTelemetry;
        var state = SourceControlUi.GetPlaybackState(screen, telemetry);
        var isPlaying = state == ScreenPlaybackState.Playing;
        var locked = SourceControlUi.IsSourceControlsLocked(screen);
        var canControl = config.Enabled && screen.Enabled && !locked && BrowserSourceControlsPanel.HasSource(screen);
        var gutter = S(MainGutter);
        var gap = S(12.0f);

        var x = start.X + gutter;
        var playSize = S(36.0f);
        ImGui.SetCursorScreenPos(new Vector2(x, start.Y + ((height - playSize) * 0.5f)));
        var playTooltip = locked
            ? $"Locked by {SourceControlUi.LockedOwner(screen)}"
            : !BrowserSourceControlsPanel.HasSource(screen)
                ? "Load a source first"
                : isPlaying ? "Pause" : "Play";
        if (Ui.PlayButton("##PlayPause", isPlaying && canControl, !canControl, playTooltip))
        {
            if (isPlaying)
            {
                screen.PlaybackPaused = true;
                renderer.TryPauseDynamicSource();
            }
            else
            {
                screen.PlaybackPaused = false;
                renderer.TryPlayDynamicSource();
            }

            changed = true;
        }

        x += playSize + gap;
        var rowY = start.Y + ((height - Ui.FrameHeight) * 0.5f);
        ImGui.SetCursorScreenPos(new Vector2(x, rowY));
        if (Ui.IconButton("##Restart", FontAwesomeIcon.Undo, locked ? $"Locked by {SourceControlUi.LockedOwner(screen)}" : "Restart", disabled: !canControl))
        {
            screen.PlaybackPaused = false;
            renderer.TryRestartDynamicSource();
            changed = true;
        }

        x += Ui.FrameHeight + gap;
        var timeWidth = S(46.0f);
        var progress = GetProgressState(screen.ScreenId);
        var hasDuration = telemetry is { DurationMs: > 0 };
        // The volume slider slides out of the speaker icon on hover and pushes the seek bar shorter.
        var volumeSliderWidth = S(84.0f);
        var volumeRevealWidth = MathF.Round(volumeSliderWidth * EaseOutCubic(volumeReveal));
        var volumeWidth = Ui.FrameHeight + (volumeRevealWidth >= 1.0f ? S(4.0f) + volumeRevealWidth : 0.0f);
        var seekWidth = Math.Max(S(40.0f), (start.X + width - gutter) - x - (timeWidth * 2.0f) - (gap * 3.0f) - volumeWidth);

        // Seek first so the elapsed readout can follow the cap while scrubbing.
        ImGui.SetCursorScreenPos(new Vector2(x + timeWidth + gap, rowY));
        changed |= SourceControlUi.DrawSeekBar($"{screen.ProviderKind}{screen.ScreenId}", progress, renderer, telemetry, seekWidth, canControl, out var displaySeconds);

        using (Ui.Font(f => f.Mono))
        {
            var textY = start.Y + ((height - ImGui.GetTextLineHeight()) * 0.5f);
            var elapsed = SourceControlUi.FormatPlaybackPosition((long)(displaySeconds * 1000.0f));
            var elapsedWidth = ImGui.CalcTextSize(elapsed).X;
            dl.AddText(new Vector2(x + timeWidth - elapsedWidth, textY), U32(isPlaying ? CrystalCastUiTheme.Text : TextSecondary), elapsed);

            var total = hasDuration ? SourceControlUi.FormatPlaybackPosition(telemetry!.DurationMs) : "--:--";
            dl.AddText(new Vector2(x + timeWidth + gap + seekWidth + gap, textY), U32(TextTertiary), total);
        }

        var volumeMin = new Vector2(start.X + width - gutter - volumeWidth, rowY);
        ImGui.SetCursorScreenPos(volumeMin);
        changed |= AudioControlsPanel.DrawCompactVolume(screen, "Transport", volumeSliderWidth, volumeRevealWidth, out var volumeDragging);
        UpdateVolumeReveal(volumeMin, new Vector2(start.X + width - gutter, rowY + Ui.FrameHeight), volumeDragging);

        ImGui.SetCursorScreenPos(start);
        ImGui.Dummy(new Vector2(width, height));
        ImGui.SetCursorScreenPos(new Vector2(start.X, start.Y + height));
        return changed;
    }

    private const float VolumeRevealSeconds = 0.15f;
    private const double VolumeCloseDelaySeconds = 0.4;

    /// <summary>Opens the volume slider while the pointer is over it (or dragging), and closes it shortly after.</summary>
    private void UpdateVolumeReveal(Vector2 min, Vector2 max, bool dragging)
    {
        var now = ImGui.GetTime();
        var hovered = ImGui.IsWindowHovered(ImGuiHoveredFlags.ChildWindows | ImGuiHoveredFlags.AllowWhenBlockedByActiveItem)
            && ImGui.IsMouseHoveringRect(min - S(4.0f, 8.0f), max + S(4.0f, 8.0f), false);
        if (hovered || dragging)
            volumeLastHoveredAt = now;

        var target = now - volumeLastHoveredAt < VolumeCloseDelaySeconds ? 1.0f : 0.0f;
        var step = ImGui.GetIO().DeltaTime / VolumeRevealSeconds;
        volumeReveal = target > volumeReveal
            ? Math.Min(target, volumeReveal + step)
            : Math.Max(target, volumeReveal - step);
    }

    private static float EaseOutCubic(float t)
    {
        var inverse = 1.0f - Math.Clamp(t, 0.0f, 1.0f);
        return 1.0f - (inverse * inverse * inverse);
    }

    private bool DrawBody(Configuration config, BrowserScreenProfile? activeScreen)
    {
        var changed = false;
        var sectionGap = 24.0f;

        if (activeScreen != null && config.Enabled && activeScreen.Enabled
            && SourceControlUi.TryDescribeSourceProblem(renderer.SourceStatus, out var kind, out var title, out var detail))
        {
            if (Ui.Banner("SourceProblem", kind, title, detail, "Copy details"))
                ImGui.SetClipboardText(renderer.SourceStatus);
            Ui.VerticalSpace(sectionGap);
        }
        else if (!config.Enabled)
        {
            Ui.Banner("PluginOff", BannerKind.Warning, "CrystalCast is off", "Every screen is hidden and its browser released. Turn Plugin on in the title bar to resume.");
            Ui.VerticalSpace(sectionGap);
        }

        changed |= screenListPanel.Draw(config, activeScreen, OnScreenDeleted, AskBeforeDeletingIpcScreen);
        activeScreen = config.GetActiveBrowserScreen();
        if (activeScreen == null)
            return changed;

        Ui.VerticalSpace(sectionGap);
        selectedTab = Ui.Tabs("CrystalCastMainTabs", TabLabels, Math.Clamp(selectedTab, 0, TabLabels.Length - 1));
        Ui.VerticalSpace(16.0f);

        changed |= selectedTab switch
        {
            0 => sourceControlsPanel.Draw(activeScreen),
            1 => audioControlsPanel.Draw(activeScreen),
            2 => playlistPanel.Draw(activeScreen),
            PlacementTab => placementPanel.Draw(config, activeScreen),
#if DEBUG
            4 => DrawIpcTesting(config, activeScreen),
#endif
            _ => false,
        };

        return changed;
    }

#if DEBUG
    private bool DrawIpcTesting(Configuration config, BrowserScreenProfile activeScreen)
    {
        const float labelWidth = MainLabelWidth;
        const float labelGap = MainLabelGap;
        var changed = false;
        Ui.Meta("Debug-only tools for creating screens that use the same CreatedByIpc metadata and resource limits as external integrations.");
        Ui.VerticalSpace(16.0f);

        var ipcScreenCount = ScreenLimitPolicy.CountIpcScreens(config.BrowserScreens);
        var canCreate = ScreenLimitPolicy.CanCreateIpcScreen(config.BrowserScreens);
        Ui.RowLabel("IPC screens", labelWidth, labelGap);
        Ui.RowText($"{ipcScreenCount} / {Configuration.MaxIpcBrowserScreens}", CrystalCastUiTheme.Text, f => f.Mono);

        Ui.RowLabel("Test screen", labelWidth, labelGap);
        if (Ui.Button("Create and place", ButtonKind.Primary, disabled: !canCreate,
                tooltip: canCreate ? "Copies the selected screen's provider and source settings" : "The IPC or total screen limit has been reached"))
        {
            var screen = CreateDebugIpcScreen(config, activeScreen);
            config.BrowserScreens.Add(screen);
            config.ActiveBrowserScreenId = screen.ScreenId;
            renderer.PlaceBrowserScreenInFrontOfPlayer(screen);
            changed = true;
        }

        Ui.RowIndent(labelWidth, labelGap);
        Ui.Meta(canCreate
            ? "New test screens copy the selected screen's provider and source settings."
            : "The IPC or total screen limit has been reached.");

        Ui.VerticalSpace(24.0f);
        Ui.SectionHeader("Selected screen", activeScreen.CreatedByIpc
            ? $"IPC-created · owner {(string.IsNullOrWhiteSpace(activeScreen.IpcOwnerId) ? "(none)" : activeScreen.IpcOwnerId)}"
            : "Select an IPC-created screen to use these controls.");

        var isIpc = activeScreen.CreatedByIpc;
        Ui.RowLabel("Placement", labelWidth, labelGap);
        if (Ui.Button("Place in front", disabled: !isIpc))
            changed |= renderer.PlaceBrowserScreenInFrontOfPlayer(activeScreen);

        var sourceLocked = activeScreen.SourceControlsLocked;
        Ui.RowLabel("Simulate lock", labelWidth, labelGap);
        if (Ui.Switch("##SimulateIpcLock", ref sourceLocked, !isIpc))
        {
            activeScreen.SourceControlsLocked = sourceLocked;
            activeScreen.SourceControlsOwnerId = sourceLocked ? DebugIpcOwnerId : string.Empty;
            changed = true;
        }

        Ui.RowIndent(labelWidth, labelGap);
        Ui.Meta("Locks source and placement controls like an integration-owned screen; this tab can still reposition it.");

        var allowUserAdds = activeScreen.PlaylistAllowUserAdds;
        Ui.RowLabel("Playlist edits", labelWidth, labelGap);
        if (Ui.SwitchLabeled("##SimulateIpcPlaylistAdds", "Allow user to add videos", ref allowUserAdds, disabled: !isIpc))
        {
            activeScreen.PlaylistAllowUserAdds = allowUserAdds;
            changed = true;
        }

        Ui.RowIndent(labelWidth, labelGap);
        Ui.Meta("Like playlistAllowUserAdds in Screen.Update. The rest of the Playlist tab stays blocked on IPC screens.");
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

    private SourceProgressUiState GetProgressState(string screenId)
    {
        if (!progressStates.TryGetValue(screenId, out var state))
        {
            state = new SourceProgressUiState();
            progressStates[screenId] = state;
        }

        return state;
    }

    private void AskBeforeDeletingIpcScreen(BrowserScreenProfile screen)
    {
        var screenId = screen.ScreenId;
        ipcScreenDeleteWindow.Ask(screen, hide =>
        {
            var config = plugin.Configuration;
            var current = config.BrowserScreens.FirstOrDefault(candidate => candidate.ScreenId == screenId);
            if (current == null)
                return;

            screenListPanel.DeleteScreen(config, current, hide, OnScreenDeleted);
            config.Save();
            SaveAndPublish();
        });
    }

    private void OnScreenDeleted(BrowserScreenProfile screen, bool hide)
    {
        ClearDeletedScreenUiState(screen.ScreenId);
        if (screen.CreatedByIpc)
            ipc.OnUserDeletedScreen(screen, hide);
    }

    private void ClearDeletedScreenUiState(string screenId)
    {
        sourceControlsPanel.ClearScreen(screenId);
        playlistPanel.ClearScreen(screenId);
        placementUndoService.Remove(screenId);
        progressStates.Remove(screenId);
    }

    private void SaveAndPublish()
    {
        ipc.PublishLocalState();
    }
}
