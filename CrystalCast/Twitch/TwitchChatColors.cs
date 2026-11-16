using System.Globalization;
using System.Numerics;

namespace CrystalCast.Twitch;

/// <summary>Name colors in chat, kept readable on CrystalCast's dark background.</summary>
internal static class TwitchChatColors
{
    /// <summary>Twitch's own picks for viewers who never chose a color.</summary>
    private static readonly uint[] Defaults =
    [
        0xFF0000, 0x0000FF, 0x008000, 0xB22222, 0xFF7F50, 0x9ACD32, 0xFF4500, 0x2E8B57,
        0xDAA520, 0xD2691E, 0x5F9EA0, 0x1E90FF, 0xFF69B4, 0x8A2BE2, 0x00FF7F,
    ];

    /// <summary>The lightness a name color is raised to at least, so dark blues stay readable.</summary>
    internal const float MinimumLuminance = 0.35f;

    public static Vector4 NameColor(string login, string hex)
    {
        var rgb = TryParseHex(hex, out var parsed) ? parsed : Defaults[StableHash(login) % (uint)Defaults.Length];
        return Readable(new Vector4(
            ((rgb >> 16) & 0xFF) / 255.0f,
            ((rgb >> 8) & 0xFF) / 255.0f,
            (rgb & 0xFF) / 255.0f,
            1.0f));
    }

    internal static bool TryParseHex(string hex, out uint rgb)
    {
        rgb = 0;
        return hex is ['#', _, _, _, _, _, _]
            && uint.TryParse(hex.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out rgb);
    }

    /// <summary>Mixes a too-dark color toward white until it reaches <see cref="MinimumLuminance"/>.</summary>
    internal static Vector4 Readable(Vector4 color)
    {
        var luminance = Luminance(color);
        if (luminance >= MinimumLuminance)
            return color;

        // Luminance is linear in the mix toward white, so one step lands on the target.
        var t = (MinimumLuminance - luminance) / (1.0f - luminance);
        return Vector4.Lerp(color, Vector4.One, t) with { W = 1.0f };
    }

    internal static float Luminance(Vector4 color) => (0.2126f * color.X) + (0.7152f * color.Y) + (0.0722f * color.Z);

    /// <summary>The same number for the same login on every run, unlike string.GetHashCode.</summary>
    private static uint StableHash(string text)
    {
        var hash = 2166136261u;
        foreach (var c in text)
            hash = (hash ^ c) * 16777619u;
        return hash;
    }
}
