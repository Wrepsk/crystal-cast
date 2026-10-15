namespace CrystalRenderer;

public sealed record ScreenRendererOptions
{
    /// <summary>Maximum rectangles per frame, including individual curved-screen strips.</summary>
    public int MaxSurfaces { get; init; } = 256;

    internal void Validate()
    {
        if (MaxSurfaces is < 1 or > 8192)
            throw new ArgumentOutOfRangeException(nameof(MaxSurfaces), "Surface capacity must be between 1 and 8192.");
    }
}
