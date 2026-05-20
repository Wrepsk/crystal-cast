using System.Numerics;
using CrystalCast.Rendering;
using CrystalCast.Sync;
using CrystalCast.Video;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace CrystalCast.Windows;

public sealed class MainWindow : Window, IDisposable
{
    private const string LocalVideoPlacementUndoKey = "local-video";
    private const int PlacementUndoHistoryLimit = 32;
    private const long PlacementUndoCoalesceMilliseconds = 350;

    private static readonly (string Name, int Width, int Height)[] YouTubeResolutionPresets =
    [
        ("360p (640 x 360)", 640, 360),
        ("480p (854 x 480)", 854, 480),
        ("720p (1280 x 720)", 1280, 720),
        ("1080p (1920 x 1080)", 1920, 1080),
        ("1440p (2560 x 1440)", 2560, 1440),
        ("4K (3840 x 2160)", 3840, 2160),
    ];

    private static readonly string[] SourceNames =
        ["Local video", "YouTube browser"];

    private static readonly ScreenSourceKind[] SourceKinds =
        [ScreenSourceKind.LocalVideo, ScreenSourceKind.YouTubeBrowser];

    private static readonly string[] PlacementModeNames =
        ["World", "Follow player", "Follow camera"];

    private static readonly ScreenPlacementMode[] PlacementModes =
        [ScreenPlacementMode.World, ScreenPlacementMode.FollowPlayer, ScreenPlacementMode.FollowCamera];

    private static readonly string[] PlacementGizmoOperationNames =
        ["Move", "Rotate"];

    private static readonly ScreenPlacementGizmoOperation[] PlacementGizmoOperations =
        [ScreenPlacementGizmoOperation.Translate, ScreenPlacementGizmoOperation.Rotate];

    private readonly Plugin plugin;
    private readonly WorldScreenManager renderer;
    private readonly ScreenStateIpc ipc;
    private readonly Dictionary<string, YouTubeUiState> youtubeUiStates = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PlacementUndoHistory> placementUndoHistories = new(StringComparer.Ordinal);
    private string renamingScreenId = string.Empty;
    private string renameDraft = string.Empty;
    private string renamingPlacementPresetId = string.Empty;
    private string placementPresetRenameDraft = string.Empty;
    private string placementUndoAppliedKey = string.Empty;

