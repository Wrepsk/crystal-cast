namespace CrystalCast.Rooms;

/// <summary>
/// Room codes: eight characters without 0/O or 1/I/L, so they survive being read aloud. Shown as
/// "ABCD-EFGH"; typed codes may use any case, spaces or dashes. Must match the relay's alphabet.
/// </summary>
internal static class RoomCode
{
    public const string Alphabet = "23456789ABCDEFGHJKMNPQRSTUVWXYZ";
    public const int Length = 8;

    public static bool TryNormalize(string? input, out string code)
    {
        code = string.Empty;
        if (string.IsNullOrWhiteSpace(input))
            return false;

        var characters = input
            .Where(character => !char.IsWhiteSpace(character) && character != '-')
            .Select(char.ToUpperInvariant)
            .ToArray();
        if (characters.Length != Length || characters.Any(character => !Alphabet.Contains(character)))
            return false;

        code = new string(characters);
        return true;
    }

    public static string Format(string code) =>
        code.Length == Length ? $"{code[..4]}-{code[4..]}" : code;

    /// <summary>The joined screen's id, so the same room always updates the same screen.</summary>
    public static string ScreenIdFor(string code) => $"room-{code}";
}
