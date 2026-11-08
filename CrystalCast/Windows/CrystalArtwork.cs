using System.Numerics;
using Dalamud.Bindings.ImGui;
using static CrystalCast.Windows.CrystalCastUiTheme;

namespace CrystalCast.Windows;

/// <summary>
/// The crystal-over-water artwork behind the player title and in screen thumbnails, drawn with the draw list.
/// While playing it slowly pushes in, the crystal floats and motes rise; stopped, it is a still frame.
/// </summary>
internal static class CrystalArtwork
{
    private const float ViewWidth = 540.0f;
    private const float ViewHeight = 260.0f;

    private static readonly Vector2[] BackRidge =
    [
        new(0, 178), new(58, 156), new(106, 168), new(164, 132), new(222, 162), new(280, 140),
        new(330, 152), new(388, 122), new(440, 148), new(494, 130), new(540, 142),
    ];

    private static readonly Vector2[] FrontRidge =
    [
        new(0, 198), new(66, 178), new(124, 190), new(182, 168), new(240, 186), new(306, 174),
        new(364, 188), new(420, 166), new(480, 182), new(540, 174),
    ];

    private static readonly Vector2[] Shore =
    [
        new(0, 206), new(48, 198), new(116, 204), new(172, 194), new(250, 202), new(318, 196),
        new(404, 204), new(482, 198), new(540, 202),
    ];

    private static readonly Vector2[] Stars =
    [
        new(46, 28), new(150, 20), new(232, 52), new(466, 24), new(512, 70),
    ];

    /// <summary>Full artwork for the player header.</summary>
    public static void DrawHeader(ImDrawListPtr dl, Vector2 min, Vector2 max, bool playing, float seed = 0.0f)
    {
        var time = (float)ImGui.GetTime();
        var push = playing ? 0.5f - (0.5f * MathF.Cos(time * MathF.PI / 30.0f)) : 0.0f;
        var zoom = 1.0f + (0.06f * push);
        var pan = new Vector2(-8.0f, -3.0f) * push;
        var map = CreateMap(min, max, zoom, pan, new Vector2(0.7f, 0.45f));

        dl.PushClipRect(min, max, true);
        DrawScene(dl, min, max, map, time, playing, seed, detailed: true);
        dl.PopClipRect();
    }

    /// <summary>Static small artwork for a screen thumbnail.</summary>
    public static void DrawThumbnail(ImDrawListPtr dl, Vector2 min, Vector2 max, float seed)
    {
        var map = CreateMap(min, max, 1.0f, Vector2.Zero, new Vector2(0.5f, 0.5f));
        DrawScene(dl, min, max, map, 0.0f, playing: false, seed, detailed: false);
    }

    /// <summary>A small inline crystal used as a picker prefix (32 × 18).</summary>
    public static void DrawChip(ImDrawListPtr dl, Vector2 min, Vector2 max, float seed)
    {
        dl.PushClipRect(min, max, true);
        DrawThumbnail(dl, min, max, seed);
        dl.PopClipRect();
    }

    /// <summary>The CrystalCast logo mark: an accent diamond with two facet lines, 12 × 16.</summary>
    public static void DrawLogo(ImDrawListPtr dl, Vector2 center)
    {
        var s = S(1.0f);
        var top = center + new Vector2(0, -7.5f * s);
        var right = center + new Vector2(4.5f * s, -2.5f * s);
        var bottom = center + new Vector2(0, 7.5f * s);
        var left = center + new Vector2(-4.5f * s, -2.5f * s);
        dl.AddQuadFilled(top, right, bottom, left, U32(Accent));
        dl.AddLine(left, right, U32(Base), 0.9f * s);
        dl.AddLine(top, bottom, U32(Base), 0.9f * s);
    }

    public static float SeedFor(string id)
    {
        unchecked
        {
            var hash = 17;
            foreach (var c in id)
                hash = (hash * 31) + c;
            return (hash & 0xFFFF) / 65535.0f;
        }
    }

