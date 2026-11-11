using System.Numerics;
using CrystalCast.Rendering;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using static CrystalCast.Windows.CrystalCastUiTheme;
using Ui = CrystalCast.Windows.CrystalCastUiWidgets;

namespace CrystalCast.Windows;

internal sealed class PlacementPanel(
    WorldScreenManager renderer,
    PlacementUndoService undoService,
    ScreenPlacementResolver placementResolver)
{
    private const float LabelWidth = MainLabelWidth;
    private const float LabelGap = MainLabelGap;
    private const float RadiansToDegrees = 180.0f / MathF.PI;

    private static readonly (ScreenPlacementMode Mode, string Name, string Description)[] PlacementModes =
    [
        (ScreenPlacementMode.World, "World", "Stays at a fixed position in the zone."),
        (ScreenPlacementMode.FollowPlayer, "Follow player", "Coordinates are relative to the local player."),
        (ScreenPlacementMode.FollowCamera, "Follow camera", "Coordinates are relative to the camera heading around the local player."),
    ];

    private string renamingPresetId = string.Empty;
    private string presetRenameDraft = string.Empty;
    private ScreenPlacementSettings? copiedPlacement;
    private long placementCopiedAtTick;
    private long placeFailedAtTick;

    public bool Draw(Configuration config, BrowserScreenProfile activeScreen)
    {
        var placementLocked = IsPlacementControlsLocked(activeScreen);
        if (placementLocked)
        {
            Ui.Banner(
                "PlacementLocked",
                BannerKind.Warning,
                $"Placement is controlled by {SourceControlUi.LockedOwner(activeScreen)}",
                "You can still copy this layout and paste it onto an editable screen.");
            ImGui.Spacing();
        }

        var before = activeScreen.Placement.Clone();
        var changed = DrawAnchor(config, activeScreen.Placement, placementLocked);
        Ui.VerticalSpace(24.0f);
        changed |= DrawTransform(activeScreen.Placement, placementLocked);
        Ui.VerticalSpace(24.0f);
        changed |= DrawLayoutTools(config, activeScreen, placementLocked);

        if (changed)
            undoService.Capture(activeScreen.ScreenId, before, activeScreen.Placement);

        return changed;
    }

    public bool DrawGizmo(Configuration config, BrowserScreenProfile activeScreen)
    {
        if (!config.PlacementGizmoEnabled)
            return false;

        if (IsPlacementControlsLocked(activeScreen))
            return false;

        var before = activeScreen.Placement.Clone();
        var options = new GizmoOptions(
            config.PlacementGizmoOperation,
            config.PlacementGizmoWorldAxes,
            Snap: ImGui.GetIO().KeyCtrl,
            HeightPerWidth(activeScreen.Placement));
        if (!ScreenPlacementGizmo.Draw(activeScreen.Placement, options, placementResolver))
            return false;

        undoService.Capture(activeScreen.ScreenId, before, activeScreen.Placement);
        return true;
    }

    private bool DrawAnchor(Configuration config, ScreenPlacementSettings placement, bool locked)
    {
        var changed = false;
        var mode = Array.FindIndex(PlacementModes, entry => entry.Mode == placement.Mode);
        if (mode < 0)
            mode = 0;

        Ui.RowLabel("Anchor", LabelWidth, LabelGap);
        if (Ui.BeginDropdown("##CrystalCastPlacementMode", PlacementModes[mode].Name, disabled: locked))
        {
            for (var i = 0; i < PlacementModes.Length; i++)
            {
                if (Ui.MenuItem($"{PlacementModes[i].Name}##PlacementMode{i}", i == mode) && PlacementModes[i].Mode != placement.Mode)
                    changed |= placementResolver.TryConvertModePreservingWorld(placement, PlacementModes[i].Mode);
            }

            Ui.EndDropdown();
        }

        Ui.RowIndent(LabelWidth, LabelGap);
        Ui.Meta(PlacementModes[mode].Description);

        Ui.RowLabel("Bring into view", LabelWidth, LabelGap);
        var placeLabel = placement.Mode == ScreenPlacementMode.FollowCamera
            ? "Place in front of camera"
            : "Place in front of player";
        if (Ui.Button(placeLabel, disabled: locked))
        {
            if (placementResolver.PlaceInFrontOfPlayer(placement))
                changed = true;
            else
                placeFailedAtTick = Environment.TickCount64;
        }

        ImGui.SameLine(0.0f, S(8.0f));
        if (Ui.Button("Face me", disabled: locked, tooltip: "Turn the screen toward your camera and level it"))
        {
            if (placementResolver.FaceCamera(placement))
                changed = true;
            else
                placeFailedAtTick = Environment.TickCount64;
        }

        if (placeFailedAtTick > 0 && Environment.TickCount64 - placeFailedAtTick < 3000)
        {
            Ui.RowIndent(LabelWidth, LabelGap);
            Ui.ErrorText(placement.Mode == ScreenPlacementMode.FollowCamera
                ? "Waiting for the local player and camera."
                : "Waiting for the local player.");
        }

        var gizmoEnabled = config.PlacementGizmoEnabled;
        Ui.RowLabel("Gizmo", LabelWidth, LabelGap);
        if (Ui.Switch("##CrystalCastPlacementGizmo", ref gizmoEnabled, locked))
        {
            config.PlacementGizmoEnabled = gizmoEnabled;
            changed = true;
        }

        ImGui.SameLine(0.0f, S(12.0f));
        var gizmoOff = !config.PlacementGizmoEnabled || locked;
        var translate = config.PlacementGizmoOperation == ScreenPlacementGizmoOperation.Translate;
        if (Ui.Button("Move##GizmoMove", ButtonKind.Ghost, disabled: gizmoOff, on: translate && !gizmoOff, tooltip: "Drag the in-world arrows to move the screen"))
        {
            config.PlacementGizmoOperation = ScreenPlacementGizmoOperation.Translate;
            changed = true;
        }

        ImGui.SameLine(0.0f, S(4.0f));
        var rotate = config.PlacementGizmoOperation == ScreenPlacementGizmoOperation.Rotate;
        if (Ui.Button("Rotate##GizmoRotate", ButtonKind.Ghost, disabled: gizmoOff, on: rotate && !gizmoOff, tooltip: "Drag the in-world rings to rotate the screen"))
        {
            config.PlacementGizmoOperation = ScreenPlacementGizmoOperation.Rotate;
            changed = true;
        }

        ImGui.SameLine(0.0f, S(4.0f));
        var resize = config.PlacementGizmoOperation == ScreenPlacementGizmoOperation.Resize;
        if (Ui.Button("Resize##GizmoResize", ButtonKind.Ghost, disabled: gizmoOff, on: resize && !gizmoOff, tooltip: "Drag the in-world handle to change the screen's width; the height follows the video"))
        {
            config.PlacementGizmoOperation = ScreenPlacementGizmoOperation.Resize;
            changed = true;
        }

        // Resizing always follows the screen's sides, so the axes choice only applies to moving and rotating.
        var axesOff = gizmoOff || resize;
        Ui.RowLabel("Gizmo axes", LabelWidth, LabelGap);
        if (Ui.Button("Screen##GizmoAxesScreen", ButtonKind.Ghost, disabled: axesOff, on: !config.PlacementGizmoWorldAxes && !axesOff,
                tooltip: "Arrows and rings follow the screen: left/right, up/down, toward/away"))
        {
            config.PlacementGizmoWorldAxes = false;
            changed = true;
        }

        ImGui.SameLine(0.0f, S(4.0f));
        if (Ui.Button("World##GizmoAxesWorld", ButtonKind.Ghost, disabled: axesOff, on: config.PlacementGizmoWorldAxes && !axesOff,
                tooltip: "Arrows and rings follow the zone's fixed directions, whichever way the screen faces"))
        {
            config.PlacementGizmoWorldAxes = true;
            changed = true;
        }

        Ui.RowIndent(LabelWidth, LabelGap);
        Ui.Meta($"Hold Ctrl while dragging to snap: {ScreenPlacementGizmo.MoveSnapMeters:0.#} m steps, {ScreenPlacementGizmo.RotateSnapDegrees:0}° turns, {ScreenPlacementGizmo.ResizeSnapMeters:0.##} m widths.");
        return changed;
    }

    /// <summary>The screen's height per meter of width: the video's shape once it has frames, else the saved size.</summary>
    private float HeightPerWidth(ScreenPlacementSettings placement)
    {
        if (renderer.TextureWidth > 0 && renderer.TextureHeight > 0)
            return (float)renderer.TextureHeight / renderer.TextureWidth;

        return placement.WidthMeters > 0.0f ? placement.HeightMeters / placement.WidthMeters : 9.0f / 16.0f;
    }

    private bool DrawTransform(ScreenPlacementSettings placement, bool locked)
    {
        var changed = false;
        var follow = placement.Mode is ScreenPlacementMode.FollowPlayer or ScreenPlacementMode.FollowCamera;
        Ui.SectionHeader("Position and shape", follow
            ? "Local offsets: right, up and forward from the anchor."
            : "World coordinates of the screen center.");

        var position = new Vector3(placement.PositionX, placement.PositionY, placement.PositionZ);
        Ui.RowLabel("Position", LabelWidth, LabelGap);
        if (Ui.DragFloat3("CrystalCastPosition", ref position, 0.01f, follow ? ["R", "U", "F"] : ["X", "Y", "Z"], "%.2f", locked))
        {
            placement.PositionX = position.X;
            placement.PositionY = position.Y;
            placement.PositionZ = position.Z;
            changed = true;
        }

        var rotation = new Vector3(placement.YawRadians, placement.PitchRadians, placement.RollRadians) * RadiansToDegrees;
        Ui.RowLabel("Rotation", LabelWidth, LabelGap);
        if (Ui.DragFloat3("CrystalCastRotation", ref rotation, 0.5f, ["Yaw", "Pitch", "Roll"], "%.1f°", locked))
        {
            placement.YawRadians = rotation.X / RadiansToDegrees;
            placement.PitchRadians = rotation.Y / RadiansToDegrees;
            placement.RollRadians = rotation.Z / RadiansToDegrees;
            changed = true;
        }

        var width = placement.WidthMeters;
        Ui.RowLabel("Width", LabelWidth, LabelGap);
        if (Ui.DragFloat("##CrystalCastWidth", ref width, 0.01f, format: "%.2f m", width: S(120.0f), disabled: locked))
        {
            placement.WidthMeters = Math.Max(0.1f, width);
            changed = true;
        }

        ImGui.SameLine(0.0f, S(12.0f));
        Ui.RowText(renderer.TextureWidth > 0 && renderer.TextureHeight > 0
                    ? $"Height {placement.WidthMeters * renderer.TextureHeight / renderer.TextureWidth:0.00} m, from the source"
                    : "Height follows the source",
                TextTertiary, f => f.Small);

        var maxCurveAmount = Math.Max(0.001f, placement.WidthMeters / MathF.PI);
        var curveAmount = Math.Clamp(placement.ScreenCurveAmountMeters, 0.0f, maxCurveAmount);
        if (Math.Abs(placement.ScreenCurveAmountMeters - curveAmount) > 0.0001f)
        {
            placement.ScreenCurveAmountMeters = curveAmount;
            changed = true;
        }

        Ui.RowLabel("Curve", LabelWidth, LabelGap);
        if (Ui.Slider("##CrystalCastCurve", ref curveAmount, 0.0f, maxCurveAmount, value => $"{value:0.00} m", detent: 0.0f, disabled: locked))
        {
            placement.ScreenCurveAmountMeters = Math.Clamp(curveAmount, 0.0f, maxCurveAmount);
            changed = true;
        }

        return changed;
    }

    private bool DrawLayoutTools(Configuration config, BrowserScreenProfile screen, bool locked)
    {
        var changed = false;
        var placement = screen.Placement;
        Ui.SectionHeader("Layouts", "Copy a layout between screens, undo recent edits, or save presets.");

        Ui.RowLabel("This layout", LabelWidth, LabelGap);
        if (Ui.Button("Copy##PlacementCopy"))
        {
            copiedPlacement = placement.Clone();
            placementCopiedAtTick = Environment.TickCount64;
        }

        ImGui.SameLine(0.0f, S(8.0f));
        if (Ui.Button("Paste##PlacementPaste", disabled: copiedPlacement == null || locked))
        {
            placement.CopyFrom(copiedPlacement!);
            placement.Normalize();
            changed = true;
        }

        ImGui.SameLine(0.0f, S(8.0f));
        changed |= undoService.DrawUndoButton(screen.ScreenId, placement, locked);

        if (placementCopiedAtTick > 0 && Environment.TickCount64 - placementCopiedAtTick < 2000)
        {
            ImGui.SameLine(0.0f, S(12.0f));
            Ui.RowText("Copied", TextTertiary, f => f.Small);
        }

        changed |= DrawPlacementPresets(config, placement, locked);
        return changed;
    }

    private bool DrawPlacementPresets(Configuration config, ScreenPlacementSettings placement, bool locked)
    {
        config.Normalize();
        var changed = false;
        var activePreset = config.GetActivePlacementPreset();

        Ui.RowLabel("Preset", LabelWidth, LabelGap);
        var buttonsWidth = Ui.ButtonWidth("Load") + Ui.ButtonWidth("Save new") + Ui.FrameHeight + S(16.0f);
        var dropdownWidth = Math.Max(S(100.0f), ImGui.GetContentRegionAvail().X - buttonsWidth - S(8.0f));
        if (Ui.BeginDropdown("##CrystalCastPlacementPreset", activePreset?.Name ?? "No presets saved", dropdownWidth))
        {
            if (config.PlacementPresets.Count == 0)
                Ui.MenuHint("Save the current layout to create one.");

            foreach (var preset in config.PlacementPresets)
            {
                if (Ui.MenuItem($"{preset.Name}##PlacementPreset{preset.PresetId}", preset.PresetId == config.ActivePlacementPresetId))
                {
                    config.ActivePlacementPresetId = preset.PresetId;
                    config.Save();
                    activePreset = preset;
                }
            }

            Ui.EndDropdown();
        }

        ImGui.SameLine(0.0f, S(8.0f));
        if (Ui.Button("Load##PresetLoad", disabled: activePreset == null || locked, tooltip: "Apply the selected preset to this screen") && activePreset != null)
        {
            placement.CopyFrom(activePreset.Placement);
            changed = true;
        }

        ImGui.SameLine(0.0f, S(8.0f));
        if (Ui.Button("Save new##PresetSave", tooltip: "Save this screen's layout as a new preset"))
        {
            var preset = config.CreatePlacementPreset(GetNextPresetName(config), placement);
            config.PlacementPresets.Add(preset);
            config.ActivePlacementPresetId = preset.PresetId;
            config.Save();
            activePreset = preset;
        }

        ImGui.SameLine(0.0f, S(8.0f));
        if (Ui.IconButton("##PresetMore", FontAwesomeIcon.EllipsisH, "More preset actions", disabled: activePreset == null))
            Ui.OpenMenu("PresetActions");

        if (Ui.BeginMenu("PresetActions", ImGui.GetItemRectMax()))
        {
            if (Ui.MenuItem("Update with this layout", icon: FontAwesomeIcon.Sync) && activePreset != null)
            {
                activePreset.Placement = placement.Clone();
                activePreset.Placement.Normalize();
                config.Save();
            }

            if (Ui.MenuItem("Rename", icon: FontAwesomeIcon.Pen) && activePreset != null)
            {
                renamingPresetId = activePreset.PresetId;
                presetRenameDraft = activePreset.Name;
            }

            Ui.MenuSeparator();
            if (Ui.MenuItem("Delete", danger: true, icon: FontAwesomeIcon.TrashAlt) && activePreset != null)
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

            Ui.EndMenu();
        }

        if (activePreset != null && renamingPresetId == activePreset.PresetId)
            DrawPresetRenameControls(config, activePreset);

        return changed;
    }

    private void DrawPresetRenameControls(Configuration config, ScreenPlacementPreset activePreset)
    {
        Ui.RowLabel("Preset name", LabelWidth, LabelGap);
        var draft = presetRenameDraft;
        var saveWidth = Ui.ButtonWidth("Save");
        var cancelWidth = Ui.ButtonWidth("Cancel");
        if (ImGui.IsWindowAppearing())
            ImGui.SetKeyboardFocusHere();
        var pressedEnter = Ui.TextField(
            "##CrystalCastPresetName",
            ref draft,
            128,
            width: Math.Max(S(80.0f), ImGui.GetContentRegionAvail().X - saveWidth - cancelWidth - S(16.0f)),
            flags: ImGuiInputTextFlags.EnterReturnsTrue);
        presetRenameDraft = draft;

        ImGui.SameLine(0.0f, S(8.0f));
        if (Ui.Button("Save##PresetRenameSave", ButtonKind.Primary) || pressedEnter)
        {
            var trimmed = presetRenameDraft.Trim();
            if (!string.IsNullOrWhiteSpace(trimmed))
            {
                activePreset.Name = GetUniquePresetName(config, trimmed, activePreset.PresetId);
                config.Save();
            }

            renamingPresetId = string.Empty;
        }

        ImGui.SameLine(0.0f, S(8.0f));
        if (Ui.Button("Cancel##PresetRenameCancel", ButtonKind.Ghost))
            renamingPresetId = string.Empty;
    }

    private static bool IsPlacementControlsLocked(BrowserScreenProfile screen)
    {
        return screen.SourceControlsLocked;
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
