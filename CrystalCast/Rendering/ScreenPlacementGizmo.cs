using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Bindings.ImGuizmo;
using Dalamud.Interface.Utility;
using FFXIVClientStructs.FFXIV.Client.Game.Control;

namespace CrystalCast.Rendering;

internal static class ScreenPlacementGizmo
{
    public static bool Draw(ScreenPlacementSettings placement, ScreenPlacementGizmoOperation operation, ScreenPlacementResolver placementResolver)
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
                changed = DrawGizmo(placement, operation, resolved, ImGui.GetWindowPos(), io.DisplaySize, ref view, ref projection, placementResolver);
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
        ScreenPlacementGizmoOperation operation,
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

        var matrix = Matrix4x4.CreateFromQuaternion(resolved.Rotation);
        matrix.Translation = resolved.Position;
        var snap = Vector3.Zero;

        try
        {
            if (!Manipulate(operation, ref view.M11, ref projection.M11, ref matrix.M11, ref snap.X))
                return false;

            if (operation == ScreenPlacementGizmoOperation.Rotate)
            {
                return Matrix4x4.Decompose(matrix, out _, out var worldRotation, out _)
                    && placementResolver.TryApplyWorldRotationPreservingMode(placement, worldRotation);
            }

            return placementResolver.TryApplyWorldPositionPreservingMode(placement, matrix.Translation);
        }
        finally
        {
            ImGuizmo.SetID(-1);
        }
    }

    private static unsafe bool Manipulate(ScreenPlacementGizmoOperation operation, ref float view, ref float projection, ref float matrix, ref float snap)
    {
        fixed (float* nativeView = &view)
        fixed (float* nativeProjection = &projection)
        fixed (float* nativeMatrix = &matrix)
        fixed (float* nativeSnap = &snap)
        {
            return ImGuizmo.Manipulate(
                nativeView,
                nativeProjection,
                operation == ScreenPlacementGizmoOperation.Rotate ? ImGuizmoOperation.Rotate : ImGuizmoOperation.Translate,
                operation == ScreenPlacementGizmoOperation.Rotate ? ImGuizmoMode.Local : ImGuizmoMode.World,
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

        projection = renderCamera->ProjectionMatrix;
        if (!Matrix4x4.Invert(projection, out var inverseProjection))
        {
            view = default;
            projection = default;
            return false;
        }

        view = Matrix4x4.Multiply(control->ViewProjectionMatrix, inverseProjection);
        view.M44 = 1.0f;

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
