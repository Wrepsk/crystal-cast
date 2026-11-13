using System.Text.Json.Serialization;
using CrystalCast.Ipc;

namespace CrystalCast.Rooms;

/// <summary>
/// Where the host is. A joined screen shows when the viewer is in the same copy of the same area: same world,
/// zone and instance, and in housing the same ward and subdivision, or indoors the same house or apartment.
/// Housing reuses one zone, with identical coordinates, for every ward, both halves of a ward and every house
/// of a size, so the zone alone is not enough; outdoors the plot someone stands on doesn't matter.
/// </summary>
public sealed record RoomLocation
{
    public uint WorldId { get; init; }
    public ushort TerritoryId { get; init; }

    /// <summary>The instance of an instanced zone (busy cities and areas); 0 when the zone has only one.</summary>
    public uint Instance { get; init; }
    public sbyte Ward { get; init; } = -1;

    /// <summary>Indoors only: which house. Outdoors it would only say which plot someone stands on.</summary>
    public sbyte Plot { get; init; } = -1;

    /// <summary>Indoors only: the apartment or private room.</summary>
    public short Room { get; init; } = -1;

    /// <summary>Which half of the ward: the main division or the subdivision, a copy with the same coordinates.</summary>
    public byte Division { get; init; }
    public bool Indoors { get; init; }

    public bool IsKnown => WorldId != 0 && TerritoryId != 0;
    public bool InHousing => Ward >= 0;

    public bool SamePlaceAs(RoomLocation other)
    {
        if (!IsKnown || !other.IsKnown
            || WorldId != other.WorldId
            || TerritoryId != other.TerritoryId
            || Instance != other.Instance
            || InHousing != other.InHousing)
        {
            return false;
        }

        if (!InHousing)
            return true;

        if (Ward != other.Ward || Division != other.Division || Indoors != other.Indoors)
            return false;

        // Outdoors the whole ward half is one place; plot and room are ignored, also when an older version sent them.
        return !Indoors || Plot == other.Plot && Room == other.Room;
    }
}

/// <summary>Everything a viewer needs to show the host's screen. Sent by the host, passed on by the relay.</summary>
public sealed class RoomScreenState
{
    public int SchemaVersion { get; set; } = 1;
    public string Name { get; set; } = string.Empty;
    public RoomLocation Location { get; set; } = new();

    public float PositionX { get; set; }
    public float PositionY { get; set; }
    public float PositionZ { get; set; }
    public float YawRadians { get; set; }
    public float PitchRadians { get; set; }
    public float RollRadians { get; set; }
    public float WidthMeters { get; set; }
    public float HeightMeters { get; set; }
    public ScreenVisualState Visual { get; set; } = new();

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public BrowserSourceProviderKind Provider { get; set; }
    public string Url { get; set; } = string.Empty;

    public bool Paused { get; set; }

    /// <summary>Playback position when the host sent this message.</summary>
    public long PositionMs { get; set; }
    public float Rate { get; set; } = 1.0f;
    public bool Loop { get; set; }
    public bool PlaylistAutoplayNext { get; set; } = true;

    /// <summary>The host's playlist, so viewers see what's coming and can add to it when the host allows.</summary>
    public RoomPlaylistState Playlist { get; set; } = new();
}

public sealed class RoomPlaylistState
{
    /// <summary>Whether people in the room may add videos.</summary>
    public bool AllowAdds { get; set; }
    public bool Shuffle { get; set; }
    public bool Loop { get; set; }
    public string CurrentEntryId { get; set; } = string.Empty;
    public List<RoomPlaylistEntry> Entries { get; set; } = [];
}

/// <summary>
/// One video in the host's playlist. Only the link travels: each viewer's CrystalCast looks up the title,
/// channel, thumbnail and length itself, as it does for its own playlists.
/// </summary>
public sealed class RoomPlaylistEntry
{
    /// <summary>Tells entries apart when the same link is queued twice, and marks which one is playing.</summary>
    public string EntryId { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public bool Played { get; set; }
}

/// <summary>Messages from the relay. Which fields are set depends on <see cref="Type"/>.</summary>
public sealed class RoomRelayMessage
{
    public string Type { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;
    public string Code { get; set; } = string.Empty;

    /// <summary>The relay's clock when it sent this message.</summary>
    public long RelayTime { get; set; }

    /// <summary>The relay's clock when the host's state arrived; the state's position is from that moment.</summary>
    public long ReceivedAt { get; set; }
    public RoomScreenState? State { get; set; }
    public bool? HostConnected { get; set; }
    public bool? Connected { get; set; }
    public int? Viewers { get; set; }
    public int? Count { get; set; }
    public long T { get; set; }
    public string Reason { get; set; } = string.Empty;

    /// <summary>For the host: a link someone in the room asked to add to the playlist.</summary>
    public string Url { get; set; } = string.Empty;
}

public sealed class RoomCreateResponse
{
    public string Code { get; set; } = string.Empty;
    public string HostKey { get; set; } = string.Empty;
    public string Error { get; set; } = string.Empty;
}
