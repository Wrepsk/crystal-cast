namespace CrystalRenderer;

/// <summary>Scene occlusion and camera-depth fading for a textured screen surface.</summary>
public readonly record struct ScreenAppearance
{
    public float OccludedAlpha { get; init; }
    public float OcclusionTolerance { get; init; } = 0.02f;
    public float FadeStart { get; init; } = float.PositiveInfinity;
    public float FadeStop { get; init; } = float.PositiveInfinity;

    public ScreenAppearance() { }
}
