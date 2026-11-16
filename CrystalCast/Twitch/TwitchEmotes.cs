using System.Text.Json.Serialization;

namespace CrystalCast.Twitch;

/// <summary>An emote the user can send: typing its name in a message sends the emote.</summary>
internal sealed record TwitchEmote(string Id, string Name, string Type, string OwnerId);

/// <summary>
/// The emotes for the picker. <see cref="MissingScope"/> means the login predates the emote permission, so only
/// Twitch's global emotes could be listed until the user logs in again.
/// </summary>
internal sealed record TwitchEmoteList(IReadOnlyList<TwitchEmote> Emotes, bool MissingScope);

internal enum TwitchEmoteGroup
{
    /// <summary>The channel being watched: its subscriber and follower emotes.</summary>
    ThisChannel,
    /// <summary>Other channels' emotes the user has, from subscriptions, Bits and the like.</summary>
    OtherChannels,
    /// <summary>Twitch's emotes everyone can use.</summary>
    Global,
}

/// <summary>Sorting and searching the picker's emotes.</summary>
internal static class TwitchEmotePicker
{
    public const int MaxMessageLength = 500;

    /// <summary>Twitch's global emotes have no owner (or owner 0) and the type "globals".</summary>
    public static TwitchEmoteGroup GroupOf(TwitchEmote emote, string channelId)
    {
        if (channelId.Length > 0 && emote.OwnerId == channelId)
            return TwitchEmoteGroup.ThisChannel;

        return emote.Type == "globals" || emote.OwnerId is "" or "0" or "twitch"
            ? TwitchEmoteGroup.Global
            : TwitchEmoteGroup.OtherChannels;
    }

    /// <summary>
    /// The emotes matching <paramref name="search"/> (any part of the name, any case), in groups: this channel,
    /// other channels, then global, each by name. Duplicates (same name) keep the first.
    /// </summary>
    public static IReadOnlyList<(TwitchEmoteGroup Group, IReadOnlyList<TwitchEmote> Emotes)> Arrange(
        IEnumerable<TwitchEmote> emotes,
        string channelId,
        string search)
    {
        search = search.Trim();
        return emotes
            .Where(emote => search.Length == 0 || emote.Name.Contains(search, StringComparison.OrdinalIgnoreCase))
            .DistinctBy(emote => emote.Name, StringComparer.Ordinal)
            .GroupBy(emote => GroupOf(emote, channelId))
            .OrderBy(group => group.Key)
            .Select(group => (group.Key, (IReadOnlyList<TwitchEmote>)group.OrderBy(emote => emote.Name, StringComparer.OrdinalIgnoreCase).ToArray()))
            .ToArray();
    }

    /// <summary>
    /// The message with the emote's name added at the end, set apart by spaces so Twitch sees a whole word.
    /// Unchanged when it would go past Twitch's message length.
    /// </summary>
    public static string Insert(string message, string emoteName)
    {
        var separator = message.Length == 0 || message.EndsWith(' ') ? string.Empty : " ";
        var result = $"{message}{separator}{emoteName} ";
        return result.Length > MaxMessageLength ? message : result;
    }

    public static string Title(TwitchEmoteGroup group) => group switch
    {
        TwitchEmoteGroup.ThisChannel => "This channel",
        TwitchEmoteGroup.OtherChannels => "Other channels",
        _ => "Global",
    };
}

internal sealed class HelixEmotePage
{
    [JsonPropertyName("data")] public List<HelixEmote>? Data { get; set; }
    [JsonPropertyName("pagination")] public HelixPagination? Pagination { get; set; }
}

internal sealed class HelixEmote
{
    [JsonPropertyName("id")] public string? Id { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("emote_type")] public string? EmoteType { get; set; }
    [JsonPropertyName("owner_id")] public string? OwnerId { get; set; }
}

internal sealed class HelixPagination
{
    [JsonPropertyName("cursor")] public string? Cursor { get; set; }
}
