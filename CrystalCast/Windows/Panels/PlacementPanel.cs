using System.Numerics;
using CrystalCast.Rendering;
using CrystalCast.Windows.Style;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using static CrystalCast.Windows.Style.CrystalCastUiTheme;
using Ui = CrystalCast.Windows.Style.CrystalCastUiWidgets;

namespace CrystalCast.Windows.Panels;

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
        (ScreenPlacementMode.World, "Fixed in place", "Stays where you put it in this zone."),
        (ScreenPlacementMode.FollowPlayer, "Follows you", "Moves and turns with your character."),
        (ScreenPlacementMode.FollowCamera, "Follows your view", "Moves with your character and turns with your camera."),
        (ScreenPlacementMode.Furniture, "Attached to furniture", "Moves and turns with a placed housing item, and shows only in that house or ward."),
    ];

    /// <summary>How many of the nearest housing items the furniture list offers.</summary>
    private const int FurnitureListLimit = 40;

    private string renamingPresetId = string.Empty;
    private string presetRenameDraft = string.Empty;
    private ScreenPlacementSettings? copiedPlacement;
    private long placementCopiedAtTick;
    private long placeFailedAtTick;
    private long furnitureFailedAtTick;
    private string furnitureFailure = string.Empty;

    // Picking in the world (a surface or a piece of furniture): runs while the Placement tab is drawn and ends
    // after one pick.
    private enum PickMode
    {
        None,
        Surface,
        Furniture,
    }

    private const long HoveredFurnitureRefreshMilliseconds = 500;

    private PickMode pickMode;
    private int pickLastFrame;
    private readonly SurfaceClickTracker pickClicks = new();
    private List<HousingFurnitureItem> pickFurniture = [];
    private long pickFurnitureReadAtMs;
    private HousingFurnitureItem? hoveredFurniture;
    private SurfaceSnapPose hoveredFurniturePose;
    private long hoveredFurniturePoseAtMs;
    private readonly List<Vector2> previewOutline = [];

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

    /// <summary>Draws the in-world handles. The main window calls this only while the Placement tab is open.</summary>
    public bool DrawGizmo(Configuration config, BrowserScreenProfile activeScreen)
    {
        if (IsPlacementControlsLocked(activeScreen))
        {
            StopPicking();
            return false;
        }

        if (pickMode != PickMode.None)
        {
            // Closing the window or leaving the tab ends picking.
            var frame = ImGui.GetFrameCount();
            if (pickLastFrame != 0 && frame - pickLastFrame > 1)
                StopPicking();
            pickLastFrame = frame;
        }

        switch (pickMode)
        {
            case PickMode.Surface:
                return DrawSurfacePicker(config, activeScreen);
            case PickMode.Furniture:
                return DrawFurniturePicker(config, activeScreen);
        }

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
                if (!Ui.MenuItem($"{PlacementModes[i].Name}##PlacementMode{i}", i == mode) || PlacementModes[i].Mode == placement.Mode)
                    continue;

                if (PlacementModes[i].Mode == ScreenPlacementMode.Furniture)
                {
                    changed |= AttachToNearestFurniture(placement);
                }
                else if (placementResolver.TryConvertModePreservingWorld(placement, PlacementModes[i].Mode))
                {
                    changed = true;
                }
                else if (placement.Mode == ScreenPlacementMode.Furniture)
                {
                    ShowFurnitureFailure("Go to the house where the furniture is to detach the screen.");
                }
            }

            Ui.EndDropdown();
        }

        Ui.RowIndent(LabelWidth, LabelGap);
        Ui.Meta(PlacementModes[mode].Description);

        if (placement.Mode == ScreenPlacementMode.Furniture)
            changed |= DrawFurnitureRow(placement, config.SurfaceSnapGapMeters, locked);

        if (furnitureFailedAtTick > 0 && Environment.TickCount64 - furnitureFailedAtTick < 4000 && placement.Mode == ScreenPlacementMode.Furniture)
        {
            Ui.RowIndent(LabelWidth, LabelGap);
            Ui.ErrorText(furnitureFailure);
        }

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

        Ui.RowLabel("Snap", LabelWidth, LabelGap);
        var snapping = pickMode == PickMode.Surface;
        if (Ui.Button(snapping ? "Cancel##SurfaceSnap" : "Snap to surface##SurfaceSnap",
                snapping ? ButtonKind.Ghost : ButtonKind.Secondary,
                disabled: locked,
                on: snapping,
                tooltip: "Click a wall, floor or piece of furniture in the world to put the screen flat against it"))
        {
            TogglePicking(PickMode.Surface);
        }

        ImGui.SameLine(0.0f, S(8.0f));
        var pickingFurniture = pickMode == PickMode.Furniture;
        if (Ui.Button(pickingFurniture ? "Cancel##FurniturePick" : "Pick furniture##FurniturePick",
                pickingFurniture ? ButtonKind.Ghost : ButtonKind.Secondary,
                disabled: locked,
                on: pickingFurniture,
                tooltip: "Click a piece of housing furniture to attach the screen to it, on its front"))
        {
            if (pickingFurniture || placementResolver.ReadLocation().InHousing)
                TogglePicking(PickMode.Furniture);
            else
                ShowFurnitureFailure("Furniture can only be picked in a house, apartment or housing ward.");
        }

        ImGui.SameLine(0.0f, S(12.0f));
        var gap = config.SurfaceSnapGapMeters;
        if (Ui.DragFloat("##SurfaceSnapGap", ref gap, 0.005f, 0.0f, SurfaceSnap.MaxGapMeters, "Gap %.2f m", S(110.0f), locked,
                "How far in front of the surface the screen goes. Raise it if parts of the screen dip into the surface as the camera moves."))
        {
            config.SurfaceSnapGapMeters = Math.Clamp(gap, 0.0f, SurfaceSnap.MaxGapMeters);
            changed = true;
        }

        if (pickMode == PickMode.Surface)
        {
            Ui.RowIndent(LabelWidth, LabelGap);
            Ui.Meta("Click where the screen's center should go. Dragging still turns the camera. Furniture without collision, like rugs and paintings, can't be picked.");
        }
        else if (pickMode == PickMode.Furniture)
        {
            Ui.RowIndent(LabelWidth, LabelGap);
            Ui.Meta("Click a piece of furniture to attach the screen to its front. Dragging still turns the camera.");
        }

        if (furnitureFailedAtTick > 0 && Environment.TickCount64 - furnitureFailedAtTick < 4000 && placement.Mode != ScreenPlacementMode.Furniture)
        {
            Ui.RowIndent(LabelWidth, LabelGap);
            Ui.ErrorText(furnitureFailure);
        }

        if (placeFailedAtTick > 0 && Environment.TickCount64 - placeFailedAtTick < 3000)
        {
            Ui.RowIndent(LabelWidth, LabelGap);
            Ui.ErrorText(placement.Mode == ScreenPlacementMode.FollowCamera
                ? "Waiting for the local player and camera."
                : "Waiting for the local player.");
        }

        // The handles show on the screen whenever this tab is open; these pick what they do.
        Ui.RowLabel("Gizmo", LabelWidth, LabelGap);
        var gizmoOff = locked;
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

    /// <summary>
    /// Shows where the screen would land under the mouse and places it on a click. Nothing takes the mouse from
    /// the game, so the camera still turns and zooms; <see cref="SurfaceClickTracker"/> tells clicks from drags.
    /// </summary>
    private bool DrawSurfacePicker(Configuration config, BrowserScreenProfile screen)
    {
        var io = ImGui.GetIO();
        var mouse = io.MousePos;
        var overGame = !io.WantCaptureMouse;
        SurfaceSnapPose? pose = null;
        if (overGame
            && placementResolver.TryGetViewerPosition(out var viewer)
            && SurfaceRaycaster.TryHit(Plugin.GameGui, mouse, viewer, out var point, out var normal))
        {
            pose = SurfaceSnap.Pose(point, normal, viewer, config.SurfaceSnapGapMeters);
        }

        DrawPickPreview(screen.Placement, pose, mouse, overGame,
            pose != null ? "Click to place the screen here" : "Point at a wall, floor or furniture");

        var clicked = pickClicks.Update(SurfaceClickTracker.IsPrimaryButtonDown(), overGame, mouse, Environment.TickCount64);
        if (!clicked || pose is not { } target)
            return false;

        var placement = screen.Placement;
        var before = placement.Clone();
        StopPicking();
        // Snapping always leaves the screen fixed in place, also when it was attached to furniture (Normalize then
        // drops the item).
        placement.Mode = ScreenPlacementMode.World;
        placement.PositionX = target.Position.X;
        placement.PositionY = target.Position.Y;
        placement.PositionZ = target.Position.Z;
        placement.YawRadians = PlacementMath.NormalizeRadians(target.YawRadians);
        placement.PitchRadians = target.PitchRadians;
        placement.RollRadians = 0.0f;
        placement.Normalize();

        undoService.Capture(screen.ScreenId, before, placement);
        return true;
    }

    /// <summary>
    /// Shows which piece of furniture is under the mouse and where the screen would go on it, and attaches the
    /// screen there on a click.
    /// </summary>
    private bool DrawFurniturePicker(Configuration config, BrowserScreenProfile screen)
    {
        var io = ImGui.GetIO();
        var mouse = io.MousePos;
        var overGame = !io.WantCaptureMouse;
        var now = Environment.TickCount64;
        HousingFurnitureItem? hovered = null;
        if (overGame && placementResolver.TryGetViewerPosition(out var viewer)
            && Plugin.GameGui.ScreenToWorld(mouse, out var target, SurfaceSnap.MaxDistanceMeters)
            && Vector3.DistanceSquared(target, viewer) > 0.0001f)
        {
            var direction = Vector3.Normalize(target - viewer);
            hovered = HousingFurnitureReader.TryPickByModel(viewer, direction, out var byModel)
                ? byModel
                : PickByCollision(viewer, direction, now);
        }

        // Measuring an item's face takes a few hundred rays, so it's done once per hovered item and refreshed slowly.
        if (hovered is { } item)
        {
            var same = hoveredFurniture is { } previous && previous.Index == item.Index && previous.BaseId == item.BaseId;
            if (!same || now - hoveredFurniturePoseAtMs > HoveredFurnitureRefreshMilliseconds)
            {
                hoveredFurniturePose = ScreenPlacementResolver.FurnitureFrontPose(item, config.SurfaceSnapGapMeters);
                hoveredFurniturePoseAtMs = now;
            }
        }

        hoveredFurniture = hovered;
        DrawPickPreview(screen.Placement, hovered != null ? hoveredFurniturePose : null, mouse, overGame,
            hovered is { } named ? $"Click to attach to {FurnitureName(named.Name, named.Index)}" : "Point at a piece of furniture");

        if (!pickClicks.Update(SurfaceClickTracker.IsPrimaryButtonDown(), overGame, mouse, now) || hovered is not { } picked)
            return false;

        var placement = screen.Placement;
        var before = placement.Clone();
        StopPicking();
        if (!placementResolver.TryMoveOntoFurniture(placement, placementResolver.ReadLocation(), picked, config.SurfaceSnapGapMeters))
        {
            ShowFurnitureFailure("Couldn't move the screen onto that item.");
            return false;
        }

        undoService.Capture(screen.ScreenId, before, placement);
        return true;
    }

    /// <summary>The item standing where the mouse ray hits collision, when no model reported a hit.</summary>
    private HousingFurnitureItem? PickByCollision(Vector3 origin, Vector3 direction, long now)
    {
        if (pickFurniture.Count == 0 || now - pickFurnitureReadAtMs > HoveredFurnitureRefreshMilliseconds)
        {
            pickFurniture = placementResolver.ReadFurnitureWithNames();
            pickFurnitureReadAtMs = now;
        }

        return SurfaceRaycaster.TryCast(origin, direction, SurfaceSnap.MaxDistanceMeters, out RayHit hit)
            ? FurniturePicker.NearestToHit(pickFurniture, hit.Point, hit.Normal)
            : null;
    }

    private void TogglePicking(PickMode mode)
    {
        var start = pickMode != mode;
        StopPicking();
        if (start)
            pickMode = mode;
    }

    private void StopPicking()
    {
        pickMode = PickMode.None;
        pickLastFrame = 0;
        pickClicks.Reset();
        hoveredFurniture = null;
        pickFurniture = [];
        pickFurnitureReadAtMs = 0;
    }

    private void DrawPickPreview(ScreenPlacementSettings placement, SurfaceSnapPose? pose, Vector2 mouse, bool overGame, string hint)
    {
        var dl = ImGui.GetForegroundDrawList(ImGuiHelpers.MainViewport);
        if (pose is { } target)
        {
            var rotation = target.Rotation;
            var width = Math.Max(0.1f, placement.WidthMeters);
            var halfRight = Vector3.Transform(Vector3.UnitX, rotation) * (width * 0.5f);
            var halfUp = Vector3.Transform(Vector3.UnitY, rotation) * (width * HeightPerWidth(placement) * 0.5f);
            Span<Vector3> corners =
            [
                target.Position - halfRight + halfUp,
                target.Position + halfRight + halfUp,
                target.Position + halfRight - halfUp,
                target.Position - halfRight - halfUp,
            ];
            var color = U32(Accent);
            if (ScreenPlacementGizmo.TryGetCameraMatrices(out var view, out var projection))
            {
                PolygonProjection.Project(corners, view * projection, ImGuiHelpers.MainViewport.Pos, ImGui.GetIO().DisplaySize, previewOutline);
                if (previewOutline.Count >= 3)
                {
                    var points = previewOutline.ToArray();
                    dl.AddConvexPolyFilled(ref points[0], points.Length, U32(Accent with { W = 0.18f }));
                    dl.AddPolyline(ref points[0], points.Length, color, ImDrawFlags.Closed, S(2.0f));
                }
            }

            if (Plugin.GameGui.WorldToScreen(target.Position, out var center))
                dl.AddCircleFilled(center, S(4.0f), color);
        }

        if (!overGame)
            return;

        var at = mouse + new Vector2(S(16.0f), S(12.0f));
        dl.AddText(at + new Vector2(1.0f, 1.0f), 0xC0000000, hint);
        dl.AddText(at, U32(pose != null ? Accent : CrystalCastUiTheme.Text), hint);
    }

    private bool DrawFurnitureRow(ScreenPlacementSettings placement, float gapMeters, bool locked)
    {
        var changed = false;
        var anchor = placement.Furniture;
        var here = anchor != null && placementResolver.TryFindFurniture(anchor, out _);
        var name = FurnitureName(anchor?.Name, anchor?.Index ?? -1);

        Ui.RowLabel("Furniture", LabelWidth, LabelGap);
        if (Ui.BeginDropdown("##CrystalCastFurniture", here ? name : $"{name} (not here)", disabled: locked))
        {
            var location = placementResolver.ReadLocation();
            var items = location.InHousing ? NearestFurniture(placement, toPlayer: true) : [];
            if (items.Count == 0)
                Ui.MenuHint(location.InHousing ? "No placed furniture found here." : "Go to a house, apartment or ward to pick furniture.");

            foreach (var (item, distance) in items)
            {
                var selected = anchor != null && item.Index == anchor.Index && item.BaseId == anchor.BaseId;
                if (Ui.MenuItem($"{FurnitureName(item.Name, item.Index)}  ·  {distance:0.0} m##Furniture{item.Index}", selected) && !selected)
                {
                    if (placementResolver.TryMoveOntoFurniture(placement, location, item, gapMeters))
                        changed = true;
                    else
                        ShowFurnitureFailure("Couldn't move the screen onto that item.");
                }
            }

            Ui.EndDropdown();
        }

        Ui.RowIndent(LabelWidth, LabelGap);
        Ui.Meta(here
            ? "Listed nearest to you first. Picking one moves the screen onto its front; adjust it with Move or Rotate."
            : "The screen shows when you're where this item is placed. Use Move or Rotate to change its offset from the item.");
        return changed;
    }

    private bool AttachToNearestFurniture(ScreenPlacementSettings placement)
    {
        var location = placementResolver.ReadLocation();
        if (!location.InHousing)
        {
            ShowFurnitureFailure("Furniture can only be picked in a house, apartment or housing ward.");
            return false;
        }

        var nearest = NearestFurniture(placement, toPlayer: false);
        if (nearest.Count == 0)
        {
            ShowFurnitureFailure("No placed furniture found here.");
            return false;
        }

        if (placementResolver.TryAttachToFurniture(placement, location, nearest[0].Item))
            return true;

        ShowFurnitureFailure("Couldn't find where the screen is right now.");
        return false;
    }

    /// <summary>
    /// Housing items here, nearest first: to the player for the list, or to the screen when attaching it where it
    /// stands. Falls back to the other point when the first isn't known.
    /// </summary>
    private List<(HousingFurnitureItem Item, float Distance)> NearestFurniture(ScreenPlacementSettings placement, bool toPlayer)
    {
        Vector3? player = placementResolver.TryGetPlayerPosition(out var standingAt) ? standingAt : null;
        Vector3? screen = placementResolver.TryResolve(placement, out var world) ? world.Position : null;
        var from = (toPlayer ? player ?? screen : screen ?? player) ?? Vector3.Zero;
        return placementResolver.ReadFurnitureWithNames()
            .Select(item => (Item: item, Distance: Vector3.Distance(item.Position, from)))
            .OrderBy(entry => entry.Distance)
            .Take(FurnitureListLimit)
            .ToList();
    }

    private static string FurnitureName(string? name, int index) =>
        !string.IsNullOrWhiteSpace(name) ? name : index >= 0 ? $"Item {index + 1}" : "No furniture";

    private void ShowFurnitureFailure(string message)
    {
        furnitureFailure = message;
        furnitureFailedAtTick = Environment.TickCount64;
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
        var follow = placement.Mode is ScreenPlacementMode.FollowPlayer or ScreenPlacementMode.FollowCamera or ScreenPlacementMode.Furniture;
        Ui.SectionHeader("Position and shape", placement.Mode switch
        {
            ScreenPlacementMode.Furniture => "Offset from the furniture: right, up and forward.",
            ScreenPlacementMode.FollowPlayer or ScreenPlacementMode.FollowCamera => "Offset from you: right, up and forward.",
            _ => "Where the screen's center is in this zone.",
        });

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