    private static Func<Vector2, Vector2> CreateMap(Vector2 min, Vector2 max, float zoom, Vector2 pan, Vector2 origin)
    {
        var size = max - min;
        var scale = Math.Max(size.X / ViewWidth, size.Y / ViewHeight) * zoom;
        var offset = min + ((size - (new Vector2(ViewWidth, ViewHeight) * scale)) * 0.5f);
        var pivot = new Vector2(ViewWidth * origin.X, ViewHeight * origin.Y);
        return p => offset + ((pivot + ((p - pivot) * 1.0f) + pan) * scale);
    }

    private static void DrawScene(ImDrawListPtr dl, Vector2 min, Vector2 max, Func<Vector2, Vector2> map, float time, bool playing, float seed, bool detailed)
    {
        // Sky: #070B0D → #0D2125 (55%) → #18403F (80%) → #0C1416.
        var top = map(new Vector2(0, 0)).Y;
        var bottom = map(new Vector2(0, ViewHeight)).Y;
        GradientBand(dl, min.X, max.X, Math.Min(min.Y, top), Lerp(top, bottom, 0.55f), Hex(0x070B0D), Hex(0x0D2125));
        GradientBand(dl, min.X, max.X, Lerp(top, bottom, 0.55f), Lerp(top, bottom, 0.8f), Hex(0x0D2125), Hex(0x18403F));
        GradientBand(dl, min.X, max.X, Lerp(top, bottom, 0.8f), Math.Max(max.Y, bottom), Hex(0x18403F), Hex(0x0C1416));

        var crystalX = detailed ? 378.0f : 300.0f + (seed * 120.0f);
        var crystalY = detailed ? 104.0f : 96.0f;
        var crystalScale = detailed ? 1.0f : 1.55f;
        var pixel = (map(new Vector2(1, 0)) - map(Vector2.Zero)).X;

        if (detailed)
        {
            foreach (var star in Stars)
                dl.AddCircleFilled(map(star), Math.Max(0.6f, 0.9f * pixel), U32(Hex(0xE9F3F2, 0.6f)));

            var haloCenter = map(new Vector2(crystalX, 109));
            for (var i = 0; i < 14; i++)
            {
                var radius = (150.0f - (i * 10.0f)) * pixel;
                dl.AddCircleFilled(haloCenter, radius, U32(WithAlpha(Accent, 0.019f)), 48);
            }
        }

        var ridgeShift = detailed ? 0.0f : (seed - 0.5f) * 30.0f;
        FillRidge(dl, map, BackRidge, ViewHeight, Hex(0x132324), ridgeShift);
        FillRidge(dl, map, FrontRidge, ViewHeight, Hex(0x0E191A), -ridgeShift * 0.6f);

        // Water and shimmer.
        var waterTop = map(new Vector2(0, 206)).Y;
        GradientBand(dl, min.X, max.X, waterTop, Math.Max(max.Y, bottom), Hex(0x123334), Hex(0x0A1012));
        if (detailed)
        {
            var shimmer = playing ? 0.25f + (0.35f * (0.5f + (0.5f * MathF.Sin(time * MathF.Tau / 5.0f)))) : 0.4f;
            var shimmerColor = U32(WithAlpha(Accent, shimmer));
            dl.AddLine(map(new Vector2(356, 218)), map(new Vector2(400, 218)), shimmerColor, 1.2f * pixel);
            dl.AddLine(map(new Vector2(366, 228)), map(new Vector2(392, 228)), shimmerColor, 1.2f * pixel);
            dl.AddLine(map(new Vector2(372, 238)), map(new Vector2(386, 238)), shimmerColor, 1.2f * pixel);
        }

        FillRidge(dl, map, Shore, 212.0f, Hex(0x0A1112), 0.0f);

        var floatY = playing ? 5.0f * MathF.Sin(time * MathF.Tau / 8.0f) * -0.5f - 2.5f : 0.0f;
        DrawCrystal(dl, map, new Vector2(crystalX, crystalY + floatY), crystalScale, glow: detailed, pixel);

        if (detailed && playing)
        {
            DrawMote(dl, map, 350, 206, 1.2f, 9.0f, 0.0f, time, pixel);
            DrawMote(dl, map, 392, 210, 0.9f, 11.0f, -4.0f, time, pixel);
            DrawMote(dl, map, 412, 204, 1.3f, 10.0f, -7.0f, time, pixel);
            DrawMote(dl, map, 370, 212, 0.8f, 12.0f, -2.0f, time, pixel);
        }
    }

