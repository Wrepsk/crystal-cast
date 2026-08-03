using System.Numerics;

namespace CrystalCast.Windows;

internal static class CrystalCastUiTheme
{
    public static readonly Vector4 Accent = new(0.392f, 0.149f, 0.145f, 1.0f);
    public static readonly Vector4 AccentText = new(0.847f, 0.549f, 0.537f, 1.0f);
    public static readonly Vector4 AccentHover = new(0.490f, 0.212f, 0.204f, 1.0f);
    public static readonly Vector4 AccentActive = new(0.565f, 0.251f, 0.239f, 1.0f);

    public static Vector4 WithAlpha(Vector4 color, float alpha)
    {
        return new Vector4(color.X, color.Y, color.Z, alpha);
    }
}
