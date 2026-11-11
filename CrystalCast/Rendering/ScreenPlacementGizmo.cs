using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Bindings.ImGuizmo;
using Dalamud.Interface.Utility;
using FFXIVClientStructs.FFXIV.Client.Game.Control;

namespace CrystalCast.Rendering;

/// <summary>What the in-world gizmo shows and how it snaps.</summary>
internal readonly record struct GizmoOptions(
    ScreenPlacementGizmoOperation Operation,
    bool WorldAxes,
    bool Snap,
    float HeightPerWidth);

internal static class ScreenPlacementGizmo
{
    /// <summary>Ctrl snapping steps: meters when moving, degrees when rotating, meters of width when resizing.</summary>
    internal const float MoveSnapMeters = 0.1f;
    internal const float RotateSnapDegrees = 15.0f;
    internal const float ResizeSnapMeters = 0.25f;
    internal const float MinimumWidthMeters = 0.1f;

    public static bool Draw(ScreenPlacementSettings placement, GizmoOptions options, ScreenPlacementResolver placementResolver)
    {
        if (!placementResolver.TryResolve(placement, out var resolved))
            return false;

        if (!TryGetCameraMatrices(out var view, out var projection))
            return false;

        ImGuiHelpers.ForceNextWindowMainViewport();
        ImGuiHelpers.SetNextWindowPosRelativeMainViewport(Vector2.Zero, ImGuiCond.Always);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);

        var changed = false;
        const ImGuiWindowFlags windowFlags =
            ImGuiWindowFlags.NoBackground
            | ImGuiWindowFlags.NoDocking
            | ImGuiWindowFlags.NoFocusOnAppearing
            | ImGuiWindowFlags.NoNav
            | ImGuiWindowFlags.NoTitleBar
            | ImGuiWindowFlags.NoInputs;

        try
        {
            var open = ImGui.Begin("CrystalCast placement gizmo###CrystalCastPlacementGizmo", windowFlags);
            try
            {
                if (!open)
                    return false;

                var io = ImGui.GetIO();
                ImGui.SetWindowSize(io.DisplaySize);
                changed = DrawGizmo(placement, options, resolved, ImGui.GetWindowPos(), io.DisplaySize, ref view, ref projection, placementResolver);
            }
            finally
            {
                ImGui.End();
            }
        }
        finally
        {
            ImGui.PopStyleVar();
        }