    private static void DrawCrystal(ImDrawListPtr dl, Func<Vector2, Vector2> map, Vector2 at, float scale, bool glow, float pixel)
    {
        Vector2 P(float x, float y) => map(at + (new Vector2(x, y) * scale));

        if (glow)
        {
            var center = P(0, 5);
            for (var i = 0; i < 10; i++)
                dl.AddCircleFilled(center, (54.0f - (i * 4.5f)) * pixel * scale, U32(WithAlpha(Accent, 0.03f)), 40);
        }

        var faceTop = Mix(Hex(0xE2FBF8), Accent, 0.45f);
        var faceLeft = Mix(Accent, Hex(0x0E3533), 0.45f);
        var faceRight = Mix(Hex(0x7FDCD3), Hex(0x103B39), 0.55f);

        dl.AddQuadFilled(P(0, -50), P(19, -12), P(0, 0), P(-19, -12), U32(faceTop));
        dl.AddQuadFilled(P(-19, -12), P(0, 0), P(0, 54), P(-15, 15), U32(faceLeft));
        dl.AddQuadFilled(P(19, -12), P(15, 15), P(0, 54), P(0, 0), U32(faceRight));

        var edge = U32(Hex(0xE9FFFC, 0.45f));
        var thickness = Math.Max(0.8f, 0.8f * pixel * scale);
        CrystalCastUiWidgets.Polyline(dl, [P(0, -50), P(19, -12), P(15, 15), P(0, 54), P(-15, 15), P(-19, -12)], edge, thickness);
        dl.AddLine(P(-19, -12), P(0, 0), edge, thickness);
        dl.AddLine(P(0, 0), P(19, -12), edge, thickness);
        dl.AddLine(P(0, 0), P(0, 54), edge, thickness);
    }

    private static void DrawMote(ImDrawListPtr dl, Func<Vector2, Vector2> map, float x, float y, float radius, float period, float delay, float time, float pixel)
    {
        var t = (((time - delay) % period) + period) % period / period;
        var alpha = t < 0.2f ? t / 0.2f * 0.8f : 0.8f * (1.0f - ((t - 0.2f) / 0.8f));
        dl.AddCircleFilled(map(new Vector2(x, y - (110.0f * t))), radius * pixel, U32(WithAlpha(Accent, alpha)));
    }

    private static void FillRidge(ImDrawListPtr dl, Func<Vector2, Vector2> map, Vector2[] ridge, float baseY, Vector4 color, float shift)
    {
        var col = U32(color);
        for (var i = 0; i < ridge.Length - 1; i++)
        {
            var a = ridge[i] + new Vector2(0, i % 2 == 0 ? shift : -shift);
            var b = ridge[i + 1] + new Vector2(0, (i + 1) % 2 == 0 ? shift : -shift);
            dl.AddQuadFilled(map(a), map(b), map(new Vector2(b.X, baseY)), map(new Vector2(a.X, baseY)), col);
        }
    }

    private static void GradientBand(ImDrawListPtr dl, float left, float right, float top, float bottom, Vector4 from, Vector4 to)
    {
        if (bottom <= top)
            return;
        var a = U32(from);
        var b = U32(to);
        dl.AddRectFilledMultiColor(new Vector2(left, top), new Vector2(right, bottom + 0.5f), a, a, b, b);
    }

    private static float Lerp(float a, float b, float t) => a + ((b - a) * t);
}