    public MainWindow(Plugin plugin, WorldScreenManager renderer, ScreenStateIpc ipc)
        : base("CrystalCast###CrystalCastMain")
    {
        this.plugin = plugin;
        this.renderer = renderer;
        this.ipc = ipc;

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
        placementUndoAppliedKey = string.Empty;
        config.Normalize();
        var activeBrowserScreen = config.GetActiveBrowserScreen();

        DrawHeader();
        changed |= DrawTopControls(config, activeBrowserScreen);
        activeBrowserScreen = config.GetActiveBrowserScreen();
        changed |= DrawPlaybackShell(config, activeBrowserScreen);
        changed |= DrawMainTabs(config, activeBrowserScreen);
        changed |= DrawPlacementGizmo(config, activeBrowserScreen);

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
            changed |= DrawBrowserScreenControls(config, activeScreen);
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

    private bool DrawBrowserScreenControls(Configuration config, BrowserScreenProfile activeScreen)
    {
        var changed = false;
        var activeIndex = Math.Max(0, config.BrowserScreens.FindIndex(screen => screen.ScreenId == activeScreen.ScreenId));

        if (ImGui.BeginCombo("Screen", activeScreen.Name))
        {
            for (var i = 0; i < config.BrowserScreens.Count; i++)
            {
                var screen = config.BrowserScreens[i];
                var selected = i == activeIndex;
                if (ImGui.Selectable($"{screen.Name}##Screen{screen.ScreenId}", selected))
                {
                    config.ActiveBrowserScreenId = screen.ScreenId;
                    changed = true;
                }

                if (selected)
                    ImGui.SetItemDefaultFocus();
            }

            ImGui.EndCombo();
        }

        if (ImGui.Button("Add YouTube"))
        {
            if (config.BrowserScreens.Count < Configuration.MaxBrowserScreens)
            {
                var screen = config.CreateDefaultBrowserScreen(GetNextScreenName(config));
                config.BrowserScreens.Add(screen);
                config.ActiveBrowserScreenId = screen.ScreenId;
                renderer.PlaceBrowserScreenInFrontOfPlayer(screen);
                changed = true;
            }
        }

        ImGui.SameLine();
        if (ImGui.Button("Duplicate"))
        {
            if (config.BrowserScreens.Count < Configuration.MaxBrowserScreens)
            {
                var copy = activeScreen.CloneAsNew(GetDuplicateScreenName(config, activeScreen.Name));
                OffsetDuplicatePlacement(copy.Placement);
                config.BrowserScreens.Add(copy);
                config.ActiveBrowserScreenId = copy.ScreenId;
                changed = true;
            }
        }

        ImGui.SameLine();
        if (ImGui.Button("Rename"))
        {
            renamingScreenId = activeScreen.ScreenId;
            renameDraft = activeScreen.Name;
        }

        ImGui.SameLine();
        var canDelete = config.BrowserScreens.Count > 1;
        if (!canDelete)
            ImGui.BeginDisabled();
        if (ImGui.Button("Delete") && canDelete)
        {
            var removedId = activeScreen.ScreenId;
            config.BrowserScreens.RemoveAll(screen => screen.ScreenId == removedId);
            youtubeUiStates.Remove(removedId);
            placementUndoHistories.Remove(removedId);
            if (renamingScreenId == removedId)
                renamingScreenId = string.Empty;
            config.ActiveBrowserScreenId = config.BrowserScreens[Math.Clamp(activeIndex - 1, 0, config.BrowserScreens.Count - 1)].ScreenId;
            changed = true;
        }
        if (!canDelete)
            ImGui.EndDisabled();

        if (config.BrowserScreens.Count >= Configuration.MaxBrowserScreens)
            ImGui.TextDisabled($"Screen limit: {Configuration.MaxBrowserScreens}");

        if (renamingScreenId == activeScreen.ScreenId)
        {
            var draft = renameDraft;
            var pressedEnter = ImGui.InputText("Screen name", ref draft, 128, ImGuiInputTextFlags.EnterReturnsTrue);
            renameDraft = draft;
            ImGui.SameLine();
            if (ImGui.Button("Save name") || pressedEnter)
            {
                var trimmed = renameDraft.Trim();
                if (!string.IsNullOrWhiteSpace(trimmed))
                {
                    activeScreen.Name = trimmed;
                    changed = true;
                }

                renamingScreenId = string.Empty;
            }

            ImGui.SameLine();
            if (ImGui.Button("Cancel"))
                renamingScreenId = string.Empty;
        }

        var enabled = activeScreen.Enabled;
        if (ImGui.Checkbox("Screen enabled", ref enabled))
        {
            activeScreen.Enabled = enabled;
            changed = true;
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
                changed |= DrawSourceSettings(config, activeScreen);
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Audio"))
            {
                ImGui.Spacing();
                changed |= DrawAudioSettings(config, activeScreen);
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Placement"))
            {
                ImGui.Spacing();
                changed |= config.SourceKind == ScreenSourceKind.YouTubeBrowser
                    ? DrawPlacement(activeScreen)
                    : DrawPlacement(config);
                ImGui.EndTabItem();
            }

            ImGui.EndTabBar();
        }

        return changed;
    }

    private bool DrawYouTubeProgressBar(BrowserScreenProfile screen, MediaPlaybackTelemetry? telemetry, float width = -1.0f)
    {
        var uiState = GetYouTubeUiState(screen);
        var progressWidth = width > 0.0f
            ? width
            : ImGui.GetContentRegionAvail().X;
        var durationMs = telemetry?.DurationMs ?? 0;
        if (durationMs <= 0)
        {
            uiState.ProgressDraftSeconds = -1.0f;
            uiState.ProgressScrubbing = false;
            ImGui.ProgressBar(0.0f, new Vector2(progressWidth, 0.0f), "0:00");
            return false;
        }

        var changed = false;
        var durationSeconds = Math.Max(0.001f, durationMs / 1000.0f);
        var positionSeconds = Math.Clamp((telemetry?.PositionMs ?? 0) / 1000.0f, 0.0f, durationSeconds);
        if (uiState.ProgressDraftSeconds < 0.0f)
            uiState.ProgressDraftSeconds = positionSeconds;

        var start = ImGui.GetCursorScreenPos();
        width = Math.Max(1.0f, progressWidth);
        var height = Math.Max(16.0f, ImGui.GetFrameHeight());
        var size = new Vector2(width, height);
        ImGui.InvisibleButton($"##YouTubeProgress{screen.ScreenId}", size);

        var active = ImGui.IsItemActive();
        var hovered = ImGui.IsItemHovered();
        if (active)
        {
            var mouseX = ImGui.GetIO().MousePos.X;
            uiState.ProgressDraftSeconds = Math.Clamp((mouseX - start.X) / width * durationSeconds, 0.0f, durationSeconds);
            uiState.ProgressScrubbing = true;
        }
        else if (uiState.ProgressScrubbing)
        {
            var seekDeltaSeconds = uiState.ProgressDraftSeconds - positionSeconds;
            if (Math.Abs(seekDeltaSeconds) >= 0.25f)
            {
                renderer.TrySeekDynamicSourceBy(seekDeltaSeconds);
                changed = true;
            }

            uiState.ProgressDraftSeconds = -1.0f;
            uiState.ProgressScrubbing = false;
        }

        var displaySeconds = uiState.ProgressScrubbing
            ? Math.Clamp(uiState.ProgressDraftSeconds, 0.0f, durationSeconds)
            : positionSeconds;
        var progressFraction = Math.Clamp(displaySeconds / durationSeconds, 0.0f, 1.0f);
        var lineHeight = active || hovered ? 5.0f : 3.0f;
        var lineY = start.Y + (height * 0.5f);
        var fillX = start.X + (width * progressFraction);
        var drawList = ImGui.GetWindowDrawList();
        drawList.AddRectFilled(
            new Vector2(start.X, lineY - (lineHeight * 0.5f)),
            new Vector2(start.X + width, lineY + (lineHeight * 0.5f)),
            ImGui.GetColorU32(new Vector4(0.30f, 0.30f, 0.30f, 1.0f)),
            lineHeight * 0.5f);
        drawList.AddRectFilled(
            new Vector2(start.X, lineY - (lineHeight * 0.5f)),
            new Vector2(fillX, lineY + (lineHeight * 0.5f)),
            ImGui.GetColorU32(new Vector4(1.0f, 0.0f, 0.0f, 1.0f)),
            lineHeight * 0.5f);
        drawList.AddCircleFilled(
            new Vector2(fillX, lineY),
            active || hovered ? 6.0f : 4.0f,
            ImGui.GetColorU32(new Vector4(1.0f, 0.0f, 0.0f, 1.0f)));
        return changed;
    }

    private bool DrawPlacement(Configuration config)
    {
        var placement = config.GetLocalVideoPlacement();
        var before = placement.Clone();
        var changed = DrawPlacementSettings(LocalVideoPlacementUndoKey, placement);
        if (changed)
        {
            CapturePlacementUndo(LocalVideoPlacementUndoKey, before, placement);
            config.ApplyLocalVideoPlacement(placement);
        }

        return changed;
    }

    private bool DrawPlacement(BrowserScreenProfile screen)
    {
        var before = screen.Placement.Clone();
        var changed = DrawPlacementSettings(screen.ScreenId, screen.Placement);
        if (changed)
            CapturePlacementUndo(screen.ScreenId, before, screen.Placement);

        return changed;
    }

    private bool DrawPlacementSettings(string undoKey, ScreenPlacementSettings placement)
    {
        var changed = false;
        changed |= DrawPlacementUndo(undoKey, placement);

        var placementGizmoEnabled = plugin.Configuration.PlacementGizmoEnabled;
        if (ImGui.Checkbox("Placement gizmo", ref placementGizmoEnabled))
        {
            plugin.Configuration.PlacementGizmoEnabled = placementGizmoEnabled;
            changed = true;
        }

        if (plugin.Configuration.PlacementGizmoEnabled)
        {
            changed |= DrawPlacementGizmoOperation(plugin.Configuration);
            ImGui.TextDisabled(plugin.Configuration.PlacementGizmoOperation == ScreenPlacementGizmoOperation.Rotate
                ? "Drag the in-world rings to rotate the active screen."
                : "Drag the in-world arrows to move the active screen.");
        }

        changed |= DrawPlacementMode(placement);

        var placeButtonLabel = placement.Mode == ScreenPlacementMode.FollowCamera
            ? "Place in front of camera"
            : "Place in front of player";
        if (ImGui.Button(placeButtonLabel))
        {
            if (ScreenPlacementResolver.PlaceInFrontOfPlayer(placement))
                changed = true;
            else
                ImGui.TextColored(
                    new Vector4(1.0f, 0.45f, 0.35f, 1.0f),
                    placement.Mode == ScreenPlacementMode.FollowCamera
                        ? "Waiting for local player/camera"
                        : "Waiting for local player");
        }

        changed |= DrawPlacementPresets(
            () => placement,
            placement.CopyFrom);

        var followMode = placement.Mode is ScreenPlacementMode.FollowPlayer or ScreenPlacementMode.FollowCamera;
        var positionLabel = followMode
            ? "Local position (right / up / forward)"
            : "Position";
        var position = new Vector3(placement.PositionX, placement.PositionY, placement.PositionZ);
        if (ImGui.InputFloat3(positionLabel, ref position))
        {
            placement.PositionX = position.X;
            placement.PositionY = position.Y;
            placement.PositionZ = position.Z;
            changed = true;
        }

        var rotationLabel = followMode
            ? "Local yaw / pitch / roll"
            : "Yaw / Pitch / Roll";
        var rotation = new Vector3(placement.YawRadians, placement.PitchRadians, placement.RollRadians);
        if (ImGui.InputFloat3(rotationLabel, ref rotation))
        {
            placement.YawRadians = rotation.X;
            placement.PitchRadians = rotation.Y;
            placement.RollRadians = rotation.Z;
            changed = true;
        }

        changed |= DrawPlacementSizeAndCurve(placement);
        return changed;
    }

    private bool DrawPlacementGizmo(Configuration config, BrowserScreenProfile activeScreen)
    {
        if (!config.PlacementGizmoEnabled)
            return false;

        if (config.SourceKind == ScreenSourceKind.YouTubeBrowser)
        {
            var before = activeScreen.Placement.Clone();
            if (!ScreenPlacementGizmo.Draw(activeScreen.Placement, config.PlacementGizmoOperation))
                return false;

            CapturePlacementUndo(activeScreen.ScreenId, before, activeScreen.Placement);
            return true;
        }

        var placement = config.GetLocalVideoPlacement();
        if (!ScreenPlacementGizmo.Draw(placement, config.PlacementGizmoOperation))
            return false;

        var beforeLocal = config.GetLocalVideoPlacement();
        CapturePlacementUndo(LocalVideoPlacementUndoKey, beforeLocal, placement);
        config.ApplyLocalVideoPlacement(placement);
        return true;
    }

    private bool DrawPlacementUndo(string undoKey, ScreenPlacementSettings placement)
    {
        var history = GetPlacementUndoHistory(undoKey);
        var canUndo = history.Snapshots.Count > 0;
        if (!canUndo)
            ImGui.BeginDisabled();

        var changed = false;
        if (ImGui.Button("Undo placement") && canUndo)
        {
            var snapshot = history.Snapshots[^1];
            placement.CopyFrom(snapshot);
            history.Snapshots.RemoveAt(history.Snapshots.Count - 1);
            history.LastChangeUnixMs = 0;
            placementUndoAppliedKey = undoKey;
            changed = true;
        }

        if (!canUndo)
            ImGui.EndDisabled();

        return changed;
    }

    private static bool DrawPlacementGizmoOperation(Configuration config)
    {
        var changed = false;
        var current = FindPlacementGizmoOperationIndex(config.PlacementGizmoOperation);
        if (ImGui.BeginCombo("Gizmo mode", PlacementGizmoOperationNames[current]))
        {
            for (var i = 0; i < PlacementGizmoOperations.Length; i++)
            {
                var selected = i == current;
                if (ImGui.Selectable(PlacementGizmoOperationNames[i], selected))
                {
                    config.PlacementGizmoOperation = PlacementGizmoOperations[i];
                    changed = true;
                }

                if (selected)
                    ImGui.SetItemDefaultFocus();
            }

            ImGui.EndCombo();
        }

        return changed;
    }

    private void CapturePlacementUndo(string undoKey, ScreenPlacementSettings before, ScreenPlacementSettings after)
    {
        if (placementUndoAppliedKey == undoKey || !PlacementDiffers(before, after))
            return;

        var history = GetPlacementUndoHistory(undoKey);
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var shouldPush = history.Snapshots.Count == 0
            || history.LastChangeUnixMs == 0
            || now - history.LastChangeUnixMs > PlacementUndoCoalesceMilliseconds;

        if (shouldPush)
        {
            PushPlacementUndo(history, before);
        }

        history.LastChangeUnixMs = now;
    }

    private PlacementUndoHistory GetPlacementUndoHistory(string undoKey)
    {
        if (placementUndoHistories.TryGetValue(undoKey, out var history))
            return history;

        history = new PlacementUndoHistory();
        placementUndoHistories[undoKey] = history;
        return history;
    }

    private static void PushPlacementUndo(PlacementUndoHistory history, ScreenPlacementSettings placement)
    {
        if (history.Snapshots.Count > 0 && !PlacementDiffers(history.Snapshots[^1], placement))
            return;

        history.Snapshots.Add(placement.Clone());
        if (history.Snapshots.Count > PlacementUndoHistoryLimit)
            history.Snapshots.RemoveRange(0, history.Snapshots.Count - PlacementUndoHistoryLimit);
    }

    private static bool PlacementDiffers(ScreenPlacementSettings left, ScreenPlacementSettings right)
    {
        const float epsilon = 0.0001f;
        return left.Mode != right.Mode
            || MathF.Abs(left.PositionX - right.PositionX) > epsilon
            || MathF.Abs(left.PositionY - right.PositionY) > epsilon
            || MathF.Abs(left.PositionZ - right.PositionZ) > epsilon
            || MathF.Abs(left.YawRadians - right.YawRadians) > epsilon
            || MathF.Abs(left.PitchRadians - right.PitchRadians) > epsilon
            || MathF.Abs(left.RollRadians - right.RollRadians) > epsilon
            || MathF.Abs(left.WidthMeters - right.WidthMeters) > epsilon
            || MathF.Abs(left.HeightMeters - right.HeightMeters) > epsilon
            || MathF.Abs(left.ScreenCurveAmountMeters - right.ScreenCurveAmountMeters) > epsilon
            || MathF.Abs(left.OccludedAlpha - right.OccludedAlpha) > epsilon
            || MathF.Abs(left.OcclusionTolerance - right.OcclusionTolerance) > epsilon
            || left.EnableDistanceFade != right.EnableDistanceFade
            || MathF.Abs(left.FadeStartMeters - right.FadeStartMeters) > epsilon
            || MathF.Abs(left.FadeStopMeters - right.FadeStopMeters) > epsilon;
    }

    private bool DrawPlacementPresets(Func<ScreenPlacementSettings> getCurrentPlacement, Action<ScreenPlacementSettings> applyPlacement)
    {
        var config = plugin.Configuration;
        config.Normalize();
        var changed = false;
        var activePreset = config.GetActivePlacementPreset();
        var presetLabel = activePreset?.Name ?? "No presets";

        ImGui.Spacing();
        ImGui.PushID("PlacementPresets");
        if (ImGui.BeginCombo("Placement preset", presetLabel))
        {
            if (config.PlacementPresets.Count == 0)
            {
                ImGui.TextDisabled("No presets saved");
            }
            else
            {
                foreach (var preset in config.PlacementPresets)
                {
                    var selected = preset.PresetId == config.ActivePlacementPresetId;
                    if (ImGui.Selectable($"{preset.Name}##PlacementPreset{preset.PresetId}", selected))
                    {
                        config.ActivePlacementPresetId = preset.PresetId;
                        config.Save();
                        activePreset = preset;
                    }

                    if (selected)
                        ImGui.SetItemDefaultFocus();
                }
            }

            ImGui.EndCombo();
        }

        if (ImGui.Button("Save New"))
        {
            var preset = config.CreatePlacementPreset(GetNextPlacementPresetName(config), getCurrentPlacement());
            config.PlacementPresets.Add(preset);
            config.ActivePlacementPresetId = preset.PresetId;
            config.Save();
            activePreset = preset;
        }

        var hasPreset = activePreset != null;
        ImGui.SameLine();
        if (!hasPreset)
            ImGui.BeginDisabled();
        if (ImGui.Button("Update") && activePreset != null)
        {
            activePreset.Placement = getCurrentPlacement().Clone();
            activePreset.Placement.Normalize();
            config.Save();
        }

        ImGui.SameLine();
        if (ImGui.Button("Load") && activePreset != null)
        {
            applyPlacement(activePreset.Placement);
            changed = true;
        }

        ImGui.SameLine();
        if (ImGui.Button("Rename") && activePreset != null)
        {
            renamingPlacementPresetId = activePreset.PresetId;
            placementPresetRenameDraft = activePreset.Name;
        }

        ImGui.SameLine();
        if (ImGui.Button("Delete") && activePreset != null)
        {
            var removedId = activePreset.PresetId;
            var removedIndex = Math.Max(0, config.PlacementPresets.FindIndex(preset => preset.PresetId == removedId));
            config.PlacementPresets.RemoveAll(preset => preset.PresetId == removedId);
            if (renamingPlacementPresetId == removedId)
                renamingPlacementPresetId = string.Empty;

            config.ActivePlacementPresetId = config.PlacementPresets.Count == 0
                ? string.Empty
                : config.PlacementPresets[Math.Clamp(removedIndex - 1, 0, config.PlacementPresets.Count - 1)].PresetId;
            config.Save();
            activePreset = config.GetActivePlacementPreset();
        }

        if (!hasPreset)
            ImGui.EndDisabled();

        if (activePreset != null && renamingPlacementPresetId == activePreset.PresetId)
        {
            var draft = placementPresetRenameDraft;
            var pressedEnter = ImGui.InputText("Preset name", ref draft, 128, ImGuiInputTextFlags.EnterReturnsTrue);
            placementPresetRenameDraft = draft;
            ImGui.SameLine();
            if (ImGui.Button("Save preset name") || pressedEnter)
            {
                var trimmed = placementPresetRenameDraft.Trim();
                if (!string.IsNullOrWhiteSpace(trimmed))
                {
                    activePreset.Name = GetUniquePlacementPresetName(config, trimmed, activePreset.PresetId);
                    config.Save();
                }

                renamingPlacementPresetId = string.Empty;
            }

            ImGui.SameLine();
            if (ImGui.Button("Cancel preset rename"))
                renamingPlacementPresetId = string.Empty;
        }

        ImGui.PopID();
        return changed;
    }

    private static bool DrawPlacementMode(ScreenPlacementSettings placement)
    {
        var changed = false;
        var current = FindPlacementModeIndex(placement.Mode);
        if (ImGui.BeginCombo("Placement mode", PlacementModeNames[current]))
        {
            for (var i = 0; i < PlacementModes.Length; i++)
            {
                var mode = PlacementModes[i];
                var selected = i == current;
                if (ImGui.Selectable(PlacementModeNames[i], selected) && mode != placement.Mode)
                    changed |= ScreenPlacementResolver.TryConvertModePreservingWorld(placement, mode);

                if (selected)
                    ImGui.SetItemDefaultFocus();
            }

            ImGui.EndCombo();
        }

        if (placement.Mode == ScreenPlacementMode.FollowPlayer)
            ImGui.TextDisabled("Coordinates are relative to the local player.");
        else if (placement.Mode == ScreenPlacementMode.FollowCamera)
            ImGui.TextDisabled("Coordinates are relative to the camera heading around the local player.");

        return changed;
    }

    private bool DrawPlacementSizeAndCurve(ScreenPlacementSettings placement)
    {
        var changed = false;
        var width = placement.WidthMeters;
        if (ImGui.InputFloat("Width meters", ref width, 0.1f, 0.5f))
        {
            placement.WidthMeters = Math.Max(0.1f, width);
            changed = true;
        }

        ImGui.TextDisabled(renderer.TextureWidth > 0 && renderer.TextureHeight > 0
            ? $"Auto height: {placement.WidthMeters * renderer.TextureHeight / renderer.TextureWidth:0.###} m"
            : "Auto height: waiting for texture");

        var maxCurveAmount = Math.Max(0.001f, placement.WidthMeters / MathF.PI);
        var curveAmount = Math.Clamp(placement.ScreenCurveAmountMeters, 0.0f, maxCurveAmount);
        if (Math.Abs(placement.ScreenCurveAmountMeters - curveAmount) > 0.0001f)
        {
            placement.ScreenCurveAmountMeters = curveAmount;
            changed = true;
        }

        if (ImGui.SliderFloat("Curve amount", ref curveAmount, 0.0f, maxCurveAmount))
        {
            placement.ScreenCurveAmountMeters = Math.Clamp(curveAmount, 0.0f, maxCurveAmount);
            changed = true;
        }

        return changed;
    }

    private bool DrawSourceSettings(Configuration config, BrowserScreenProfile activeScreen)
    {
        var changed = false;
        switch (config.SourceKind)
        {
            case ScreenSourceKind.LocalVideo:
                changed |= DrawLocalVideoSource(config);
                break;
            case ScreenSourceKind.YouTubeBrowser:
                changed |= DrawYouTubeSource(activeScreen);
                break;
        }

        return changed;
    }

    private bool DrawAudioSettings(Configuration config, BrowserScreenProfile activeScreen)
    {
        var changed = false;
        switch (config.SourceKind)
        {
            case ScreenSourceKind.LocalVideo:
                changed |= DrawLocalVideoAudio(config);
                changed |= DrawSpatialAudio(config);
                break;
            case ScreenSourceKind.YouTubeBrowser:
                changed |= DrawYouTubeAudio(activeScreen);
                changed |= DrawSpatialAudio(activeScreen);
                break;
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

    private static int FindPlacementModeIndex(ScreenPlacementMode mode)
    {
        for (var i = 0; i < PlacementModes.Length; i++)
        {
            if (PlacementModes[i] == mode)
                return i;
        }

        return 0;
    }

    private static int FindPlacementGizmoOperationIndex(ScreenPlacementGizmoOperation operation)
    {
        for (var i = 0; i < PlacementGizmoOperations.Length; i++)
        {
            if (PlacementGizmoOperations[i] == operation)
                return i;
        }

        return 0;
    }

    private bool DrawYouTubeSource(BrowserScreenProfile screen)
    {
        var changed = false;
        var uiState = GetYouTubeUiState(screen);
        var fps = screen.YouTubeCaptureFps;
        var autoplay = screen.YouTubeAutoplay;
        var loop = screen.LoopYouTube;
        var rate = screen.YouTubePlaybackRate;

        if (!string.Equals(uiState.UrlDraftSource, screen.YouTubeUrl, StringComparison.Ordinal))
        {
            uiState.UrlDraft = screen.YouTubeUrl;
            uiState.UrlDraftSource = screen.YouTubeUrl;
        }

        var committedVideoIdValid = YouTubeVideoId.TryParse(screen.YouTubeUrl, out var committedVideoId);
        var draft = uiState.UrlDraft;
        var pressedEnter = ImGui.InputText("YouTube URL / ID", ref draft, 1024, ImGuiInputTextFlags.EnterReturnsTrue);
        uiState.UrlDraft = draft;
        var draftVideoIdValid = YouTubeVideoId.TryParse(uiState.UrlDraft, out var draftVideoId);

        ImGui.SameLine();
        if (ImGui.Button("Load") || pressedEnter)
        {
            if (draftVideoIdValid)
            {
                screen.YouTubeUrl = uiState.UrlDraft.Trim();
                uiState.UrlDraftSource = screen.YouTubeUrl;
                screen.PlaybackPaused = false;
                changed = true;
            }
        }

        if (draftVideoIdValid)
            ImGui.TextDisabled($"Video ID: {draftVideoId}");
        else if (!string.IsNullOrWhiteSpace(uiState.UrlDraft))
            ImGui.TextColored(new Vector4(1.0f, 0.45f, 0.35f, 1.0f), "Video ID: invalid");
        else if (committedVideoIdValid)
            ImGui.TextDisabled($"Current video ID: {committedVideoId}");
        else
            ImGui.TextDisabled("Video ID: empty");

        changed |= DrawYouTubePlaybackControls(screen);
        changed |= DrawYouTubeResolutionPreset(screen);

        var manualFps = screen.YouTubeCaptureFpsManual;
        if (ImGui.Checkbox("Set capture FPS manually", ref manualFps))
        {
            screen.YouTubeCaptureFpsManual = manualFps;
            changed = true;
        }

        if (screen.YouTubeCaptureFpsManual)
        {
            if (ImGui.InputFloat("Capture FPS", ref fps, 1.0f, 5.0f))
            {
                screen.YouTubeCaptureFps = Math.Clamp(fps, 1.0f, 120.0f);
                changed = true;
            }
        }
        else
        {
            var detectedFps = renderer.GetDetectedVideoFps(screen);
            var autoFps = detectedFps > 0.0f ? detectedFps : 60.0f;
            ImGui.TextDisabled($"Capture FPS: {autoFps:0.#} ({(detectedFps > 0.0f ? "auto-detected" : "default")})");
        }

        if (ImGui.Checkbox("Autoplay on load", ref autoplay))
        {
            screen.YouTubeAutoplay = autoplay;
            changed = true;
        }

        if (ImGui.Checkbox("Loop YouTube video", ref loop))
        {
            screen.LoopYouTube = loop;
            changed = true;
        }

        if (ImGui.SliderFloat("Playback rate", ref rate, 0.25f, 2.0f))
        {
            screen.YouTubePlaybackRate = Math.Clamp(rate, 0.25f, 2.0f);
            changed = true;
        }

        return changed;
    }

    private static bool DrawYouTubeResolutionPreset(BrowserScreenProfile screen)
    {
        var current = FindYouTubeResolutionPreset(screen.YouTubeBrowserWidth, screen.YouTubeBrowserHeight);
        var currentLabel = current >= 0
            ? YouTubeResolutionPresets[current].Name
            : $"Custom ({screen.YouTubeBrowserWidth} x {screen.YouTubeBrowserHeight})";

        if (!ImGui.BeginCombo("Browser resolution", currentLabel))
            return false;

        var changed = false;
        for (var i = 0; i < YouTubeResolutionPresets.Length; i++)
        {
            var preset = YouTubeResolutionPresets[i];
            var selected = i == current;
            if (ImGui.Selectable(preset.Name, selected))
            {
                screen.YouTubeBrowserWidth = preset.Width;
                screen.YouTubeBrowserHeight = preset.Height;
                changed = true;
            }

            if (selected)
                ImGui.SetItemDefaultFocus();
        }

        ImGui.EndCombo();
        return changed;
    }

    private static int FindYouTubeResolutionPreset(int width, int height)
    {
        for (var i = 0; i < YouTubeResolutionPresets.Length; i++)
        {
            var preset = YouTubeResolutionPresets[i];
            if (preset.Width == width && preset.Height == height)
                return i;
        }

        return -1;
    }

    private bool DrawYouTubePlaybackControls(BrowserScreenProfile screen)
    {
        var changed = false;
        var telemetry = renderer.PlaybackTelemetry;
        var position = telemetry == null
            ? "0:00"
            : FormatPlaybackPosition(telemetry.PositionMs);
        var duration = telemetry is { DurationMs: > 0 }
            ? $" / {FormatPlaybackPosition(telemetry.DurationMs)}"
            : string.Empty;
        var state = GetYouTubePlaybackState(screen, telemetry);
        var isPlaying = state == ScreenPlaybackState.Playing;

        ImGui.TextDisabled($"Playback: {state} @ {position}{duration}");

        var buttonSize = ImGui.GetFrameHeight();
        var toggleLabel = isPlaying
            ? "##YouTubePlayPause"
            : "##YouTubePlayPause";
        if (ImGui.Button(toggleLabel, new Vector2(buttonSize, buttonSize)))
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
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(isPlaying ? "Pause" : "Play");

        ImGui.SameLine();
        var spacing = ImGui.GetStyle().ItemSpacing.X;
        var progressWidth = Math.Max(48.0f, ImGui.GetContentRegionAvail().X - buttonSize - spacing);
        changed |= DrawYouTubeProgressBar(screen, telemetry, progressWidth);

        ImGui.SameLine();
        if (ImGui.Button("##YouTubeRestart", new Vector2(buttonSize, buttonSize)))
        {
            screen.PlaybackPaused = false;
            renderer.TryRestartDynamicSource();
            changed = true;
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Restart");

        return changed;
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

    private static bool DrawLocalVideoSource(Configuration config)
    {
        var changed = false;
        var ffmpegPath = config.FfmpegPath;
        var videoPath = config.LocalVideoPath;
        var scalePercent = config.LocalVideoScalePercent;
        var fps = config.LocalVideoFps;
        var loop = config.LoopLocalVideo;

        if (ImGui.InputText("FFmpeg path", ref ffmpegPath, 512))
        {
            config.FfmpegPath = ffmpegPath;
            changed = true;
        }

        if (ImGui.InputText("Video path", ref videoPath, 1024))
        {
            config.LocalVideoPath = videoPath;
            changed = true;
        }

        if (ImGui.SliderFloat("Scale percent", ref scalePercent, 5.0f, 200.0f))
        {
            config.LocalVideoScalePercent = Math.Clamp(scalePercent, 5.0f, 200.0f);
            changed = true;
        }

        if (ImGui.InputFloat("Output FPS", ref fps, 1.0f, 5.0f))
        {
            config.LocalVideoFps = Math.Clamp(fps, 1.0f, 120.0f);
            changed = true;
        }

        if (ImGui.Checkbox("Loop video", ref loop))
        {
            config.LoopLocalVideo = loop;
            changed = true;
        }

        return changed;
    }

    private static bool DrawLocalVideoAudio(Configuration config)
    {
        var changed = false;
        var audioEnabled = config.AudioEnabled;
        var audioVolume = config.AudioVolume;

        ImGui.TextUnformatted("Playback audio");
        if (ImGui.Checkbox("Enable local video audio", ref audioEnabled))
        {
            config.AudioEnabled = audioEnabled;
            changed = true;
        }

        if (config.AudioEnabled && ImGui.SliderFloat("Audio volume", ref audioVolume, 0.0f, 1.0f))
        {
            config.AudioVolume = Math.Clamp(audioVolume, 0.0f, 1.0f);
            changed = true;
        }

        return changed;
    }

    private static bool DrawYouTubeAudio(BrowserScreenProfile screen)
    {
        var changed = false;
        var audioEnabled = screen.YouTubeAudioEnabled;
        var volume = screen.YouTubeVolume;

        ImGui.TextUnformatted("Playback audio");
        if (ImGui.Checkbox("Enable browser audio", ref audioEnabled))
        {
            screen.YouTubeAudioEnabled = audioEnabled;
            changed = true;
        }

        if (screen.YouTubeAudioEnabled && ImGui.SliderFloat("YouTube volume", ref volume, 0.0f, 1.0f))
        {
            screen.YouTubeVolume = Math.Clamp(volume, 0.0f, 1.0f);
            changed = true;
        }

        return changed;
    }

    private bool DrawSpatialAudio(Configuration config)
    {
        var changed = false;
        var enabled = config.SpatialAudioEnabled;
        var fullRadius = config.SpatialAudioFullVolumeRadiusMeters;
        var silentRadius = config.SpatialAudioSilentRadiusMeters;

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.TextUnformatted("Distance falloff");
        if (ImGui.Checkbox("Spatial audio", ref enabled))
        {
            config.SpatialAudioEnabled = enabled;
            changed = true;
        }

        if (config.SpatialAudioEnabled)
        {
            if (ImGui.InputFloat("Full volume radius", ref fullRadius, 0.5f, 2.0f))
            {
                config.SpatialAudioFullVolumeRadiusMeters = Math.Max(0.0f, fullRadius);
                if (config.SpatialAudioSilentRadiusMeters <= config.SpatialAudioFullVolumeRadiusMeters)
                    config.SpatialAudioSilentRadiusMeters = config.SpatialAudioFullVolumeRadiusMeters + 0.1f;
                changed = true;
            }

            if (ImGui.InputFloat("Silent radius", ref silentRadius, 0.5f, 2.0f))
            {
                config.SpatialAudioSilentRadiusMeters = Math.Max(config.SpatialAudioFullVolumeRadiusMeters + 0.1f, silentRadius);
                changed = true;
            }

            ImGui.TextDisabled($"Distance: {renderer.AudioDistanceMeters:0.0} m  Falloff: {FormatPercent(renderer.SpatialAudioAttenuation)}");
            ImGui.TextDisabled($"Applied volume: {FormatPercent(renderer.EffectiveAudioVolume)}");
        }
        else
        {
            ImGui.TextDisabled("Distance falloff disabled");
        }

        return changed;
    }

    private bool DrawSpatialAudio(BrowserScreenProfile screen)
    {
        var changed = false;
        var enabled = screen.SpatialAudioEnabled;
        var fullRadius = screen.SpatialAudioFullVolumeRadiusMeters;
        var silentRadius = screen.SpatialAudioSilentRadiusMeters;

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.TextUnformatted("Distance falloff");
        if (ImGui.Checkbox("Spatial audio", ref enabled))
        {
            screen.SpatialAudioEnabled = enabled;
            changed = true;
        }

        if (screen.SpatialAudioEnabled)
        {
            if (ImGui.InputFloat("Full volume radius", ref fullRadius, 0.5f, 2.0f))
            {
                screen.SpatialAudioFullVolumeRadiusMeters = Math.Max(0.0f, fullRadius);
                if (screen.SpatialAudioSilentRadiusMeters <= screen.SpatialAudioFullVolumeRadiusMeters)
                    screen.SpatialAudioSilentRadiusMeters = screen.SpatialAudioFullVolumeRadiusMeters + 0.1f;
                changed = true;
            }

            if (ImGui.InputFloat("Silent radius", ref silentRadius, 0.5f, 2.0f))
            {
                screen.SpatialAudioSilentRadiusMeters = Math.Max(screen.SpatialAudioFullVolumeRadiusMeters + 0.1f, silentRadius);
                changed = true;
            }

            ImGui.TextDisabled($"Distance: {renderer.AudioDistanceMeters:0.0} m  Falloff: {FormatPercent(renderer.SpatialAudioAttenuation)}");
            ImGui.TextDisabled($"Applied volume: {FormatPercent(renderer.EffectiveAudioVolume)}");
        }
        else
        {
            ImGui.TextDisabled("Distance falloff disabled");
        }

        return changed;
    }

    private YouTubeUiState GetYouTubeUiState(BrowserScreenProfile screen)
    {
        if (youtubeUiStates.TryGetValue(screen.ScreenId, out var state))
            return state;

        state = new YouTubeUiState
        {
            UrlDraft = screen.YouTubeUrl,
            UrlDraftSource = screen.YouTubeUrl,
        };
        youtubeUiStates[screen.ScreenId] = state;
        return state;
    }

    private static string GetNextScreenName(Configuration config)
    {
        for (var i = 1; i <= Configuration.MaxBrowserScreens; i++)
        {
            var name = $"YouTube screen {i}";
            if (config.BrowserScreens.All(screen => !string.Equals(screen.Name, name, StringComparison.OrdinalIgnoreCase)))
                return name;
        }

        return $"YouTube screen {config.BrowserScreens.Count + 1}";
    }

    private static string GetNextPlacementPresetName(Configuration config)
    {
        for (var i = 1; ; i++)
        {
            var name = $"Placement {i}";
            if (config.PlacementPresets.All(preset => !string.Equals(preset.Name, name, StringComparison.OrdinalIgnoreCase)))
                return name;
        }
    }

    private static string GetUniquePlacementPresetName(Configuration config, string name, string presetIdToIgnore)
    {
        if (config.PlacementPresets.All(preset =>
                preset.PresetId == presetIdToIgnore || !string.Equals(preset.Name, name, StringComparison.OrdinalIgnoreCase)))
            return name;

        for (var i = 2; ; i++)
        {
            var candidate = $"{name} {i}";
            if (config.PlacementPresets.All(preset =>
                    preset.PresetId == presetIdToIgnore || !string.Equals(preset.Name, candidate, StringComparison.OrdinalIgnoreCase)))
                return candidate;
        }
    }

    private static string GetDuplicateScreenName(Configuration config, string sourceName)
    {
        var baseName = string.IsNullOrWhiteSpace(sourceName) ? "YouTube screen" : $"{sourceName} copy";
        if (config.BrowserScreens.All(screen => !string.Equals(screen.Name, baseName, StringComparison.OrdinalIgnoreCase)))
            return baseName;

        for (var i = 2; i <= Configuration.MaxBrowserScreens; i++)
        {
            var name = $"{baseName} {i}";
            if (config.BrowserScreens.All(screen => !string.Equals(screen.Name, name, StringComparison.OrdinalIgnoreCase)))
                return name;
        }

        return $"{baseName} {config.BrowserScreens.Count + 1}";
    }

    private static void OffsetDuplicatePlacement(ScreenPlacementSettings placement)
    {
        var right = new Vector3(MathF.Cos(placement.YawRadians), 0.0f, -MathF.Sin(placement.YawRadians));
        placement.PositionX += right.X * 0.35f;
        placement.PositionZ += right.Z * 0.35f;
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

    private static string FormatPercent(float value)
    {
        if (!float.IsFinite(value) || value <= 0.0f)
            return "0%";

        var percent = value * 100.0f;
        return percent < 1.0f
            ? "<1%"
            : $"{percent:0}%";
    }

    private void SaveAndPublish()
    {
        ipc.PublishLocalState();
    }

    private sealed class YouTubeUiState
    {
        public string UrlDraft { get; set; } = string.Empty;
        public string UrlDraftSource { get; set; } = string.Empty;
        public float ProgressDraftSeconds { get; set; } = -1.0f;
        public bool ProgressScrubbing { get; set; }
    }

    private sealed class PlacementUndoHistory
    {
        public List<ScreenPlacementSettings> Snapshots { get; } = [];
        public long LastChangeUnixMs { get; set; }
    }
}
