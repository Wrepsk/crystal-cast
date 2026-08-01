using System.Numerics;
using CrystalCast.Rendering;
using Dalamud.Bindings.ImGui;

namespace CrystalCast.Windows;

internal sealed class PlacementPanel(WorldScreenManager renderer, PlacementUndoService undoService)
{
    private static readonly string[] PlacementModeNames =
        ["World", "Follow player", "Follow camera"];

    private static readonly ScreenPlacementMode[] PlacementModes =
        [ScreenPlacementMode.World, ScreenPlacementMode.FollowPlayer, ScreenPlacementMode.FollowCamera];

    private static readonly string[] GizmoOperationNames =
        ["Move", "Rotate"];

    private static readonly ScreenPlacementGizmoOperation[] GizmoOperations =
        [ScreenPlacementGizmoOperation.Translate, ScreenPlacementGizmoOperation.Rotate];

    private string renamingPresetId = string.Empty;
    private string presetRenameDraft = string.Empty;

    public bool Draw(Configuration config, BrowserScreenProfile activeScreen)
    {
        return DrawBrowserPlacement(config, activeScreen);
    }

    public bool DrawGizmo(Configuration config, BrowserScreenProfile activeScreen)
    {
        if (!config.PlacementGizmoEnabled)
            return false;

        if (IsPlacementControlsLocked(activeScreen))
            return false;

        var before = activeScreen.Placement.Clone();
        if (!ScreenPlacementGizmo.Draw(activeScreen.Placement, config.PlacementGizmoOperation))
            return false;

        undoService.Capture(activeScreen.ScreenId, before, activeScreen.Placement);
        return true;
    }

    private bool DrawBrowserPlacement(Configuration config, BrowserScreenProfile screen)
    {
        var placementLocked = IsPlacementControlsLocked(screen);
        if (placementLocked)
            DrawLockedControlsMessage(screen, "Placement controls");

        var before = screen.Placement.Clone();
        if (placementLocked)
            ImGui.BeginDisabled();

        var changed = DrawPlacementSettings(config, screen.ScreenId, screen.Placement);

        if (placementLocked)
            ImGui.EndDisabled();

        if (changed)
            undoService.Capture(screen.ScreenId, before, screen.Placement);

        return changed;
    }

    private bool DrawPlacementSettings(Configuration config, string undoKey, ScreenPlacementSettings placement)
    {
        var changed = false;
        changed |= undoService.DrawUndoButton(undoKey, placement);

        var placementGizmoEnabled = config.PlacementGizmoEnabled;
        if (ImGui.Checkbox("Placement gizmo", ref placementGizmoEnabled))
        {
            config.PlacementGizmoEnabled = placementGizmoEnabled;
            changed = true;
        }

        if (config.PlacementGizmoEnabled)
        {
            changed |= DrawGizmoOperation(config);
            ImGui.TextDisabled(config.PlacementGizmoOperation == ScreenPlacementGizmoOperation.Rotate
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
            config,
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

        changed |= DrawSizeAndCurve(placement);
        return changed;
    }

    private static bool DrawGizmoOperation(Configuration config)
    {
        var changed = false;
        var current = FindGizmoOperationIndex(config.PlacementGizmoOperation);
        if (ImGui.BeginCombo("Gizmo mode", GizmoOperationNames[current]))
        {
            for (var i = 0; i < GizmoOperations.Length; i++)
            {
                var selected = i == current;
                if (ImGui.Selectable(GizmoOperationNames[i], selected))
                {
                    config.PlacementGizmoOperation = GizmoOperations[i];
                    changed = true;
                }

                if (selected)
                    ImGui.SetItemDefaultFocus();
            }

            ImGui.EndCombo();
        }

        return changed;
    }

    private bool DrawPlacementPresets(
        Configuration config,
        Func<ScreenPlacementSettings> getCurrentPlacement,
        Action<ScreenPlacementSettings> applyPlacement)
    {
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
            var preset = config.CreatePlacementPreset(GetNextPresetName(config), getCurrentPlacement());
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
            renamingPresetId = activePreset.PresetId;
            presetRenameDraft = activePreset.Name;
        }

        ImGui.SameLine();
        if (ImGui.Button("Delete") && activePreset != null)
        {
            var removedId = activePreset.PresetId;
            var removedIndex = Math.Max(0, config.PlacementPresets.FindIndex(preset => preset.PresetId == removedId));
            config.PlacementPresets.RemoveAll(preset => preset.PresetId == removedId);
            if (renamingPresetId == removedId)
                renamingPresetId = string.Empty;

            config.ActivePlacementPresetId = config.PlacementPresets.Count == 0
                ? string.Empty
                : config.PlacementPresets[Math.Clamp(removedIndex - 1, 0, config.PlacementPresets.Count - 1)].PresetId;
            config.Save();
            activePreset = config.GetActivePlacementPreset();
        }

        if (!hasPreset)
            ImGui.EndDisabled();

        if (activePreset != null && renamingPresetId == activePreset.PresetId)
            DrawPresetRenameControls(config, activePreset);

        ImGui.PopID();
        return changed;
    }

    private void DrawPresetRenameControls(Configuration config, ScreenPlacementPreset activePreset)
    {
        var draft = presetRenameDraft;
        var pressedEnter = ImGui.InputText("Preset name", ref draft, 128, ImGuiInputTextFlags.EnterReturnsTrue);
        presetRenameDraft = draft;
        ImGui.SameLine();
        if (ImGui.Button("Save preset name") || pressedEnter)
        {
            var trimmed = presetRenameDraft.Trim();
            if (!string.IsNullOrWhiteSpace(trimmed))
            {
                activePreset.Name = GetUniquePresetName(config, trimmed, activePreset.PresetId);
                config.Save();
            }

            renamingPresetId = string.Empty;
        }

        ImGui.SameLine();
        if (ImGui.Button("Cancel preset rename"))
            renamingPresetId = string.Empty;
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

    private bool DrawSizeAndCurve(ScreenPlacementSettings placement)
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

    private static bool IsPlacementControlsLocked(BrowserScreenProfile screen)
    {
        return screen.SourceControlsLocked;
    }

    private static void DrawLockedControlsMessage(BrowserScreenProfile screen, string label)
    {
        ImGui.TextDisabled(string.IsNullOrWhiteSpace(screen.SourceControlsOwnerId)
            ? $"{label} locked by IPC."
            : $"{label} locked by {screen.SourceControlsOwnerId}.");
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

    private static int FindGizmoOperationIndex(ScreenPlacementGizmoOperation operation)
    {
        for (var i = 0; i < GizmoOperations.Length; i++)
        {
            if (GizmoOperations[i] == operation)
                return i;
        }

        return 0;
    }

    private static string GetNextPresetName(Configuration config)
    {
        for (var i = 1; ; i++)
        {
            var name = $"Placement {i}";
            if (config.PlacementPresets.All(preset => !string.Equals(preset.Name, name, StringComparison.OrdinalIgnoreCase)))
                return name;
        }
    }

    private static string GetUniquePresetName(Configuration config, string name, string presetIdToIgnore)
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
}