        return changed;
    }

    private static bool DrawGizmo(
        ScreenPlacementSettings placement,
        GizmoOptions options,
        ResolvedScreenPlacement resolved,
        Vector2 position,
        Vector2 size,
        ref Matrix4x4 view,
        ref Matrix4x4 projection,
        ScreenPlacementResolver placementResolver)
    {
        ImGuizmo.BeginFrame();
        ImGuizmo.SetDrawlist();
        ImGuizmo.Enable(true);
        ImGuizmo.SetID((int)ImGui.GetID("CrystalCastPlacementGizmo"));
        ImGuizmo.SetOrthographic(false);
        ImGuizmo.SetRect(position.X, position.Y, size.X, size.Y);

        var resizing = options.Operation == ScreenPlacementGizmoOperation.Resize;
        var width = Math.Max(MinimumWidthMeters, placement.WidthMeters);
        var height = width * options.HeightPerWidth;
        var matrix = Matrix4x4.CreateFromQuaternion(resolved.Rotation);
        if (resizing)
            matrix = Matrix4x4.CreateScale(width, height, 1.0f) * matrix;
        matrix.Translation = resolved.Position;

        // Moves and turns snap through ImGuizmo; widths are rounded here so they land on whole steps.
        var snap = options.Snap && !resizing
            ? options.Operation == ScreenPlacementGizmoOperation.Rotate
                ? new Vector3(RotateSnapDegrees)
                : new Vector3(MoveSnapMeters)
            : Vector3.Zero;

        try
        {
            if (!Manipulate(options, ref view.M11, ref projection.M11, ref matrix.M11, ref snap.X))
                return false;

            // Holding the mouse still must not move the screen. Small frame-to-frame differences (camera,
            // a followed player, rounding through yaw/pitch/roll) would otherwise make it twitch.
            if (ImGui.GetIO().MouseDelta == Vector2.Zero)
                return false;

            switch (options.Operation)
            {
                case ScreenPlacementGizmoOperation.Rotate:
                    return Matrix4x4.Decompose(matrix, out _, out var worldRotation, out _)
                        && placementResolver.TryApplyWorldRotationPreservingMode(placement, worldRotation);
                case ScreenPlacementGizmoOperation.Resize:
                    if (!Matrix4x4.Decompose(matrix, out var scale, out _, out _))
                        return false;

                    if (ResizedWidth(scale.X, options.Snap) is not { } newWidth
                        || MathF.Abs(newWidth - placement.WidthMeters) < 0.0001f)
                        return false;

                    placement.WidthMeters = newWidth;
                    placement.HeightMeters = newWidth * options.HeightPerWidth;
                    placement.Normalize();
                    return true;
                default:
                    return placementResolver.TryApplyWorldPositionPreservingMode(placement, matrix.Translation);
            }
        }
        finally
        {
            ImGuizmo.SetID(-1);
        }
    }

    /// <summary>
    /// The width after a resize drag. The screen has one size setting, its width, so Resize shows only the
    /// width handle. ImGuizmo measures the drag from the size at its start, so its X scale is the new width.
    /// With snapping it lands on whole <see cref="ResizeSnapMeters"/> steps. Null for a broken scale.
    /// </summary>
    internal static float? ResizedWidth(float scaleX, bool snap)
    {
        if (!float.IsFinite(scaleX) || scaleX <= 0.0f)
            return null;

        var resized = snap ? MathF.Round(scaleX / ResizeSnapMeters) * ResizeSnapMeters : scaleX;
        return Math.Max(MinimumWidthMeters, resized);
    }

    private static unsafe bool Manipulate(GizmoOptions options, ref float view, ref float projection, ref float matrix, ref float snap)
    {
        var operation = options.Operation switch
        {
            ScreenPlacementGizmoOperation.Rotate => ImGuizmoOperation.Rotate,
            ScreenPlacementGizmoOperation.Resize => ImGuizmoOperation.ScaleX,
            _ => ImGuizmoOperation.Translate,
        };

        // Resizing is always along the screen's own sides.
        var mode = options.WorldAxes && options.Operation != ScreenPlacementGizmoOperation.Resize
            ? ImGuizmoMode.World
            : ImGuizmoMode.Local;

        fixed (float* nativeView = &view)
        fixed (float* nativeProjection = &projection)
        fixed (float* nativeMatrix = &matrix)
        fixed (float* nativeSnap = &snap)
        {
            return ImGuizmo.Manipulate(
                nativeView,
                nativeProjection,
                operation,
                mode,
                nativeMatrix,
                null,
                nativeSnap,
                null,
                null);
        }
    }

    private static unsafe bool TryGetCameraMatrices(out Matrix4x4 view, out Matrix4x4 projection)
    {
        var control = Control.Instance();
        var camera = control->CameraManager.GetActiveCamera();
        var renderCamera = camera != null ? camera->SceneCamera.RenderCamera : null;
        if (renderCamera == null)
        {
            view = default;
            projection = default;
            return false;
        }

        // The render camera's own view with its steady projection, as CrystalRenderer draws with. The gameplay
        // view-projection carries the anti-aliasing jitter, which shifted the gizmo's mouse ray every frame.
        projection = renderCamera->ProjectionMatrix;
        view = renderCamera->ViewMatrix;
        view.M44 = 1.0f;
        if (!Matrix4x4.Invert(view * projection, out _))
        {
            view = default;
            projection = default;
            return false;
        }

        var near = renderCamera->NearPlane;
        var far = renderCamera->FarPlane;
        if (float.IsFinite(near) && float.IsFinite(far) && far > near)
        {
            var clip = far / (far - near);
            projection.M43 = -(clip * near);
            projection.M33 = -((far + near) / (far - near));
        }

        return true;
    }
}
