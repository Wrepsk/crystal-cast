namespace CrystalCast.Twitch;

/// <summary>One badge on a chat line: its set, such as "subscriber", and the version, such as "12".</summary>
internal readonly record struct TwitchBadge(string Set, string Version);

/// <summary>Where a badge's picture is, and its name for the tooltip.</summary>
internal readonly record struct TwitchBadgeImage(string Url, string Title);

/// <summary>
/// Turns chat badges into pictures. Twitch's badge lists need a login, so the common badges are built in and
/// work logged out; once logged in, Twitch's full global list and the channel's own badges (custom sub and
/// bits badges) replace them.
/// </summary>
internal sealed class TwitchBadgeCatalog
{
    private const string BadgeBase = "https://static-cdn.jtvnw.net/badges/v1/";

    /// <summary>The common global badges, by set. Their pictures are the same for every version.</summary>
    private static readonly Dictionary<string, TwitchBadgeImage> BuiltIn = new(StringComparer.Ordinal)
    {
        ["broadcaster"] = Image("5527c58c-fb7d-422d-b71b-f309dcb85cc1", "Broadcaster"),
        ["moderator"] = Image("3267646d-33f0-4b17-b3df-f923a41db1d0", "Moderator"),
        ["vip"] = Image("b817aba4-fad8-49e2-b88a-7cc744dfa6ec", "VIP"),
        ["partner"] = Image("d12a2e27-16f6-41d0-ab77-b780518f00a3", "Verified"),
        ["staff"] = Image("d97c37bd-a6f5-4c38-8f57-4e4bef88af34", "Staff"),
        ["admin"] = Image("9ef7e029-4cdf-4d4d-a0d5-e2b3fb2583fe", "Admin"),
        ["global_mod"] = Image("9384c43e-4ce7-4e94-b2a1-b93656896eba", "Global Moderator"),
        ["turbo"] = Image("bd444ec6-8f34-4bf9-91f4-af1e3428d80f", "Turbo"),
        ["premium"] = Image("bbbe0db0-a598-423e-86d0-f9fb98ca1933", "Prime Gaming"),
        ["subscriber"] = Image("5d9f2208-5dd8-11e7-8513-2ff4adfae661", "Subscriber"),
        ["founder"] = Image("511b78a9-ab37-472f-9569-457753bbe7d3", "Founder"),
        ["artist-badge"] = Image("4300a897-03dc-4e83-8c0e-c332fee7057f", "Artist"),
    };

    private Dictionary<(string Set, string Version), TwitchBadgeImage> global = [];
    private Dictionary<(string Set, string Version), TwitchBadgeImage> channel = [];

    /// <summary>The channel whose badges are loaded (its user ID), or empty.</summary>
    public string ChannelId { get; private set; } = string.Empty;

    public bool HasGlobal => global.Count > 0;

    public void SetGlobal(IEnumerable<(TwitchBadge Badge, TwitchBadgeImage Image)> badges)
    {
        global = badges.ToDictionary(b => (b.Badge.Set, b.Badge.Version), b => b.Image);
    }

    public void SetChannel(string channelId, IEnumerable<(TwitchBadge Badge, TwitchBadgeImage Image)> badges)
    {
        ChannelId = channelId;
        channel = badges.ToDictionary(b => (b.Badge.Set, b.Badge.Version), b => b.Image);
    }

    public void ClearChannel()
    {
        ChannelId = string.Empty;
        channel = [];
    }

    /// <summary>
    /// The channel's own badge first, then Twitch's global one, then the built-in picture for the set. Null
    /// for a badge with no known picture, which is then left out.
    /// </summary>
    public TwitchBadgeImage? Resolve(TwitchBadge badge)
    {
        if (channel.TryGetValue((badge.Set, badge.Version), out var own))
            return own;
        if (global.TryGetValue((badge.Set, badge.Version), out var known))
            return known;
        return BuiltIn.TryGetValue(badge.Set, out var builtIn) ? builtIn : null;
    }

    private static TwitchBadgeImage Image(string id, string title) => new($"{BadgeBase}{id}/2", title);
}
