using System.Numerics;

namespace CrystalRenderer.Integration;

internal readonly record struct CameraMatrices(
    Matrix4x4 ViewProjection,
    Matrix4x4 DepthViewProjection,
    Vector2 DepthCoefficients);

internal static class CameraProjection
{
    internal static bool TryCreate(Matrix4x4 view, Matrix4x4 projection, Matrix4x4 depthProjection,
        out CameraMatrices matrices)
    {
        matrices = default;
        if (!IsFinite(view) || !IsFinite(projection) || !IsFinite(depthProjection))
            return false;
        // The render view stores an affine transform; supply its homogeneous element.
        view.M44 = 1f;
        var viewProjection = view * projection;
        var depthViewProjection = view * depthProjection;
        if (!IsFinite(viewProjection) || !IsFinite(depthViewProjection)
            || !Matrix4x4.Invert(viewProjection, out _) || !Matrix4x4.Invert(depthViewProjection, out _))
            return false;
        // ProjectionMatrix is steady; ProjectionMatrix2 includes the current scene jitter.
        // Preserve both as supplied, including lens shifts and the game's depth convention.
        matrices = new CameraMatrices(viewProjection, depthViewProjection, new Vector2(depthProjection.M33, depthProjection.M43));
        return true;
    }

    private static bool IsFinite(Matrix4x4 matrix)
        => float.IsFinite(matrix.M11) && float.IsFinite(matrix.M12) && float.IsFinite(matrix.M13) && float.IsFinite(matrix.M14)
        && float.IsFinite(matrix.M21) && float.IsFinite(matrix.M22) && float.IsFinite(matrix.M23) && float.IsFinite(matrix.M24)
        && float.IsFinite(matrix.M31) && float.IsFinite(matrix.M32) && float.IsFinite(matrix.M33) && float.IsFinite(matrix.M34)
        && float.IsFinite(matrix.M41) && float.IsFinite(matrix.M42) && float.IsFinite(matrix.M43) && float.IsFinite(matrix.M44);
}
