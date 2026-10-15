using System.Numerics;

namespace CrystalRenderer;

/// <summary>
/// A world-space textured rectangle. TextureHandle is a D3D11 shader-resource-view pointer.
/// Right and Down include the rectangle's full width and height. Submit retains the texture
/// for deferred rendering; the caller owns the original texture and its wrapper.
/// </summary>
public readonly record struct ScreenSurface(
    nint TextureHandle,
    Vector3 Center,
    Vector3 Right,
    Vector3 Down,
    Vector2 UvMin,
    Vector2 UvMax,
    ScreenAppearance Appearance)
{
    public ScreenSurface(nint textureHandle, Vector3 center, Vector3 right, Vector3 down, ScreenAppearance appearance)
        : this(textureHandle, center, right, down, Vector2.Zero, Vector2.One, appearance) { }

    internal void Validate()
    {
        if (TextureHandle == 0)
            throw new ArgumentException("A screen requires a D3D11 shader resource view.");
        if (!Finite(Center) || !Finite(Right) || !Finite(Down)
            || !float.IsFinite(UvMin.X) || !float.IsFinite(UvMin.Y)
            || !float.IsFinite(UvMax.X) || !float.IsFinite(UvMax.Y))
            throw new ArgumentException("Screen geometry and texture coordinates must be finite.");
        var area = Vector3.Cross(Right, Down).LengthSquared();
        if (!float.IsFinite(area) || area < 1e-8f)
            throw new ArgumentException("Screen axes must form a non-degenerate rectangle.");
        if (!float.IsFinite(Appearance.OccludedAlpha) || Appearance.OccludedAlpha is < 0 or > 1
            || !float.IsFinite(Appearance.OcclusionTolerance) || Appearance.OcclusionTolerance < 0
            || !ValidFade(Appearance.FadeStart) || !ValidFade(Appearance.FadeStop)
            || (float.IsFinite(Appearance.FadeStop) && Appearance.FadeStop <= Appearance.FadeStart))
            throw new ArgumentException("Screen occlusion and fade settings are invalid.");
    }

    private static bool Finite(Vector3 value)
        => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
    private static bool ValidFade(float value) => value >= 0 && !float.IsNaN(value);
}
