using System.Numerics;

namespace CrystalCast.Rendering;

internal static class ScreenPanelSizeResolver
{
    private const float MinimumDimensionMeters = 0.01f;

    public static Vector2 Resolve(ScreenPlacementSettings placement, int sourceWidth, int sourceHeight)
    {
        var width = Math.Max(MinimumDimensionMeters, placement.WidthMeters);
        var height = Math.Max(MinimumDimensionMeters, placement.HeightMeters);
        if (sourceWidth > 0 && sourceHeight > 0)
            height = width * sourceHeight / sourceWidth;

        return new Vector2(width, height);
    }
}
