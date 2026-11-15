using System.Numerics;

namespace CrystalCast.Rendering;

/// <summary>
/// Projects a flat world polygon to screen pixels, cutting off the part behind the camera. Points past the
/// screen's edges are kept (the draw list clips them), so a large shape still shows the part in view.
/// </summary>
internal static class PolygonProjection
{
    /// <summary>Clip-space w below this counts as behind the camera.</summary>
    private const float MinimumW = 0.001f;

    /// <summary>
    /// Fills <paramref name="output"/> with the visible outline in pixels (row-vector matrices, as System.Numerics
    /// uses). Empty when the whole polygon is behind the camera.
    /// </summary>
    public static void Project(
        ReadOnlySpan<Vector3> corners,
        Matrix4x4 viewProjection,
        Vector2 viewportPosition,
        Vector2 viewportSize,
        List<Vector2> output)
    {
        output.Clear();
        if (corners.Length < 3)
            return;

        Span<Vector4> clip = stackalloc Vector4[corners.Length];
        for (var i = 0; i < corners.Length; i++)
            clip[i] = Vector4.Transform(new Vector4(corners[i], 1.0f), viewProjection);

        // One Sutherland-Hodgman pass against the plane in front of the camera.
        for (var i = 0; i < clip.Length; i++)
        {
            var current = clip[i];
            var next = clip[(i + 1) % clip.Length];
            var currentIn = current.W >= MinimumW;
            var nextIn = next.W >= MinimumW;
            if (currentIn)
                output.Add(ToPixels(current, viewportPosition, viewportSize));

            if (currentIn != nextIn)
            {
                var t = (MinimumW - current.W) / (next.W - current.W);
                output.Add(ToPixels(Vector4.Lerp(current, next, t), viewportPosition, viewportSize));
            }
        }
    }

    private static Vector2 ToPixels(Vector4 clip, Vector2 viewportPosition, Vector2 viewportSize)
    {
        var ndcX = clip.X / clip.W;
        var ndcY = clip.Y / clip.W;
        return viewportPosition + new Vector2(
            (ndcX * 0.5f + 0.5f) * viewportSize.X,
            (0.5f - ndcY * 0.5f) * viewportSize.Y);
    }
}
