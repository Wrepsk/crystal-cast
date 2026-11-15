using System.Globalization;
using System.Text;

namespace CrystalCast.Rooms;

/// <summary>
/// The name others in a room see for you. It's chosen in Settings > Rooms and starts out random; it's never
/// taken from the character. Cleaned the same way the relay cleans it, so what you type is what others see.
/// </summary>
internal static class RoomUsername
{
    /// <summary>Matches the relay's limit, in characters (code points).</summary>
    public const int MaxLength = 24;

    /// <summary>What the relay shows for an empty name.</summary>
    public const string Fallback = "Guest";

    private static readonly string[] Adjectives =
    [
        "Brave", "Clever", "Cozy", "Gentle", "Lucky", "Merry", "Quiet", "Shiny", "Sleepy", "Swift",
        "Sunny", "Bold", "Calm", "Fluffy", "Jolly", "Mighty", "Noble", "Plucky", "Witty", "Zesty",
    ];

    private static readonly string[] Nouns =
    [
        "Moogle", "Chocobo", "Cactuar", "Tonberry", "Carbuncle", "Namazu", "Spriggan", "Bomb", "Mandragora", "Morbol",
        "Coeurl", "Goobbue", "Pudding", "Ahriman", "Adamantoise", "Behemoth", "Sprite", "Kupo", "Fat Cat", "Ochu",
    ];

    /// <summary>A name like "Sleepy Moogle 42".</summary>
    public static string Random(Random? random = null)
    {
        random ??= System.Random.Shared;
        return $"{Adjectives[random.Next(Adjectives.Length)]} {Nouns[random.Next(Nouns.Length)]} {random.Next(10, 100)}";
    }

    /// <summary>
    /// Whitespace (tabs and line breaks included) made single spaces, other control and formatting characters
    /// (such as text-direction marks) removed, trimmed and cut to <see cref="MaxLength"/> characters. Empty when
    /// nothing is left.
    /// </summary>
    public static string Clean(string? raw)
    {
        if (string.IsNullOrEmpty(raw))
            return string.Empty;

        var builder = new StringBuilder(raw.Length);
        var lastWasSpace = false;
        foreach (var rune in raw.EnumerateRunes())
        {
            // Whitespace first: tabs and line breaks are control characters too, but they separate words.
            if (Rune.IsWhiteSpace(rune))
            {
                if (!lastWasSpace)
                    builder.Append(' ');
                lastWasSpace = true;
                continue;
            }

            var category = Rune.GetUnicodeCategory(rune);
            if (category is UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.PrivateUse
                or UnicodeCategory.OtherNotAssigned or UnicodeCategory.Surrogate)
            {
                continue;
            }

            builder.Append(rune.ToString());
            lastWasSpace = false;
        }

        var trimmed = builder.ToString().Trim();
        var runes = trimmed.EnumerateRunes().Take(MaxLength).Select(rune => rune.ToString());
        return string.Concat(runes).Trim();
    }

    /// <summary>The name as the relay will show it.</summary>
    public static string Display(string? raw) => Clean(raw) is { Length: > 0 } name ? name : Fallback;
}
