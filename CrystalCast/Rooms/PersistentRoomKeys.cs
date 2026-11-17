using System.Security.Cryptography;
using System.Text;

namespace CrystalCast.Rooms;

/// <summary>
/// The secret that identifies this CrystalCast in persistent rooms, room passwords, and the list of rooms
/// you're in.
/// </summary>
internal static class RoomMemberKeys
{
    /// <summary>Most persistent rooms remembered at once.</summary>
    public const int MaxRooms = 20;

    public static string Create() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();

    /// <summary>Matches what the relay accepts: 32 to 128 lowercase hex digits.</summary>
    public static bool IsValid(string? key) =>
        key is { Length: >= 32 and <= 128 } && key.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    /// <summary>
    /// What travels to the relay instead of the password itself, which the relay hashes again with its own salt.
    /// Empty stays empty: no password.
    /// </summary>
    public static string HashPassword(string? password)
    {
        if (string.IsNullOrEmpty(password))
            return string.Empty;

        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"crystalcast-room-password\n{password}"));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    /// <summary>Drops entries with bad codes and repeats, keeping the first, and caps the list.</summary>
    public static List<PersistentRoomEntry> CleanRoomList(IEnumerable<PersistentRoomEntry>? entries)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var cleaned = new List<PersistentRoomEntry>();
        foreach (var entry in entries ?? [])
        {
            if (entry == null || !RoomCode.TryNormalize(entry.Code, out var code) || !seen.Add(code))
                continue;

            entry.Code = code;
            entry.Name ??= string.Empty;
            cleaned.Add(entry);
            if (cleaned.Count == MaxRooms)
                break;
        }

        return cleaned;
    }

    /// <summary>
    /// The local screen id for a copy of someone's shared screen, so the same screen always updates the same
    /// copy. Hashed: the parts come from other people and could be anything.
    /// </summary>
    public static string MirrorScreenId(string code, string owner, string screenId)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{owner}\n{screenId}"));
        return $"proom-{code}-{Convert.ToHexString(hash, 0, 8).ToLowerInvariant()}";
    }
}
